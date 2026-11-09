using K35Viewer.Domain;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

internal sealed class MainTestHarness : IDisposable
{
    private readonly LibraryTestContext _ctx;

    public MainTestHarness(LibraryTestContext ctx)
    {
        _ctx = ctx;
        Notifications = new NotificationService();
        Library = new LibraryViewModel(
            ctx.Library, ctx.Scan, ctx.Thumbnails, ctx.ImageLoader, new NoDialogService(), NullLogger<LibraryViewModel>.Instance);
        Viewer = new ViewerViewModel(ctx.Viewer, new ViewerImageDecoder(), NullLogger<ViewerViewModel>.Instance);
        Toasts = new ToastHostViewModel(Notifications);
        Main = new MainViewModel(Library, Viewer, Toasts, Notifications, NullLogger<MainViewModel>.Instance);
    }

    public NotificationService Notifications { get; }

    public LibraryViewModel Library { get; }

    public ViewerViewModel Viewer { get; }

    public ToastHostViewModel Toasts { get; }

    public MainViewModel Main { get; }

    /// <summary>バックグラウンドのスキャンとサムネイル作成が終わり、その状態がしばらく続くまで待つ。</summary>
    public async Task WaitForIdleAsync()
    {
        var idleSince = DateTime.MaxValue;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if (Library.IsScanning || Library.IsGeneratingThumbnails)
            {
                idleSince = DateTime.MaxValue;
            }
            else if (idleSince == DateTime.MaxValue)
            {
                idleSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "バックグラウンド処理が終わりませんでした。");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public void Dispose()
    {
        Viewer.Dispose();
        Library.Dispose();
        Toasts.Dispose();
        _ = _ctx;
    }

    private sealed class NoDialogService : IDialogService
    {
        public string? PickFolder() => null;

        public bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths) => false;
    }
}

public class MainViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>登録・スキャン・サムネイル作成まで済んだ、3頁のアーカイブがある状態を作り、一覧の先頭の行を返す。</summary>
    private static async Task<ArchiveListItem> PrepareAsync(LibraryTestContext ctx, MainTestHarness harness)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)), ("3.jpg", TestImages.Jpeg(120, 80)));
        await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        return Assert.Single(harness.Library.Archives);
    }

    [Fact]
    public async Task 一覧で開く操作をすると_ビューアに切り替わり_ウィンドウのタイトルにアーカイブ名が出る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        Assert.True(harness.Main.IsLibraryVisible);
        Assert.Equal("35Viewer", harness.Main.Title);

        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);

        Assert.False(harness.Main.IsLibraryVisible);
        Assert.True(harness.Viewer.IsOpen);
        Assert.Equal("book.zip - 35Viewer", harness.Main.Title);
        Assert.Equal("1 / 3", harness.Viewer.PositionText);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 開けなかった場合は_ライブラリ画面に留まり_理由が通知される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        File.Delete(ctx.PathOf("book.zip"));
        var shown = new List<string>();
        harness.Notifications.Notified += (_, n) => shown.Add(n.Message);
        ctx.Notifications.Items.Clear();

        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => ctx.Notifications.Items.Count > 0);
        await Task.Delay(200, Ct);

        Assert.True(harness.Main.IsLibraryVisible);
        Assert.False(harness.Viewer.IsOpen);
        Assert.Equal("35Viewer", harness.Main.Title);
        Assert.Contains("再スキャン", ctx.Notifications.Items[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ビューアで戻る操作をすると_ライブラリ画面に戻り_閲覧位置が保存される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        harness.Viewer.NextCommand.Execute(null);
        harness.Viewer.NextCommand.Execute(null);

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);

        Assert.True(harness.Main.IsLibraryVisible);
        Assert.False(harness.Main.IsViewerVisible);
        Assert.Equal("35Viewer", harness.Main.Title);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE last_view_index = 2"));

        // 同じアーカイブを開き直すと、前回の位置から始まる
        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        Assert.Equal("3 / 3", harness.Viewer.PositionText);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task ビューアを開いている間は_別の開く操作を受け付けない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        var opens = ctx.Reader.OpenCalls;

        harness.Library.OpenArchiveCommand.Execute(item);
        await Task.Delay(300, Ct);

        Assert.Equal(opens, ctx.Reader.OpenCalls);
        Assert.True(harness.Main.IsViewerVisible);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task OpenArchiveCommand_行を渡すと開く要求を出し_nullなら何もしない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        var requested = new List<long>();
        harness.Library.OpenRequested += (_, id) => requested.Add(id);

        harness.Library.OpenArchiveCommand.Execute(null);
        Assert.Empty(requested);

        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        Assert.Equal([item.Id], requested);
        await harness.Viewer.CloseAsync();
    }
}

public class ToastHostViewModelTests
{
    [Fact]
    public async Task 通知は表示され_一定時間で消え_表示数には上限がある()
    {
        var notifications = new NotificationService();
        using var toasts = new ToastHostViewModel(notifications, TimeSpan.FromMilliseconds(150));

        notifications.ShowSuccess("A");
        notifications.ShowWarning("B");
        Assert.Equal(["A", "B"], toasts.Items.Select(t => t.Message));
        Assert.Equal([NotificationLevel.Success, NotificationLevel.Warning], toasts.Items.Select(t => t.Level));
        Assert.Equal(["✓", "⚠"], toasts.Items.Select(t => t.Symbol));

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Empty(toasts.Items);

        for (var i = 0; i < 8; i++)
        {
            notifications.ShowInfo($"通知{i}");
        }

        Assert.Equal(5, toasts.Items.Count);
        Assert.Equal("通知7", toasts.Items[^1].Message); // 古いものから消える
    }
}
