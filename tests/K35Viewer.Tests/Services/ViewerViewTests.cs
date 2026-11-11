using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using K35Viewer.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>実際のWPFウィンドウで、ビューア画面の表示・操作・画面切り替えを確認する。</summary>
[Collection(WpfViewsGroup.Name)]
public class ViewerViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ビューアは_画像をウィンドウに収めて中央に表示し_拡大するとスクロールできて_ホイールで頁を送る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.png", TestImages.Png(3000, 2000)), ("2.png", TestImages.Png(200, 100)));
        await ctx.RegisterAndScanAsync();
        using var viewer = new ViewerViewModel(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);
        Assert.True(await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct));

        double fitWidth = 0, scrollWidth = 0, leftMargin = 0, rightMargin = 0, zoomedImageWidth = 0, scrollable = 0;
        var position = string.Empty;
        var imageFound = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new ViewerView { DataContext = viewer };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1000, Height = 700, Content = view });
            window.Show();
            WpfTestHelper.Pump();

            var scroll = WpfTestHelper.Descendants<ScrollViewer>(view)
                .First(s => AutomationProperties.GetName(s) == "画像の表示領域");
            var image = WpfTestHelper.Descendants<Image>(view).First();
            imageFound = image.Source is not null;

            // ウィンドウに収まる大きさで、左右の余白が等しい（中央に置かれている）
            fitWidth = image.ActualWidth;
            scrollWidth = scroll.ActualWidth;
            var origin = image.TranslatePoint(new Point(0, 0), scroll);
            leftMargin = origin.X;
            rightMargin = scroll.ActualWidth - origin.X - image.ActualWidth;

            // 原寸にすると、ウィンドウからはみ出してスクロールできる
            viewer.ActualSizeCommand.Execute(null);
            WpfTestHelper.Pump();
            zoomedImageWidth = image.ActualWidth;
            scrollable = scroll.ScrollableWidth;

            // ホイールで次の頁へ（Ctrlなしは頁送り）
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };
            scroll.RaiseEvent(wheel);
            position = viewer.PositionText;

            window.Close();
        });

        Assert.True(imageFound, "画像が表示されていません。");
        Assert.Equal(viewer.DisplayWidth > 0, fitWidth > 0);
        Assert.True(fitWidth <= scrollWidth, $"画像({fitWidth})がウィンドウ({scrollWidth})に収まっていません。");
        Assert.InRange(leftMargin - rightMargin, -2, 2);
        Assert.True(scrollable > 0, "原寸にしても、スクロールできません。");
        Assert.True(zoomedImageWidth > scrollWidth, $"原寸({zoomedImageWidth})がウィンドウ({scrollWidth})より大きくなっていません。");
        Assert.Equal("2 / 2", position);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task メインウィンドウは_ビューア表示中だけキー操作を渡し_Escでライブラリへ戻り_F11で全画面にする()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)), ("3.jpg", TestImages.Jpeg(120, 80)));
        await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync(); // 画面表示時の初期化を済ませ、表示後に勝手に走らないようにする
        await harness.WaitForIdleAsync();
        Assert.True(await harness.Viewer.OpenAsync(harness.Library.Archives[0].Id, Ct));
        harness.Main.IsViewerVisible = true;

        string titleWhileViewing = string.Empty, positionAfterRight = string.Empty, positionAfterLeft = string.Empty;
        var handledRight = false;
        var libraryKeyHandled = true;
        (WindowStyle Style, WindowState State) fullScreen = default, restored = default;
        var returnedToLibrary = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new MainWindow(harness.Main));
            window.Show();
            WpfTestHelper.Pump();
            titleWhileViewing = window.Title;
            var source = PresentationSource.FromVisual(window)!;

            bool Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(args);
                return args.Handled;
            }

            handledRight = Press(Key.Right);
            positionAfterRight = harness.Viewer.PositionText;
            Press(Key.Left);
            positionAfterLeft = harness.Viewer.PositionText;

            Press(Key.F11);
            WpfTestHelper.Pump();
            fullScreen = (window.WindowStyle, window.WindowState);
            Press(Key.F11);
            WpfTestHelper.Pump();
            restored = (window.WindowStyle, window.WindowState);

            // Escでライブラリへ戻る（画面の切り替えと保存は非同期なので、完了まで待つ）
            Press(Key.Escape);
            WpfTestHelper.PumpUntil(() => harness.Main.IsLibraryVisible && !harness.Viewer.IsOpen);
            returnedToLibrary = true;

            // ライブラリ表示中は、キー操作をビューアへ渡さない
            libraryKeyHandled = Press(Key.Right);

            window.Close();
        });

        Assert.Equal("book.zip - 35Viewer", titleWhileViewing);
        Assert.True(handledRight);
        Assert.Equal("2 / 3", positionAfterRight);
        Assert.Equal("1 / 3", positionAfterLeft);
        Assert.Equal((WindowStyle.None, WindowState.Maximized), fullScreen);
        Assert.Equal((WindowStyle.SingleBorderWindow, WindowState.Normal), restored);
        Assert.True(returnedToLibrary);
        Assert.False(libraryKeyHandled);
    }

    [Fact]
    public async Task 一覧表示は_大量の画像でも見える行だけを生成し_クリックで選択でき_キー操作で現在位置の行へスクロールする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var page = TestImages.Jpeg(40, 30);
        ctx.CreateZip("book.zip", [.. Enumerable.Range(1, 600).Select(i => ($"{i:D4}.jpg", page))]);
        await ctx.RegisterAndScanAsync();
        using var viewer = new ViewerViewModel(
            ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);
        Assert.True(await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct));

        string toggleBefore = string.Empty, toggleAfter = string.Empty;
        bool zoomVisibleBefore = false, zoomVisibleAfter = true, singleViewVisibleAfter = true, listVisibleAfter = false;
        int totalRows = 0, realizedRows = 0, realizedCells = 0, imagesLoaded = 0;
        var selectedAfterClick = string.Empty;
        var positionAfterClick = string.Empty;
        var clickedCellSelectedStyle = false;
        var checkMarkVisible = false;
        var lastCellRealized = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new ViewerView { DataContext = viewer };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1280, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();

            T Find<T>(string automationName)
                where T : FrameworkElement =>
                WpfTestHelper.Descendants<T>(view).First(e => AutomationProperties.GetName(e) == automationName);

            var toggle = Find<Button>("表示方式の切り替え");
            toggleBefore = (string)toggle.Content;
            zoomVisibleBefore = Find<Button>("拡大").IsVisible;

            // 一覧表示に切り替える
            viewer.ToggleViewModeCommand.Execute(null);
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();

            var list = Find<ListBox>("アーカイブ内の画像の一覧");
            toggleAfter = (string)toggle.Content;
            zoomVisibleAfter = Find<Button>("拡大").IsVisible;
            singleViewVisibleAfter = Find<ScrollViewer>("画像の表示領域").IsVisible;
            listVisibleAfter = list.IsVisible;

            totalRows = viewer.PageRows.Count;
            realizedRows = WpfTestHelper.Descendants<ListBoxItem>(list).Count();
            realizedCells = WpfTestHelper.Descendants<Border>(list).Count(b => b.DataContext is PageItem && b.Width == 164);

            // サムネイルは、画面に出た画像だけが背景で生成されて表示される
            WpfTestHelper.PumpUntil(() => WpfTestHelper.Descendants<Image>(list).Any(i => i.Source is not null));
            imagesLoaded = WpfTestHelper.Descendants<Image>(list).Count(i => i.Source is not null);

            // カードをクリックして選択する
            var cell = WpfTestHelper.Descendants<Border>(list)
                .First(b => b.DataContext is PageItem { Index: 2 } && b.Width == 164);
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            };
            cell.RaiseEvent(click);
            WpfTestHelper.Pump();
            selectedAfterClick = string.Join(",", viewer.SelectedIndices);
            positionAfterClick = viewer.PositionText;
            clickedCellSelectedStyle = cell.Background is SolidColorBrush { Color: { R: 0x2F, G: 0x5D, B: 0x9E } };
            checkMarkVisible = WpfTestHelper.Descendants<TextBlock>(cell).Any(t => t.Text == "✓" && t.IsVisible);

            // 末尾へ移動すると、その行まで一覧がスクロールする
            viewer.HandleKey(Key.End, ModifierKeys.None);
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();
            lastCellRealized = WpfTestHelper.Descendants<Border>(list)
                .Any(b => b.DataContext is PageItem { Index: 599 } && b.Width == 164);

            window.Close();
        });

        Assert.Equal("一覧表示（G）", toggleBefore);
        Assert.True(zoomVisibleBefore);
        Assert.Equal("1枚表示（G）", toggleAfter);
        Assert.False(zoomVisibleAfter); // 拡大・縮小は、1枚表示のときだけ
        Assert.False(singleViewVisibleAfter);
        Assert.True(listVisibleAfter);

        Assert.True(totalRows >= 80, $"行にまとめられていません: {totalRows}行");
        Assert.InRange(realizedRows, 1, 20); // 全行ではなく、画面に見える数行だけ
        Assert.InRange(realizedCells, 1, 150);
        Assert.InRange(imagesLoaded, 1, 150);

        Assert.Equal("2", selectedAfterClick);
        Assert.Equal("3 / 600", positionAfterClick);
        Assert.True(clickedCellSelectedStyle, "選択したカードの背景が変わっていません。");
        Assert.True(checkMarkVisible, "選択したカードに「✓」が表示されていません。");
        Assert.True(lastCellRealized, "末尾へ移動しても、その画像が画面に表示されていません。");
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task メインウィンドウのGキーで_1枚表示と一覧表示が切り替わる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)));
        await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        Assert.True(await harness.Viewer.OpenAsync(harness.Library.Archives[0].Id, Ct));
        harness.Main.IsViewerVisible = true;

        bool listAfterG = false, singleAfterSecondG = false, handled = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new MainWindow(harness.Main));
            window.Show();
            WpfTestHelper.Pump();
            var source = PresentationSource.FromVisual(window)!;

            bool Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(args);
                return args.Handled;
            }

            handled = Press(Key.G);
            listAfterG = harness.Viewer.IsListView;
            Press(Key.G);
            singleAfterSecondG = harness.Viewer.IsSingleView;
            window.Close();
        });

        Assert.True(handled);
        Assert.True(listAfterG);
        Assert.True(singleAfterSecondG);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 全画面では_ツールバーを隠して現在位置だけを表示する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)));
        await ctx.RegisterAndScanAsync();
        using var viewer = new ViewerViewModel(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);
        Assert.True(await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct));

        Visibility toolbarNormal = default, toolbarFullScreen = default;
        var positionVisible = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new ViewerView { DataContext = viewer };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 900, Height = 600, Content = view });
            window.Show();
            WpfTestHelper.Pump();

            Border Toolbar() => WpfTestHelper.Descendants<Border>(view).First(b => b.Child is DockPanel);
            toolbarNormal = Toolbar().Visibility;

            viewer.ToggleFullScreenCommand.Execute(null);
            WpfTestHelper.Pump();
            toolbarFullScreen = Toolbar().Visibility;
            positionVisible = WpfTestHelper.Descendants<TextBlock>(view)
                .Any(t => AutomationProperties.GetName(t) == "現在位置" && t.IsVisible && t.Text == "1 / 2");

            window.Close();
        });

        Assert.Equal(Visibility.Visible, toolbarNormal);
        Assert.Equal(Visibility.Collapsed, toolbarFullScreen);
        Assert.True(positionVisible);
        await viewer.CloseAsync();
    }
}
