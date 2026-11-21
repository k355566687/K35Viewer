using System.Windows.Input;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

/// <summary>マウスの戻るボタン（と、キーボードの「戻る」キー）で、ビューアからライブラリへ戻る（GitHub の Issue #28）。</summary>
public class BackButtonTests
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

    private static async Task<MainTestHarness> OpenViewerAsync(LibraryTestContext ctx)
    {
        var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single(a => a.FileName == "a.zip"));
        await WaitUntilAsync(() => harness.Main.IsViewerVisible && harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        return harness;
    }

    [Fact]
    public async Task 戻るボタンで_ライブラリへ戻る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx);

        Assert.True(harness.Viewer.HandleBackButton());

        await WaitUntilAsync(() => harness.Main.IsLibraryVisible && !harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        Assert.False(harness.Viewer.HandleBackButton()); // ライブラリ画面では、何もしない
    }

    [Fact]
    public async Task キーボードの戻るキーでも_ライブラリへ戻る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx);

        Assert.True(harness.Viewer.HandleKey(Key.BrowserBack, ModifierKeys.None));

        await WaitUntilAsync(() => harness.Main.IsLibraryVisible && !harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
    }

    [Fact]
    public async Task 全画面のときも_そのままライブラリへ戻る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx);
        harness.Viewer.ToggleFullScreenCommand.Execute(null);

        Assert.True(harness.Viewer.HandleBackButton());

        await WaitUntilAsync(() => harness.Main.IsLibraryVisible && !harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
    }

    [Fact]
    public async Task 削除予約が残っていれば_Escと同じく確認し_キャンセルならビューアに留まる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenViewerAsync(ctx);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Cancel;

        Assert.True(harness.Viewer.HandleBackButton());
        await WaitUntilAsync(() => harness.Dialogs.LeaveConfirmations.Count == 1 && !harness.Main.IsTransitioning);
        Assert.True(harness.Main.IsViewerVisible);
        Assert.True(harness.Viewer.IsOpen);

        harness.Dialogs.Choice = LeaveChoice.Discard;
        Assert.True(harness.Viewer.HandleBackButton());
        await WaitUntilAsync(() => harness.Main.IsLibraryVisible && !harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        Assert.Equal(2, harness.Dialogs.LeaveConfirmations.Count);
    }
}
