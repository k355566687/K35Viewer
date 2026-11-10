using System.Security.Cryptography;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

/// <summary>アーカイブ自体の名前変更・移動・削除・エクスプローラーで表示（UC-12〜15）。</summary>
public class ArchiveFileOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Prepared(long LibraryId, long ArchiveId, string ZipPath);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// a.zip(2頁)、sub\b.zip、空のフォルダ dest を作り、登録・スキャン・サムネイルの作成まで済ませる。対象は a.zip。
    /// </summary>
    private static async Task<Prepared> PrepareAsync(LibraryTestContext ctx)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)));
        ctx.CreateZip(@"sub\b.zip", ("1.jpg", TestImages.Jpeg(120, 80)));
        _ = Directory.CreateDirectory(ctx.PathOf("dest"));
        var libraryId = await ctx.RegisterAndScanAsync();
        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var id = await ctx.ScalarAsync("SELECT a.id FROM archives a WHERE a.file_name = 'a.zip'");
        return new Prepared(libraryId, id, ctx.PathOf("a.zip"));
    }

    private static Task<long> HistoryCountAsync(LibraryTestContext ctx, string type, string status) =>
        ctx.ScalarAsync($"SELECT COUNT(*) FROM operation_history WHERE operation_type = '{type}' AND status = '{status}'");

    // ---- 名前の検証 ----

    [Theory]
    [InlineData("new", "new.zip")]
    [InlineData("  new  ", "new.zip")]
    [InlineData("new.zip", "new.zip")]
    [InlineData("NEW.CBZ", "NEW.CBZ")]
    [InlineData("v1.2", "v1.2.zip")]
    [InlineData("第1巻 前編", "第1巻 前編.zip")]
    public void ValidateFileName_拡張子が_zipまたはcbzでなければ_元の拡張子を付ける(string input, string expected)
    {
        var result = ArchiveService.ValidateFileName("old.zip", input);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.FileName);
    }

    [Fact]
    public void ValidateFileName_元の拡張子がcbzなら_省略時はcbzを付ける()
    {
        Assert.Equal("new.cbz", ArchiveService.ValidateFileName("old.cbz", "new").FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".zip")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b>")]
    [InlineData("a|b")]
    [InlineData("name.")]
    [InlineData("CON")]
    [InlineData("nul.zip")]
    [InlineData("COM1")]
    public void ValidateFileName_使えない名前は_理由つきで断る(string input)
    {
        var result = ArchiveService.ValidateFileName("old.zip", input);

        Assert.False(result.IsValid);
        Assert.Null(result.FileName);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void ValidateFileName_長すぎる名前は断る()
    {
        var result = ArchiveService.ValidateFileName("old.zip", new string('あ', 256));

        Assert.False(result.IsValid);
        Assert.Contains("255", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRenameError_同じフォルダの別のファイルと重なる名前を断り_大文字小文字だけの変更は認める()
    {
        using var ctx = new LibraryTestContext();
        _ = await PrepareAsync(ctx);
        ctx.CreateZip("taken.zip", ("1.jpg", TestImages.Jpeg(50, 50)));

        Assert.Contains("既にあります", ctx.Archives.GetRenameError(ctx.Workspace, "a.zip", "taken"), StringComparison.Ordinal);
        Assert.Contains("既にあります", ctx.Archives.GetRenameError(ctx.Workspace, "a.zip", "TAKEN.ZIP"), StringComparison.Ordinal);
        Assert.Null(ctx.Archives.GetRenameError(ctx.Workspace, "a.zip", "A.zip"));
        Assert.Null(ctx.Archives.GetRenameError(ctx.Workspace, "a.zip", "fresh"));
        Assert.NotNull(ctx.Archives.GetRenameError(ctx.Workspace, "a.zip", "a:b"));
    }

    // ---- 名前変更(UC-12) ----

    [Fact]
    public async Task 名前を変更すると_ファイル名とDBが更新され_ArchiveIDとサムネイルは維持される()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var content = Hash(prepared.ZipPath);
        var thumbnail = await ctx.StringAsync("SELECT relative_path FROM thumbnail_cache");
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "renamed", Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False(File.Exists(prepared.ZipPath));
        Assert.Equal(content, Hash(ctx.PathOf("renamed.zip")));
        Assert.Equal("renamed.zip", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal("zip", await ctx.StringAsync($"SELECT extension FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE file_name IN ('a.zip', 'renamed.zip')"));
        Assert.Equal("READY", await ctx.StringAsync($"SELECT status FROM thumbnail_cache WHERE archive_id = {prepared.ArchiveId}"));
        Assert.True(ctx.Cache.Exists(thumbnail));
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_RENAME' AND status = 'SUCCESS' " +
                $"AND archive_id = {prepared.ArchiveId} AND archive_name = 'renamed.zip' AND affected_count = 1"));
        Assert.Equal(prepared.ZipPath, await ctx.StringAsync("SELECT old_path FROM operation_history WHERE operation_type = 'ARCHIVE_RENAME'"));
        Assert.Equal(ctx.PathOf("renamed.zip"), await ctx.StringAsync("SELECT new_path FROM operation_history WHERE operation_type = 'ARCHIVE_RENAME'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success && n.Message.Contains("renamed.zip", StringComparison.Ordinal));

        // 名前変更後にスキャンしても、同じアーカイブとして扱われる
        _ = await ctx.Scan.ScanAsync(prepared.LibraryId, ScanType.Manual, Ct);
        Assert.Equal(prepared.ArchiveId, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'renamed.zip'"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal("READY", await ctx.StringAsync($"SELECT analysis_status FROM archives WHERE id = {prepared.ArchiveId}"));
    }

    [Fact]
    public async Task 拡張子をcbzにして名前を変更すると_DBの拡張子も更新される()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "comic.cbz", Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal("cbz", await ctx.StringAsync($"SELECT extension FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.True(File.Exists(ctx.PathOf("comic.cbz")));
    }

    [Fact]
    public async Task 大文字小文字だけを変える名前変更もできる()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "A.zip", Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Contains(Directory.GetFiles(ctx.Workspace), p => Path.GetFileName(p) == "A.zip");
        Assert.DoesNotContain(Directory.GetFiles(ctx.Workspace), p => Path.GetFileName(p) == "a.zip");
        Assert.Equal("A.zip", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {prepared.ArchiveId}"));
    }

    [Fact]
    public async Task 同じ名前のファイルがあれば_名前を変更せず_履歴には記録しない()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        ctx.CreateZip("taken.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        var taken = Hash(ctx.PathOf("taken.zip"));
        var original = Hash(prepared.ZipPath);
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "TAKEN", Ct);

        Assert.Equal("NAME_CONFLICT", result.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal(taken, Hash(ctx.PathOf("taken.zip"))); // 上書きしない
        Assert.Equal("a.zip", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_RENAME'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning);
    }

    [Theory]
    [InlineData("", "INVALID_NAME")]
    [InlineData("a:b", "INVALID_NAME")]
    [InlineData("a", "NO_CHANGE")]
    [InlineData("a.zip", "NO_CHANGE")]
    public async Task 無効な名前や変わらない名前では_何も変更しない(string input, string expectedCode)
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var original = Hash(prepared.ZipPath);

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, input, Ct);

        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_RENAME'"));
    }

    [Fact]
    public async Task 他のプログラムが使用中のファイルは_名前を変更せず_失敗を通知して履歴に記録する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var original = Hash(prepared.ZipPath);
        ctx.Notifications.Items.Clear();

        OperationResult result;
        using (new FileStream(prepared.ZipPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "renamed", Ct);
        }

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.True(result.ErrorCode is "IO_ERROR" or "ACCESS_DENIED", result.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal("a.zip", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(1, await HistoryCountAsync(ctx, "ARCHIVE_RENAME", "FAILED"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error && n.Message.Contains("使用中", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ファイルがなければ_名前変更は失敗し_再スキャンを促す()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        File.Delete(prepared.ZipPath);
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "renamed", Ct);

        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
        Assert.Equal("a.zip", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Contains(ctx.Notifications.Items, n => n.Message.Contains("再スキャン", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DBを更新できなければ_ファイル名を元に戻す()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var original = Hash(prepared.ZipPath);
        await using (var connection = await ctx.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_update BEFORE UPDATE ON archives BEGIN SELECT RAISE(ABORT, 'テスト用の失敗'); END;";
            _ = await command.ExecuteNonQueryAsync(Ct);
        }

        var result = await ctx.Archives.RenameAsync(prepared.ArchiveId, "renamed", Ct);

        Assert.Equal("DATABASE_ERROR", result.ErrorCode);
        Assert.True(File.Exists(prepared.ZipPath));
        Assert.False(File.Exists(ctx.PathOf("renamed.zip")));
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal("a.zip", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {prepared.ArchiveId}"));
    }

    // ---- 移動(UC-13) ----

    [Fact]
    public async Task 管理フォルダ内の既存のフォルダへ移動すると_ファイルとDBが更新され_ArchiveIDとサムネイルは維持される()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var content = Hash(prepared.ZipPath);
        var destinationFolderId = await ctx.ScalarAsync($"SELECT id FROM folders WHERE full_path = '{ctx.PathOf("dest")}'");
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.MoveAsync(prepared.ArchiveId, ctx.PathOf("dest"), Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False(File.Exists(prepared.ZipPath));
        Assert.Equal(content, Hash(ctx.PathOf(@"dest\a.zip")));
        Assert.Equal(destinationFolderId, await ctx.ScalarAsync($"SELECT folder_id FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal("READY", await ctx.StringAsync($"SELECT status FROM thumbnail_cache WHERE archive_id = {prepared.ArchiveId}"));
        Assert.Equal("READY", await ctx.StringAsync($"SELECT analysis_status FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_MOVE' AND status = 'SUCCESS' " +
                $"AND archive_id = {prepared.ArchiveId} AND old_path = '{prepared.ZipPath}' AND new_path = '{ctx.PathOf(@"dest\a.zip")}'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success);

        // 移動後にスキャンしても、同じアーカイブとして扱われる
        _ = await ctx.Scan.ScanAsync(prepared.LibraryId, ScanType.Manual, Ct);
        Assert.Equal(prepared.ArchiveId, await ctx.ScalarAsync("SELECT a.id FROM archives a WHERE a.file_name = 'a.zip'"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
    }

    [Fact]
    public async Task まだ登録されていない新しいフォルダへ移動すると_途中のフォルダも登録される()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var deep = ctx.PathOf(@"new1\new2");
        _ = Directory.CreateDirectory(deep);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders WHERE name IN ('new1', 'new2')"));

        var result = await ctx.Archives.MoveAsync(prepared.ArchiveId, deep, Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders WHERE name IN ('new1', 'new2')"));
        Assert.Equal(
            "new1\\new2",
            await ctx.StringAsync("SELECT relative_path FROM folders WHERE name = 'new2'"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT depth FROM folders WHERE name = 'new2'"));
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM folders c JOIN folders p ON c.parent_folder_id = p.id " +
                "WHERE c.name = 'new2' AND p.name = 'new1' AND p.depth = 1"));
        Assert.Equal(
            "new2",
            await ctx.StringAsync($"SELECT f.name FROM archives a JOIN folders f ON a.folder_id = f.id WHERE a.id = {prepared.ArchiveId}"));

        // その後のスキャンで、フォルダが重複しない
        _ = await ctx.Scan.ScanAsync(prepared.LibraryId, ScanType.Manual, Ct);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders WHERE name = 'new2'"));
    }

    [Fact]
    public async Task 管理フォルダの外へは移動できない()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var outside = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "outside-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(outside);
        try
        {
            var result = await ctx.Archives.MoveAsync(prepared.ArchiveId, outside, Ct);

            Assert.Equal("OUTSIDE_LIBRARY", result.ErrorCode);
            Assert.True(File.Exists(prepared.ZipPath));
            Assert.Empty(Directory.GetFiles(outside));
            Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_MOVE'"));
        }
        finally
        {
            TestFileOperations.DeleteDirectory(outside);
        }
    }

    [Fact]
    public async Task 移動先が不正なら_何も変更しない()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        ctx.CreateZip(@"dest\a.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        var original = Hash(prepared.ZipPath);
        var inDestination = Hash(ctx.PathOf(@"dest\a.zip"));

        var sameFolder = await ctx.Archives.MoveAsync(prepared.ArchiveId, ctx.Workspace, Ct);
        var conflict = await ctx.Archives.MoveAsync(prepared.ArchiveId, ctx.PathOf("dest"), Ct);
        var missing = await ctx.Archives.MoveAsync(prepared.ArchiveId, ctx.PathOf("none"), Ct);
        var blank = await ctx.Archives.MoveAsync(prepared.ArchiveId, " ", Ct);

        Assert.Equal("SAME_FOLDER", sameFolder.ErrorCode);
        Assert.Equal("NAME_CONFLICT", conflict.ErrorCode);
        Assert.Equal("DESTINATION_NOT_ACCESSIBLE", missing.ErrorCode);
        Assert.Equal("DESTINATION_NOT_ACCESSIBLE", blank.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal(inDestination, Hash(ctx.PathOf(@"dest\a.zip"))); // 上書きしない
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_MOVE'"));
    }

    [Fact]
    public async Task ボリュームをまたぐ移動では_コピーを確認してから元のファイルを削除する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var content = Hash(prepared.ZipPath);
        var writtenAt = File.GetLastWriteTimeUtc(prepared.ZipPath);
        var service = ctx.CreateArchiveService(alwaysCopyOnMove: true);

        var result = await service.MoveAsync(prepared.ArchiveId, ctx.PathOf("dest"), Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False(File.Exists(prepared.ZipPath));
        Assert.Equal(content, Hash(ctx.PathOf(@"dest\a.zip")));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(ctx.PathOf(@"dest\a.zip"))); // 更新日時を引き継ぐ
        Assert.Empty(Directory.GetFiles(ctx.PathOf("dest"), "*.k35tmp"));
        Assert.Equal("READY", await ctx.StringAsync($"SELECT analysis_status FROM archives WHERE id = {prepared.ArchiveId}")); // 再解析にならない
        Assert.Equal("READY", await ctx.StringAsync($"SELECT status FROM thumbnail_cache WHERE archive_id = {prepared.ArchiveId}"));
    }

    [Fact]
    public async Task コピーして移した後に元のファイルを削除できなければ_一部成功として知らせる()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var content = Hash(prepared.ZipPath);
        var service = ctx.CreateArchiveService(alwaysCopyOnMove: true);
        ctx.Notifications.Items.Clear();

        OperationResult result;
        // 読み取りは許可するが、削除は許可しない共有で開いておく（コピーはできるが、元の削除はできない）
        using (new FileStream(prepared.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await service.MoveAsync(prepared.ArchiveId, ctx.PathOf("dest"), Ct);
        }

        Assert.Equal(OperationStatus.Partial, result.Status);
        Assert.Equal(content, Hash(ctx.PathOf(@"dest\a.zip")));
        Assert.True(File.Exists(prepared.ZipPath)); // 元のファイルが残っている
        Assert.Equal(
            "dest",
            await ctx.StringAsync($"SELECT f.name FROM archives a JOIN folders f ON a.folder_id = f.id WHERE a.id = {prepared.ArchiveId}"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning && n.Message.Contains("手動で削除", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 移動に失敗したら_DBを更新せず_元のファイルを保持して履歴に記録する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var original = Hash(prepared.ZipPath);
        var folderBefore = await ctx.ScalarAsync($"SELECT folder_id FROM archives WHERE id = {prepared.ArchiveId}");

        OperationResult result;
        using (new FileStream(prepared.ZipPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await ctx.Archives.MoveAsync(prepared.ArchiveId, ctx.PathOf("dest"), Ct);
        }

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.False(File.Exists(ctx.PathOf(@"dest\a.zip")));
        Assert.Equal(folderBefore, await ctx.ScalarAsync($"SELECT folder_id FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(1, await HistoryCountAsync(ctx, "ARCHIVE_MOVE", "FAILED"));
    }

    // ---- 削除(UC-14) ----

    [Fact]
    public async Task 削除すると_ごみ箱へ移動され_DBの登録とサムネイルが消え_履歴は残る()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var content = Hash(prepared.ZipPath);
        var thumbnail = await ctx.StringAsync("SELECT relative_path FROM thumbnail_cache");
        Assert.True(ctx.Cache.Exists(thumbnail));
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.DeleteAsync(prepared.ArchiveId, permanent: false, Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False(File.Exists(prepared.ZipPath));
        Assert.Equal(content, Hash(Assert.Single(ctx.RecycleBin.Items)));
        Assert.Equal(0, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(0, await ctx.ScalarAsync($"SELECT COUNT(*) FROM thumbnail_cache WHERE archive_id = {prepared.ArchiveId}"));
        Assert.False(ctx.Cache.Exists(thumbnail)); // サムネイル画像のファイルも削除される
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_DELETE' AND status = 'SUCCESS' " +
                $"AND archive_id IS NULL AND archive_name = 'a.zip' AND old_path = '{prepared.ZipPath}'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success && n.Message.Contains("ごみ箱", StringComparison.Ordinal));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
    }

    [Fact]
    public async Task 完全削除を指定すると_ごみ箱を使わずに削除する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        var result = await ctx.Archives.DeleteAsync(prepared.ArchiveId, permanent: true, Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False(File.Exists(prepared.ZipPath));
        Assert.Empty(ctx.RecycleBin.Items);
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Equal(0, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Contains("完全に削除", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ごみ箱に移動できなければ_自動では完全削除せず_ファイルとDBを保持する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        var original = Hash(prepared.ZipPath);
        ctx.RecycleBin.Unavailable = "ネットワーク上の保存先のため、ごみ箱を使えません";

        var unavailable = await ctx.Archives.DeleteAsync(prepared.ArchiveId, permanent: false, Ct);

        Assert.Equal("RECYCLE_BIN_UNAVAILABLE", unavailable.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal(1, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {prepared.ArchiveId}"));

        ctx.RecycleBin.Unavailable = null;
        ctx.RecycleBin.FailToMove = true;
        var failed = await ctx.Archives.DeleteAsync(prepared.ArchiveId, permanent: false, Ct);

        Assert.Equal("RECYCLE_FAILED", failed.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Equal(1, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(2, await HistoryCountAsync(ctx, "ARCHIVE_DELETE", "FAILED"));
    }

    [Fact]
    public async Task 削除でファイルを削除できなければ_登録を残して失敗を通知する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        OperationResult result;
        using (new FileStream(prepared.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await ctx.Archives.DeleteAsync(prepared.ArchiveId, permanent: true, Ct);
        }

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.True(File.Exists(prepared.ZipPath));
        Assert.Equal(1, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal("READY", await ctx.StringAsync($"SELECT status FROM thumbnail_cache WHERE archive_id = {prepared.ArchiveId}"));
    }

    [Fact]
    public async Task ファイルが既にない場合の削除は_失敗とし_再スキャンを促す()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        File.Delete(prepared.ZipPath);
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.DeleteAsync(prepared.ArchiveId, permanent: false, Ct);

        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
        Assert.Equal(1, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Contains(ctx.Notifications.Items, n => n.Message.Contains("再スキャン", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ごみ箱の確認は_保存先の判定をそのまま返す()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        Assert.Null(ctx.Archives.GetRecycleBlockReason(prepared.ZipPath));
        ctx.RecycleBin.Unavailable = "理由";
        Assert.Equal("理由", ctx.Archives.GetRecycleBlockReason(prepared.ZipPath));
    }

    [Fact]
    public async Task 存在しないアーカイブへの操作は_何も変更せず通知する()
    {
        using var ctx = new LibraryTestContext();
        _ = await PrepareAsync(ctx);

        Assert.Equal("ARCHIVE_NOT_FOUND", (await ctx.Archives.RenameAsync(9999, "x", Ct)).ErrorCode);
        Assert.Equal("ARCHIVE_NOT_FOUND", (await ctx.Archives.MoveAsync(9999, ctx.PathOf("dest"), Ct)).ErrorCode);
        Assert.Equal("ARCHIVE_NOT_FOUND", (await ctx.Archives.DeleteAsync(9999, false, Ct)).ErrorCode);
        Assert.False(await ctx.Archives.ShowInExplorerAsync(9999, Ct));
    }

    // ---- エクスプローラーで表示(UC-15) ----

    [Fact]
    public async Task エクスプローラーで表示すると_ファイルのパスが渡される()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        var shown = await ctx.Archives.ShowInExplorerAsync(prepared.ArchiveId, Ct);

        Assert.True(shown);
        Assert.Equal([prepared.ZipPath], ctx.Explorer.Revealed);
    }

    [Fact]
    public async Task ファイルがなければ_エクスプローラーを開かず_再スキャンを促す()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        File.Delete(prepared.ZipPath);
        ctx.Notifications.Items.Clear();

        var shown = await ctx.Archives.ShowInExplorerAsync(prepared.ArchiveId, Ct);

        Assert.False(shown);
        Assert.Empty(ctx.Explorer.Revealed);
        Assert.Contains(ctx.Notifications.Items, n => n.Message.Contains("再スキャン", StringComparison.Ordinal));
    }

    [Fact]
    public async Task エクスプローラーを起動できなければ_エラーを通知する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        ctx.Explorer.Failure = new System.ComponentModel.Win32Exception("起動できません");
        ctx.Notifications.Items.Clear();

        var shown = await ctx.Archives.ShowInExplorerAsync(prepared.ArchiveId, Ct);

        Assert.False(shown);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error);
    }

    // ---- スキャンとの競合 ----

    [Theory]
    [InlineData("rename")]
    [InlineData("move")]
    public async Task スキャンの列挙後に名前変更や移動をしても_スキャンが古い結果で不在にしない(string operation)
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        ctx.Scan.AfterEnumerate = _ =>
        {
            // 列挙が済んでからDBへ反映するまでの間に、アプリ内で操作する
            var result = operation == "rename"
                ? ctx.Archives.RenameAsync(prepared.ArchiveId, "renamed", Ct).GetAwaiter().GetResult()
                : ctx.Archives.MoveAsync(prepared.ArchiveId, ctx.PathOf("dest"), Ct).GetAwaiter().GetResult();
            Assert.Equal(OperationStatus.Success, result.Status);
        };

        var summary = await ctx.Scan.ScanAsync(prepared.LibraryId, ScanType.Manual, Ct);
        ctx.Scan.AfterEnumerate = null;

        Assert.Equal(0, summary.Missing);
        Assert.Equal(0, summary.Added); // 古い名前のファイルは、もうないため登録しない
        Assert.Equal("AVAILABLE", await ctx.StringAsync($"SELECT availability_status FROM archives WHERE id = {prepared.ArchiveId}"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));

        // 次のスキャンでも、同じアーカイブのまま
        var next = await ctx.Scan.ScanAsync(prepared.LibraryId, ScanType.Manual, Ct);
        Assert.Equal(0, next.Added + next.Missing);
        Assert.Equal("AVAILABLE", await ctx.StringAsync($"SELECT availability_status FROM archives WHERE id = {prepared.ArchiveId}"));
    }

    // ---- 管理フォルダを管理対象から外す(UC-18) ----

    [Fact]
    public async Task 管理フォルダを外すと_配下の管理情報とサムネイル画像が消え_実ファイルと履歴は残る()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);
        _ = await ctx.Archives.RenameAsync(prepared.ArchiveId, "renamed", Ct); // 履歴に archive_id を持つ操作を残しておく
        var thumbnails = new List<string>();
        await using (var connection = await ctx.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT relative_path FROM thumbnail_cache";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                thumbnails.Add(reader.GetString(0));
            }
        }

        Assert.Equal(2, thumbnails.Count);
        Assert.All(thumbnails, t => Assert.True(ctx.Cache.Exists(t)));
        var historyBefore = await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history");
        ctx.Notifications.Items.Clear();

        var count = await ctx.Library.GetArchiveCountAsync(prepared.LibraryId, Ct);
        var result = await ctx.Library.RemoveFolderAsync(prepared.LibraryId, Ct);

        Assert.Equal(2, count);
        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache"));
        Assert.All(thumbnails, t => Assert.False(ctx.Cache.Exists(t)));

        // 実際のファイルとフォルダは変更しない
        Assert.True(File.Exists(ctx.PathOf("renamed.zip")));
        Assert.True(File.Exists(ctx.PathOf(@"sub\b.zip")));
        Assert.True(Directory.Exists(ctx.PathOf("dest")));

        // 履歴は残り、archive_id だけが NULL になる
        Assert.Equal(historyBefore + 1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE archive_id IS NOT NULL"));
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'LIBRARY_FOLDER_REMOVE' AND status = 'SUCCESS' " +
                $"AND affected_count = 2 AND old_path = '{ctx.Workspace}'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success && n.Message.Contains("ファイルは削除していません", StringComparison.Ordinal));

        // 同じフォルダを再登録すると、新規のアーカイブとして扱われる
        _ = await ctx.RegisterAndScanAsync();
        Assert.NotEqual(prepared.ArchiveId, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'renamed.zip'"));
    }

    [Fact]
    public async Task 登録されていない管理フォルダを外そうとすると_何も変更せず通知する()
    {
        using var ctx = new LibraryTestContext();
        var prepared = await PrepareAsync(ctx);

        var result = await ctx.Library.RemoveFolderAsync(9999, Ct);

        Assert.Equal("LIBRARY_FOLDER_NOT_FOUND", result.ErrorCode);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Null(await ctx.Library.GetArchiveCountAsync(9999, Ct));
        _ = prepared;
    }
}
