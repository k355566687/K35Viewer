using System.Windows.Input;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;

namespace K35Viewer.Tests.Services;

/// <summary>ビューアで、↑ / ↓ を押して、ライブラリの一覧の並びでの前後のアーカイブを開く（画面を戻らずに移る）。</summary>
public class ArchiveNavigationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>a.zip から、↓ で進んだときの順（一覧は、ファイル名の昇順）。</summary>
    private static readonly string[] ForwardOrder = ["b.zip", "c.zip", "d.zip"];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>a.zip・b.zip・c.zip・sub\d.zip を登録し、指定したアーカイブをビューアで開いた状態にする（一覧は、ファイル名の昇順）。</summary>
    private static async Task<MainTestHarness> OpenViewerAsync(LibraryTestContext ctx, string fileName)
    {
        var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        await OpenAsync(harness, fileName);
        return harness;
    }

    private static async Task OpenAsync(MainTestHarness harness, string fileName)
    {
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single(a => a.FileName == fileName));
        await WaitUntilAsync(() => harness.Main.IsViewerVisible && harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
    }

    // 名前が変わっても、最初の画像を読み込み終わるまでは、切り替えの途中（この間の ↑ / ↓ は無視される）。
    // 続けてキーを押すテストは、切り替えが終わるまで待つ
    private static Task WaitForViewerAsync(MainTestHarness harness, string fileName) =>
        WaitUntilAsync(() => harness.Viewer.IsOpen && harness.Viewer.ArchiveName == fileName && !harness.Main.IsTransitioning);

    [Fact]
    public async Task 下キーで次のアーカイブ_上キーで前のアーカイブを開く_ライブラリへは戻らない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "a.zip");

        Assert.True(harness.Viewer.HandleKey(Key.Down, ModifierKeys.None));
        await WaitForViewerAsync(harness, "b.zip");
        Assert.True(harness.Main.IsViewerVisible);
        Assert.Equal("b.zip", harness.Library.SelectedArchive?.FileName); // ライブラリ側も、今のアーカイブを選んでいる

        Assert.True(harness.Viewer.HandleKey(Key.Down, ModifierKeys.None));
        await WaitForViewerAsync(harness, "c.zip");

        Assert.True(harness.Viewer.HandleKey(Key.Up, ModifierKeys.None));
        await WaitForViewerAsync(harness, "b.zip");
        Assert.True(harness.Main.IsViewerVisible);
        Assert.Equal("b.zip - 35Viewer", harness.Main.Title);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧の並べ替えに従って_前後を決める()
    {
        using var ctx = new LibraryTestContext();
        var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        using var disposeHarness = harness;
        harness.Library.IsDescending = true;
        await WaitUntilAsync(() => harness.Library.Archives.Count == 4 && harness.Library.Archives[0].FileName == "d.zip");
        await OpenAsync(harness, "d.zip");

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        await WaitForViewerAsync(harness, "c.zip"); // 降順の並びでの、次
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧の先頭と末尾では止まり_理由を通知する()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "a.zip");
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);

        _ = harness.Viewer.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Equal("a.zip", harness.Viewer.ArchiveName);
        var first = Assert.Single(shown);
        Assert.Contains("これより前", first.Message, StringComparison.Ordinal);

        foreach (var expected in ForwardOrder)
        {
            _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);
            await WaitForViewerAsync(harness, expected);
        }

        shown.Clear();
        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        Assert.Equal("d.zip", harness.Viewer.ArchiveName); // ぐるっと回らない
        Assert.Contains("これより後", Assert.Single(shown).Message, StringComparison.Ordinal);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 開けないことが分かっているアーカイブは飛ばす()
    {
        using var ctx = new LibraryTestContext();
        var harness = new MainTestHarness(ctx);
        using var disposeHarness = harness;
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = ctx.CreateFile("b.zip", size: 64); // ZIP ではない（破損）
        ctx.CreateZip("c.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        Assert.True(harness.Library.Archives.Single(a => a.FileName == "b.zip").IsKnownUnopenable);
        await OpenAsync(harness, "a.zip");

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        await WaitForViewerAsync(harness, "c.zip"); // b.zip を飛ばす
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);
        _ = harness.Viewer.HandleKey(Key.Up, ModifierKeys.None);
        await WaitForViewerAsync(harness, "a.zip"); // 戻るときも、飛ばす
        Assert.Empty(shown);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 開けないアーカイブしか続いていなければ_止まる()
    {
        using var ctx = new LibraryTestContext();
        var harness = new MainTestHarness(ctx);
        using var disposeHarness = harness;
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = ctx.CreateFile("b.zip", size: 64);
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        await OpenAsync(harness, "a.zip");
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        Assert.Equal("a.zip", harness.Viewer.ArchiveName);
        Assert.Contains("開けるアーカイブはありません", Assert.Single(shown).Message, StringComparison.Ordinal);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示では_上下キーは画像の選択に使い_Ctrlを押したときだけ前後のアーカイブへ移る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "a.zip");
        var requests = new List<int>();
        harness.Viewer.NavigateArchiveRequested += (_, direction) => requests.Add(direction);

        // 1枚表示: ↑ / ↓ だけで移る
        _ = harness.Viewer.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Equal([-1], requests);
        requests.Clear();

        // 一覧表示: ↑ / ↓ は画像の選択（移らない）。Ctrl を押したときだけ、移る
        harness.Viewer.ToggleViewModeCommand.Execute(null);
        Assert.True(harness.Viewer.IsListView);
        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);
        _ = harness.Viewer.HandleKey(Key.Up, ModifierKeys.Shift);
        Assert.Empty(requests);

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.Control);
        Assert.Equal([1], requests);
        await WaitForViewerAsync(harness, "b.zip");
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 削除予約があって確認でキャンセルすると_今のアーカイブに留まる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "a.zip");
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Cancel;

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        _ = Assert.Single(harness.Dialogs.LeaveConfirmations);
        Assert.Equal("a.zip", harness.Viewer.ArchiveName);
        Assert.Equal(1, harness.Viewer.PendingDeleteCount); // 削除予約も残る
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 削除予約を破棄して移ると_ZIPは変わらず_次のアーカイブを開く()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "a.zip");
        var before = File.ReadAllBytes(ctx.PathOf("a.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Discard;

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        await WaitForViewerAsync(harness, "b.zip");
        Assert.Equal(0, harness.Viewer.PendingDeleteCount);
        Assert.Equal(before, File.ReadAllBytes(ctx.PathOf("a.zip")));
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 削除予約を反映して移ると_ZIPを更新してから_次のアーカイブを開く()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "a.zip");
        harness.Viewer.ToggleDeleteCommand.Execute(null); // 1.jpg を削除予約
        harness.Dialogs.Choice = LeaveChoice.Update;

        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        await WaitForViewerAsync(harness, "b.zip");
        Assert.True(harness.Main.IsViewerVisible);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(ctx.PathOf("a.zip")))
        {
            Assert.Equal(["2.jpg"], zip.Entries.Select(e => e.FullName));
        }

        _ = Assert.Single(ctx.RecycleBin.Items); // 更新前の ZIP は、ごみ箱へ
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 次のアーカイブを開けなかった場合は_理由を通知して_ライブラリへ戻る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx, "b.zip");
        File.Delete(ctx.PathOf("c.zip")); // 一覧には残っているが、ファイルがない
        _ = harness.Viewer.HandleKey(Key.Down, ModifierKeys.None);

        await WaitUntilAsync(() => !harness.Main.IsViewerVisible);
        Assert.False(harness.Viewer.IsOpen);
        // 開けなかった理由は、ビューアのサービスが通知する
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning && n.Message.Contains("c.zip", StringComparison.Ordinal));
        Assert.Equal("c.zip", harness.Library.SelectedArchive?.FileName); // ライブラリで、開こうとしたものが選ばれている
    }

    [Fact]
    public async Task ビューアを開いていないときは_何もしない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);

        Assert.False(harness.Viewer.HandleKey(Key.Down, ModifierKeys.None)); // 開いていない
        Assert.True(harness.Main.IsLibraryVisible);
    }
}
