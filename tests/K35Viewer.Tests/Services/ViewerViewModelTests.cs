using System.Windows.Input;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

public class ViewerViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ViewerViewModel CreateViewer(LibraryTestContext ctx) =>
        new(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>
    /// ページ i（1始まり）の幅が 100 + 10×i、高さが 80 のJPEGを持つZIPを登録し、IDを返す。
    /// 幅を見れば、どのページが表示されているか分かる。
    /// </summary>
    private static async Task<long> RegisterBookAsync(LibraryTestContext ctx, int pages = 4, string name = "book.zip")
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            name,
            [.. Enumerable.Range(1, pages).Select(i => ($"{i:D3}.jpg", TestImages.Jpeg(100 + (10 * i), 80)))]);
        await ctx.RegisterAndScanAsync();
        return await ctx.ScalarAsync($"SELECT id FROM archives WHERE file_name = '{name}'");
    }

    private static async Task<long> RegisterPngBookAsync(LibraryTestContext ctx, params (int Width, int Height)[] sizes)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            [.. sizes.Select((s, i) => ($"{i + 1:D3}.png", TestImages.Png(s.Width, s.Height)))]);
        await ctx.RegisterAndScanAsync();
        return await ctx.ScalarAsync("SELECT id FROM archives");
    }

    [Fact]
    public async Task OpenAsync_最初の頁を表示し_現在位置を示す()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx);
        using var viewer = CreateViewer(ctx);

        Assert.True(await viewer.OpenAsync(id, Ct));

        Assert.True(viewer.IsOpen);
        Assert.Equal("book.zip", viewer.ArchiveName);
        Assert.Equal("1 / 4", viewer.PositionText);
        Assert.Equal("001.jpg", viewer.EntryName);
        Assert.Equal(110, viewer.CurrentImage!.PixelWidth); // 1頁目
        Assert.False(viewer.IsLoading);
        Assert.Equal(string.Empty, viewer.ErrorText);
        Assert.False(viewer.PreviousCommand.CanExecute(null));
        Assert.True(viewer.NextCommand.CanExecute(null));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 前後移動_先頭_末尾へ移動でき_端では止まる()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx);
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);

        viewer.NextCommand.Execute(null);
        Assert.Equal("2 / 4", viewer.PositionText);
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 120);

        viewer.LastCommand.Execute(null);
        Assert.Equal("4 / 4", viewer.PositionText);
        Assert.False(viewer.NextCommand.CanExecute(null)); // 末尾では進めない
        viewer.NextCommand.Execute(null);
        Assert.Equal("4 / 4", viewer.PositionText); // 先頭へは戻らない
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 140);

        viewer.PreviousCommand.Execute(null);
        Assert.Equal("3 / 4", viewer.PositionText);
        viewer.FirstCommand.Execute(null);
        Assert.Equal("1 / 4", viewer.PositionText);
        Assert.False(viewer.PreviousCommand.CanExecute(null));
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 110);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 素早く何度も移動しても_最後に要求した頁だけが表示される()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx, pages: 8);
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);

        for (var i = 0; i < 5; i++)
        {
            viewer.NextCommand.Execute(null);
        }

        Assert.Equal("6 / 8", viewer.PositionText);
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 160); // 6頁目
        await Task.Delay(300, Ct); // 途中の頁の読み込みが遅れて届いても、上書きされないこと
        Assert.Equal(160, viewer.CurrentImage!.PixelWidth);
        Assert.Equal("006.jpg", viewer.EntryName);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 壊れた画像は_エラーを示して閲覧を続けられる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Corrupt()), ("3.jpg", TestImages.Jpeg(120, 80)));
        await ctx.RegisterAndScanAsync();
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct);

        viewer.NextCommand.Execute(null);
        await WaitUntilAsync(() => viewer.ErrorText.Length > 0);

        Assert.Contains("2.jpg", viewer.ErrorText, StringComparison.Ordinal);
        Assert.Null(viewer.CurrentImage);
        Assert.Equal(0, viewer.DisplayWidth);

        viewer.NextCommand.Execute(null);
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 120);
        Assert.Equal(string.Empty, viewer.ErrorText);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 閲覧位置は少し待って保存され_次に開くと復元される()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx);
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);

        viewer.NextCommand.Execute(null);
        viewer.NextCommand.Execute(null);

        await WaitUntilAsync(
            () => ctx.ScalarAsync($"SELECT COALESCE(last_view_index, -1) FROM archives WHERE id = {id}").GetAwaiter().GetResult() == 2,
            seconds: 20);
        Assert.Equal(1, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {id} AND last_view_entry = '003.jpg'"));
        await viewer.CloseAsync();

        await viewer.OpenAsync(id, Ct);
        Assert.Equal("3 / 4", viewer.PositionText);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 開いた直後に閉じても_最後に閲覧した位置が記録される()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx);
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);
        viewer.NextCommand.Execute(null);
        viewer.NextCommand.Execute(null);

        await viewer.CloseAsync(); // 保存の待ち時間を待たずに閉じる

        Assert.Equal(1, await ctx.ScalarAsync(
            $"SELECT COUNT(*) FROM archives WHERE id = {id} AND last_view_index = 2 AND last_viewed_at IS NOT NULL"));
    }

    [Fact]
    public async Task CloseAsync_状態を片付け_アーカイブのファイルを手放す()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx);
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);
        viewer.ToggleFullScreenCommand.Execute(null);
        Assert.True(viewer.IsFullScreen);

        await viewer.CloseAsync();
        await viewer.CloseAsync(); // 二重に閉じても問題ない

        Assert.False(viewer.IsOpen);
        Assert.Null(viewer.CurrentImage);
        Assert.False(viewer.IsFullScreen);
        Assert.Equal(string.Empty, viewer.PositionText);
        Assert.Equal(string.Empty, viewer.ArchiveName);
        File.Delete(ctx.PathOf("book.zip")); // 閉じたあとは削除できる
        Assert.False(File.Exists(ctx.PathOf("book.zip")));
    }

    [Fact]
    public async Task OpenAsync_開けなければfalseを返し_開いている間は2つ目を開かない()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx);
        using var viewer = CreateViewer(ctx);

        Assert.False(await viewer.OpenAsync(99999, Ct));
        Assert.False(viewer.IsOpen);
        Assert.Equal(NotificationLevel.Warning, ctx.Notifications.Items[^1].Level);

        Assert.True(await viewer.OpenAsync(id, Ct));
        Assert.False(await viewer.OpenAsync(id, Ct)); // すでに開いている
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task ウィンドウに合わせる_縮小のみで_小さい画像は拡大しない()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterPngBookAsync(ctx, (1600, 1200), (400, 300));
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);

        viewer.UpdateViewport(800, 600, dpiScale: 1.0);

        Assert.Equal(ZoomMode.Fit, viewer.ZoomMode);
        Assert.Equal(0.5, viewer.EffectiveScale, 3);
        Assert.Equal((800, 600), (viewer.DisplayWidth, viewer.DisplayHeight));
        Assert.Contains("フィット", viewer.ZoomText, StringComparison.Ordinal);
        Assert.Contains("50%", viewer.ZoomText, StringComparison.Ordinal);

        viewer.NextCommand.Execute(null); // 400x300 はウィンドウより小さい
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 400);
        Assert.Equal(1.0, viewer.EffectiveScale, 3);
        Assert.Equal((400, 300), (viewer.DisplayWidth, viewer.DisplayHeight));

        viewer.UpdateViewport(200, 150, 1.0); // ウィンドウを縮めると、収まるように縮小される
        Assert.Equal(0.5, viewer.EffectiveScale, 3);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 画面の拡大率_DPI_を考慮して_画面の画素に合わせた大きさにする()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterPngBookAsync(ctx, (1600, 1200));
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);

        // 150%表示: 論理800x600は、画面の画素では1200x900。1600x1200の画像は0.75倍で収まる
        viewer.UpdateViewport(800, 600, dpiScale: 1.5);
        Assert.Equal(0.75, viewer.EffectiveScale, 3);
        Assert.Equal(800, viewer.DisplayWidth, 3);
        Assert.Equal(600, viewer.DisplayHeight, 3);

        // 原寸は、画像の1画素＝画面の1画素。論理単位では 1600 ÷ 1.5
        viewer.ActualSizeCommand.Execute(null);
        Assert.Equal(1.0, viewer.EffectiveScale, 3);
        Assert.Equal(1600 / 1.5, viewer.DisplayWidth, 3);
        Assert.Equal("100%", viewer.ZoomText);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 拡大_縮小は段階式で_上限と下限で止まり_フィットと原寸へ戻せる()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterPngBookAsync(ctx, (1600, 1200));
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);
        viewer.UpdateViewport(800, 600, 1.0); // フィット 50%

        viewer.ZoomInCommand.Execute(null);
        Assert.Equal(ZoomMode.Custom, viewer.ZoomMode);
        Assert.Equal(0.67, viewer.EffectiveScale, 3); // 50% の次の段階
        Assert.Equal(1600 * 0.67, viewer.DisplayWidth, 3);
        viewer.ZoomInCommand.Execute(null);
        Assert.Equal(0.75, viewer.EffectiveScale, 3);
        viewer.ZoomOutCommand.Execute(null);
        viewer.ZoomOutCommand.Execute(null);
        Assert.Equal(0.5, viewer.EffectiveScale, 3);

        for (var i = 0; i < 30; i++)
        {
            viewer.ZoomInCommand.Execute(null);
        }

        Assert.Equal(8.0, viewer.EffectiveScale, 3); // 上限
        Assert.Equal("800%", viewer.ZoomText);
        for (var i = 0; i < 40; i++)
        {
            viewer.ZoomOutCommand.Execute(null);
        }

        Assert.Equal(0.1, viewer.EffectiveScale, 3); // 下限

        viewer.ActualSizeCommand.Execute(null);
        Assert.Equal((ZoomMode.Actual, 1.0), (viewer.ZoomMode, viewer.EffectiveScale));
        viewer.FitCommand.Execute(null);
        Assert.Equal((ZoomMode.Fit, 0.5), (viewer.ZoomMode, viewer.EffectiveScale));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 拡大率は頁をめくっても保たれる()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterPngBookAsync(ctx, (1600, 1200), (800, 600));
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);
        viewer.UpdateViewport(1000, 700, 1.0);
        viewer.ActualSizeCommand.Execute(null);

        viewer.NextCommand.Execute(null);
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 800);

        Assert.Equal(ZoomMode.Actual, viewer.ZoomMode);
        Assert.Equal((800, 600), (viewer.DisplayWidth, viewer.DisplayHeight));
        await viewer.CloseAsync();
    }

    [Theory]
    [InlineData(Key.Right, ModifierKeys.None, 3)]
    [InlineData(Key.PageDown, ModifierKeys.None, 3)]
    [InlineData(Key.Space, ModifierKeys.None, 3)]
    [InlineData(Key.Left, ModifierKeys.None, 1)]
    [InlineData(Key.PageUp, ModifierKeys.None, 1)]
    [InlineData(Key.Space, ModifierKeys.Shift, 1)]
    [InlineData(Key.Home, ModifierKeys.None, 0)]
    [InlineData(Key.End, ModifierKeys.None, 5)]
    public async Task HandleKey_頁送りのキー(Key key, ModifierKeys modifiers, int expectedIndex)
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterBookAsync(ctx, pages: 6);
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);
        viewer.NextCommand.Execute(null); // 2頁目（位置1）から始める
        viewer.NextCommand.Execute(null); // 3頁目（位置2）
        Assert.Equal("3 / 6", viewer.PositionText);

        Assert.True(viewer.HandleKey(key, modifiers));

        Assert.Equal($"{expectedIndex + 1} / 6", viewer.PositionText);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task HandleKey_拡大_縮小_フィット_原寸_全画面_戻る()
    {
        using var ctx = new LibraryTestContext();
        var id = await RegisterPngBookAsync(ctx, (1600, 1200));
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(id, Ct);
        viewer.UpdateViewport(800, 600, 1.0);
        var closeRequests = 0;
        viewer.CloseRequested += (_, _) => closeRequests++;

        Assert.True(viewer.HandleKey(Key.OemPlus, ModifierKeys.None));
        Assert.Equal(0.67, viewer.EffectiveScale, 3);
        Assert.True(viewer.HandleKey(Key.Add, ModifierKeys.None));
        Assert.Equal(0.75, viewer.EffectiveScale, 3);
        Assert.True(viewer.HandleKey(Key.OemMinus, ModifierKeys.None));
        Assert.True(viewer.HandleKey(Key.Subtract, ModifierKeys.None));
        Assert.Equal(0.5, viewer.EffectiveScale, 3);

        Assert.True(viewer.HandleKey(Key.D1, ModifierKeys.None));
        Assert.Equal(ZoomMode.Actual, viewer.ZoomMode);
        Assert.True(viewer.HandleKey(Key.D0, ModifierKeys.None));
        Assert.Equal(ZoomMode.Fit, viewer.ZoomMode);
        Assert.True(viewer.HandleKey(Key.NumPad1, ModifierKeys.None));
        Assert.Equal(ZoomMode.Actual, viewer.ZoomMode);
        Assert.True(viewer.HandleKey(Key.NumPad0, ModifierKeys.None));
        Assert.Equal(ZoomMode.Fit, viewer.ZoomMode);

        Assert.True(viewer.HandleKey(Key.F11, ModifierKeys.None));
        Assert.True(viewer.IsFullScreen);
        Assert.False(viewer.ShowToolbar);
        Assert.True(viewer.HandleKey(Key.F11, ModifierKeys.None));
        Assert.False(viewer.IsFullScreen);
        Assert.True(viewer.ShowToolbar);

        Assert.Equal(0, closeRequests);
        Assert.True(viewer.HandleKey(Key.Escape, ModifierKeys.None));
        Assert.Equal(1, closeRequests); // 画面の切り替えは呼び出し側が行う

        Assert.False(viewer.HandleKey(Key.A, ModifierKeys.None)); // 割り当てのないキーは処理しない
        Assert.False(viewer.HandleKey(Key.Enter, ModifierKeys.None));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task HandleKey_開いていないときは何も処理しない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewer = CreateViewer(ctx);
        var closeRequests = 0;
        viewer.CloseRequested += (_, _) => closeRequests++;

        Assert.False(viewer.HandleKey(Key.Escape, ModifierKeys.None));
        Assert.False(viewer.HandleKey(Key.Right, ModifierKeys.None));
        Assert.False(viewer.HandleKey(Key.F11, ModifierKeys.None));
        Assert.Equal(0, closeRequests);
        Assert.False(viewer.IsFullScreen);
    }
}
