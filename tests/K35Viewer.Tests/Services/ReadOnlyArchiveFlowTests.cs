using System.Windows.Input;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

/// <summary>
/// RAR / 7z（閲覧だけ。GitHub の Issue #3）を、ライブラリ（スキャン、解析、サムネイル）とビューア（閲覧、削除予約の拒否）で扱う流れ。
/// </summary>
public class ReadOnlyArchiveFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", name);

    private static void CopyFixture(LibraryTestContext ctx, string fixture, string relative)
    {
        var destination = ctx.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(FixturePath(fixture), destination, overwrite: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task スキャンは_RARと7zもアーカイブとして見つけ_解析とサムネイルまで進む()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CopyFixture(ctx, "nonsolid.7z", "a.7z");
        CopyFixture(ctx, "solid.7z", "b.cb7");
        _ = TestRars.CreateStored(ctx.PathOf("c.rar"), ("1.jpg", TestImages.Jpeg(20, 30)), ("2.jpg", TestImages.Jpeg(20, 30)));
        _ = TestZips.Create(ctx.PathOf("d.cbr"), ("1.jpg", TestImages.Jpeg(20, 30))); // 名前は CBR で、中身は ZIP
        File.WriteAllText(ctx.PathOf("e.txt"), "アーカイブではない");

        _ = await ctx.RegisterAndScanAsync();
        _ = await ctx.Thumbnails.ProcessPendingAsync(null, Ct);

        Assert.Equal(4, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives")); // e.txt は含まれない
        Assert.Equal(
            "a.7z|7z|READY|3;b.cb7|cb7|READY|3;c.rar|rar|READY|2;d.cbr|cbr|READY|1",
            await ctx.StringAsync(
                "SELECT group_concat(file_name || '|' || extension || '|' || analysis_status || '|' || image_count, ';') FROM (SELECT * FROM archives ORDER BY file_name)"));
        Assert.Equal(4, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.Equal("001.jpg", await ctx.StringAsync("SELECT first_image_entry FROM archives WHERE file_name = 'a.7z'"));
    }

    [Fact]
    public async Task 名前を変えても_元の拡張子が残り_DBの拡張子も合う()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.7z'");

        var result = await ctx.Archives.RenameAsync(id, "renamed", Ct); // 拡張子を省略して入力

        Assert.Equal(K35Viewer.Domain.OperationStatus.Success, result.Status);
        Assert.True(File.Exists(ctx.PathOf("renamed.7z")));
        Assert.Equal("renamed.7z|7z", await ctx.StringAsync("SELECT file_name || '|' || extension FROM archives WHERE id = " + id));
    }

    [Fact]
    public async Task 七ゼットを開くと_閲覧でき_書き込めない形式として扱われる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CopyFixture(ctx, "solid.7z", "book.7z");
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");

        var result = await ctx.Viewer.OpenAsync(id, Ct);

        Assert.NotNull(result.Session);
        using var session = result.Session;
        Assert.Equal(3, session.Count);
        Assert.False(session.CanModify);
        Assert.True(session.IsSolid);
        Assert.Equal((20, 30), TestImages.SizeOf(session.ReadImage(0)));
    }

    [Fact]
    public async Task 閲覧だけのアーカイブでは_削除予約を受け付けず_理由を知らせる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(20, 30)), ("2.jpg", TestImages.Jpeg(20, 30)));
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);

        // 7z: 削除予約できない
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single(a => a.FileName == "a.7z"));
        await WaitUntilAsync(() => harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        Assert.Equal(0, harness.Viewer.PendingDeleteCount);
        Assert.False(harness.Viewer.IsCurrentPendingDelete);
        var message = Assert.Single(shown, n => n.Message.Contains("閲覧だけ", StringComparison.Ordinal));
        Assert.Contains("ZIP に変換", message.Message, StringComparison.Ordinal);
        Assert.Equal(NotificationLevel.Info, message.Level);

        // ZIP: 今までどおり、削除予約できる（↓ で次のアーカイブへ）
        Assert.True(harness.Viewer.HandleKey(Key.Down, ModifierKeys.None));
        await WaitUntilAsync(() => harness.Viewer.IsOpen && harness.Viewer.ArchiveName == "b.zip" && !harness.Main.IsTransitioning);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        Assert.Equal(1, harness.Viewer.PendingDeleteCount);
        harness.Dialogs.Choice = LeaveChoice.Discard;
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task ソリッドの7zを開いたときだけ_後ろのページが遅いことを知らせる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        CopyFixture(ctx, "solid.7z", "a-solid.7z");
        CopyFixture(ctx, "nonsolid.7z", "b-normal.7z");
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);

        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single(a => a.FileName == "a-solid.7z"));
        await WaitUntilAsync(() => harness.Viewer.IsOpen && harness.Main.IsViewerVisible);

        var hint = Assert.Single(shown);
        Assert.Contains("ソリッド", hint.Message, StringComparison.Ordinal);
        Assert.Contains("a-solid.7z", hint.Message, StringComparison.Ordinal);
        Assert.Contains("ZIP に変換", hint.Message, StringComparison.Ordinal);

        // ↓ で、ソリッドでない 7z へ移ったときは、案内しない
        shown.Clear();
        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);
        await WaitUntilAsync(() => harness.Viewer.IsOpen && harness.Viewer.ArchiveName == "b-normal.7z" && !harness.Main.IsTransitioning);
        Assert.Empty(shown);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 画像の削除を確定する更新は_RARと7zでは断る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CopyFixture(ctx, "nonsolid.7z", "a.7z");
        var zip = ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(20, 30)));
        var state = new ArchiveFileState(new FileInfo(ctx.PathOf("a.7z")).Length, 0);

        var sevenZipReason = ctx.Archives.GetUpdateBlockReason(
            new ArchiveUpdateRequest(1, "a.7z", ctx.PathOf("a.7z"), null, ["001.jpg"], 2, null, null));
        var zipReason = ctx.Archives.GetUpdateBlockReason(
            new ArchiveUpdateRequest(2, "b.zip", zip, null, ["1.jpg"], 0, null, null));

        Assert.Equal("ZIP 形式ではないため、更新できません", sevenZipReason);
        Assert.NotEqual("ZIP 形式ではないため、更新できません", zipReason);
        _ = state;
    }
}
