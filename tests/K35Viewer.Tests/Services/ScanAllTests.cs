using K35Viewer.Domain;
using K35Viewer.Services;

namespace K35Viewer.Tests.Services;

public class ScanAllTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AddAsync(LibraryTestContext ctx, string relative)
    {
        var result = await ctx.Library.AddFolderAsync(ctx.PathOf(relative), false, Ct);
        return result.LibraryFolderId!.Value;
    }

    [Fact]
    public async Task ScanAllAsync_管理フォルダがなければ何もしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();

        var summary = await ctx.Scan.ScanAllAsync(ScanType.Startup, Ct);

        Assert.Null(summary);
        Assert.Empty(ctx.Notifications.Items);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'SCAN_STARTUP'"));
    }

    [Fact]
    public async Task ScanAllAsync_複数の管理フォルダを1回のスキャンとして集計し_通知と履歴は1回だけにする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"libA\a.zip");
        ctx.CreateFile(@"libB\sub\b.zip");
        ctx.CreateFile(@"libB\c.cbz");
        await AddAsync(ctx, "libA");
        await AddAsync(ctx, "libB");

        var summary = await ctx.Scan.ScanAllAsync(ScanType.Manual, Ct);

        Assert.NotNull(summary);
        Assert.Equal(OperationStatus.Success, summary.Status);
        Assert.Equal(3, summary.Added);
        Assert.Equal(3, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs WHERE library_folder_id IS NULL AND added_count = 3"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'SCAN_MANUAL'"));
        Assert.Equal(2, ctx.Notifications.Items.Count); // 開始と完了
        Assert.Equal("ライブラリの再スキャンを開始しました", ctx.Notifications.Items[0].Message);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders WHERE last_scan_at IS NOT NULL"));
    }

    [Fact]
    public async Task ScanAllAsync_一部の管理フォルダに接続できなくても_他は更新し一部失敗として通知する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"libA\a.zip");
        ctx.CreateFile(@"libB\b.zip");
        await AddAsync(ctx, "libA");
        await AddAsync(ctx, "libB");
        await ctx.Scan.ScanAllAsync(ScanType.Initial, Ct);
        Directory.Delete(ctx.PathOf("libB"), recursive: true); // libB だけ切断
        ctx.CreateFile(@"libA\new.zip");
        ctx.Notifications.Items.Clear();

        var summary = await ctx.Scan.ScanAllAsync(ScanType.Startup, Ct);

        Assert.NotNull(summary);
        Assert.Equal(OperationStatus.Partial, summary.Status);
        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Missing); // 切断されたlibBの登録は不在にしない
        Assert.Equal(1, summary.Failed);
        Assert.Equal(3, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE availability_status = 'AVAILABLE'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders WHERE availability_status = 'UNAVAILABLE'"));
        Assert.Equal(NotificationLevel.Warning, ctx.Notifications.Items[^1].Level);
    }

    [Fact]
    public async Task ScanAllAsync_無効な管理フォルダは対象外にする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"libA\a.zip");
        ctx.CreateFile(@"libB\b.zip");
        await AddAsync(ctx, "libA");
        await AddAsync(ctx, "libB");
        await ctx.ExecuteAsync("UPDATE library_folders SET is_enabled = 0 WHERE path LIKE '%libB'");

        var summary = await ctx.Scan.ScanAllAsync(ScanType.Manual, Ct);

        Assert.Equal(1, summary!.Added);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE file_name = 'b.zip'"));
    }

    [Fact]
    public async Task ScanAllAsync_同時に呼ばれても順番に実行され_重複登録しない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 50; i++)
        {
            ctx.CreateFile($"book{i:D2}.zip");
        }

        await AddAsync(ctx, string.Empty);

        var first = ctx.Scan.ScanAllAsync(ScanType.Startup, Ct);
        var second = ctx.Scan.ScanAllAsync(ScanType.Manual, Ct);
        var summaries = await Task.WhenAll(first, second);

        Assert.All(summaries, s => Assert.Equal(OperationStatus.Success, s!.Status));
        Assert.Equal(50, summaries.Sum(s => s!.Added)); // 追加は片方だけ。もう片方は差分なし
        Assert.Equal(50, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs WHERE status = 'SUCCESS'"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs WHERE status = 'RUNNING'"));
    }
}
