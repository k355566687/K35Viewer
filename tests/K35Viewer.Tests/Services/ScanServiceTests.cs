using K35Viewer.Domain;
using K35Viewer.Services;

namespace K35Viewer.Tests.Services;

public class ScanServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AddAndGetIdAsync(LibraryTestContext ctx, string? relative = null)
    {
        var result = await ctx.Library.AddFolderAsync(relative is null ? ctx.Workspace : ctx.PathOf(relative), false, Ct);
        Assert.Equal(AddFolderOutcome.Added, result.Outcome);
        return result.LibraryFolderId!.Value;
    }

    [Fact]
    public async Task ScanAsync_初回スキャンでZIPとCBZを再帰的に登録し_件数がDBと一致する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"a.zip");
        ctx.CreateFile(@"b.CBZ");
        ctx.CreateFile(@"sub\c.zip");
        ctx.CreateFile(@"sub\deep\d.Zip");
        ctx.CreateFile(@"sub\readme.txt");
        ctx.CreateFile(@"sub\e.rar");
        var id = await AddAndGetIdAsync(ctx);

        var summary = await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        Assert.Equal(OperationStatus.Success, summary.Status);
        Assert.Equal(4, summary.Discovered);
        Assert.Equal(4, summary.Added);
        Assert.Equal(4, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(4, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE extension = 'cbz'"));
        Assert.Equal(3, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE extension = 'zip'"));
    }

    [Fact]
    public async Task ScanAsync_フォルダツリーが相対パスと深さつきで登録される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"sub\deep\d.zip");
        var id = await AddAndGetIdAsync(ctx);

        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        Assert.Equal(3, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders WHERE relative_path = 'sub\\deep' AND depth = 2 AND parent_folder_id IS NOT NULL"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders WHERE relative_path = '' AND depth = 0 AND parent_folder_id IS NULL"));
        // 子フォルダの親は1つ上のフォルダ
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders c JOIN folders p ON c.parent_folder_id = p.id " +
            "WHERE c.relative_path = 'sub\\deep' AND p.relative_path = 'sub'"));
    }

    [Fact]
    public async Task ScanAsync_変更がなければ再スキャンしても何も変わらずArchiveIDも維持される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        ctx.CreateFile(@"sub\b.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);
        var idsBefore = await ctx.ScalarAsync("SELECT SUM(id) FROM archives");

        var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        Assert.Equal(2, summary.Discovered);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Changed);
        Assert.Equal(0, summary.Missing);
        Assert.Equal(idsBefore, await ctx.ScalarAsync("SELECT SUM(id) FROM archives"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
    }

    [Fact]
    public async Task ScanAsync_追加_変更_削除を差分として反映する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("keep.zip");
        ctx.CreateFile("edit.zip", size: 10);
        ctx.CreateFile("gone.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);
        var editId = await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'edit.zip'");
        await ctx.ExecuteAsync(
            "INSERT INTO thumbnail_cache (archive_id, cache_key, status) VALUES (" + editId + ", 'k', 'READY')");

        ctx.CreateFile("new.zip");
        ctx.CreateFile("edit.zip", size: 99); // サイズ変更
        File.Delete(ctx.PathOf("gone.zip"));
        var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Changed);
        Assert.Equal(1, summary.Missing);
        Assert.Equal(OperationStatus.Success, summary.Status);
        Assert.Equal(99, await ctx.ScalarAsync("SELECT file_size FROM archives WHERE file_name = 'edit.zip'"));
        Assert.Equal(editId, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'edit.zip'"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM archives WHERE file_name = 'edit.zip' AND analysis_status = 'PENDING'"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM thumbnail_cache WHERE archive_id = " + editId + " AND status = 'STALE'"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM archives WHERE file_name = 'gone.zip' AND availability_status = 'MISSING'"));
        Assert.Equal(3, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
    }

    [Fact]
    public async Task ScanAsync_更新日時だけが変わった場合も変更として検出する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip", size: 10, lastWriteUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        ctx.CreateFile("a.zip", size: 10, lastWriteUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, summary.Added);
    }

    [Fact]
    public async Task ScanAsync_不在になったファイルが戻ると_同じArchiveIDで復活する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);
        var archiveId = await ctx.ScalarAsync("SELECT id FROM archives");
        File.Move(ctx.PathOf("a.zip"), ctx.PathOf("a.zip.bak"));
        await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);
        Assert.Equal(0, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));

        File.Move(ctx.PathOf("a.zip.bak"), ctx.PathOf("a.zip"));
        var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, summary.Added);
        Assert.Equal(archiveId, await ctx.ScalarAsync("SELECT id FROM archives"));
        Assert.Equal(1, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
    }

    [Fact]
    public async Task ScanAsync_フォルダごと消えた場合は配下のアーカイブとフォルダを不在にする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("top.zip");
        ctx.CreateFile(@"sub\x.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        Directory.Delete(ctx.PathOf("sub"), recursive: true);
        var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        Assert.Equal(1, summary.Missing);
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders WHERE relative_path = 'sub' AND availability_status = 'UNAVAILABLE'"));
        Assert.Equal(1, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
    }

    [Fact]
    public async Task ScanAsync_登録フォルダにアクセスできない場合は_何も削除せずFAILEDにする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        ctx.CreateFile(@"sub\b.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        // ネットワーク切断を模して、登録フォルダごと見えなくする(削除。接続が戻ったら同じ内容を再作成する)
        Directory.Delete(ctx.Workspace, recursive: true);
        var summary = await ctx.Scan.ScanAsync(id, ScanType.Startup, Ct);

        Assert.Equal(OperationStatus.Failed, summary.Status);
        Assert.Equal(0, summary.Missing);
        Assert.Equal(2, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE availability_status <> 'AVAILABLE'"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM library_folders WHERE availability_status = 'UNAVAILABLE'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs WHERE status = 'FAILED'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error);

        // 接続が戻ったら、同じ登録のまま通常どおり復帰する(同じサイズ・更新日時なので差分なし)
        ctx.CreateFile("a.zip");
        ctx.CreateFile(@"sub\b.zip");
        var recovered = await ctx.Scan.ScanAsync(id, ScanType.Startup, Ct);
        Assert.Equal(OperationStatus.Success, recovered.Status);
        Assert.Equal(0, recovered.Added);
        Assert.Equal(0, recovered.Missing);
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM library_folders WHERE availability_status = 'AVAILABLE'"));
    }

    [Fact]
    public async Task ScanAsync_読み取りを拒否されたフォルダの配下は不在にせず_一部失敗として扱う()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("top.zip");
        ctx.CreateFile(@"locked\x.zip");
        ctx.CreateFile(@"locked\inner\y.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        var locked = new DirectoryInfo(ctx.PathOf("locked"));
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

            Assert.Equal(OperationStatus.Partial, summary.Status);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Missing);
            // 読めなかった範囲(locked と その配下 inner)の登録は、そのまま残る
            Assert.Equal(3, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
            Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders WHERE availability_status = 'UNAVAILABLE'"));
            Assert.Equal(NotificationLevel.Warning, ctx.Notifications.Items[^1].Level);
        }
        finally
        {
            security = locked.GetAccessControl();
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }

        var recovered = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);
        Assert.Equal(OperationStatus.Success, recovered.Status);
        Assert.Equal(0, recovered.Missing);
        Assert.Equal(3, await ctx.ArchiveRepository.CountAvailableAsync(id, Ct));
    }

    [Fact]
    public async Task ScanAsync_開始と完了を通知し_最終結果だけを履歴に記録する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        var id = await AddAndGetIdAsync(ctx);

        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        Assert.Equal(2, ctx.Notifications.Items.Count);
        Assert.Equal(NotificationLevel.Info, ctx.Notifications.Items[0].Level);
        Assert.Contains("開始", ctx.Notifications.Items[0].Message, StringComparison.Ordinal);
        Assert.Equal(NotificationLevel.Success, ctx.Notifications.Items[1].Level);
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'SCAN_INITIAL' AND status = 'SUCCESS' AND affected_count = 1"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs WHERE status = 'SUCCESS' AND added_count = 1"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM library_folders WHERE last_scan_at IS NOT NULL AND availability_status = 'AVAILABLE'"));
    }

    [Fact]
    public async Task ScanAsync_起動時チェックで差分がなければ_最新であることを通知する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);
        ctx.Notifications.Items.Clear();

        await ctx.Scan.ScanAsync(id, ScanType.Startup, Ct);

        Assert.Equal("ライブラリの更新を確認しています", ctx.Notifications.Items[0].Message);
        Assert.Equal("ライブラリは最新です", ctx.Notifications.Items[1].Message);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'SCAN_STARTUP'"));
    }

    [Fact]
    public async Task ScanAsync_キャンセル済みのトークンでは何も登録せずに中断し_RUNNINGを残さない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        var id = await AddAndGetIdAsync(ctx);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctx.Scan.ScanAsync(id, ScanType.Manual, cts.Token));

        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs WHERE status = 'RUNNING'"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
    }

    [Fact]
    public async Task ScanAsync_1万件でも初回と差分スキャンが完了し_件数がDBと一致する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var folder = 0; folder < 100; folder++)
        {
            for (var file = 0; file < 100; file++)
            {
                ctx.CreateFile(Path.Combine($"f{folder:D3}", $"book{file:D3}.zip"), size: 1);
            }
        }

        var id = await AddAndGetIdAsync(ctx);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var first = await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);
        var firstElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        var second = await ctx.Scan.ScanAsync(id, ScanType.Startup, Ct);
        var secondElapsed = stopwatch.Elapsed;

        TestContext.Current.SendDiagnosticMessage(
            $"10,000件: 初回スキャン {firstElapsed.TotalSeconds:F2}秒 / 差分スキャン(変更なし) {secondElapsed.TotalSeconds:F2}秒");
        Assert.Equal(10_000, first.Added);
        Assert.Equal(10_000, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(101, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders"));
        Assert.Equal(0, second.Added + second.Changed + second.Missing);
        // 極端な性能劣化(桁違いの遅さ)だけを検知するための緩い上限
        Assert.True(firstElapsed < TimeSpan.FromSeconds(60), $"初回スキャンが遅すぎます: {firstElapsed}");
        Assert.True(secondElapsed < TimeSpan.FromSeconds(60), $"差分スキャンが遅すぎます: {secondElapsed}");
    }

    [Fact]
    public async Task ScanAsync_大文字小文字の違うフォルダ名でも同じフォルダとして扱う()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"Sub\a.zip");
        var id = await AddAndGetIdAsync(ctx);
        await ctx.Scan.ScanAsync(id, ScanType.Initial, Ct);

        // 名前の大文字小文字だけを変える(Windowsでは同じフォルダ)
        Directory.Move(ctx.PathOf("Sub"), ctx.PathOf("Sub_tmp"));
        Directory.Move(ctx.PathOf("Sub_tmp"), ctx.PathOf("SUB"));
        var summary = await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Missing);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
    }
}
