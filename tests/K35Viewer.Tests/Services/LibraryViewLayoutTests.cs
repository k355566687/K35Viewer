using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using K35Viewer.ViewModels;
using K35Viewer.Views;

namespace K35Viewer.Tests.Services;

/// <summary>
/// 画面の配置（見た目の確認で見つかった問題の再発防止）。実際のWPFの画面を、アプリと同じテーマで配置して確かめる。
/// </summary>
[Collection(WpfViewsGroup.Name)]
public class LibraryViewLayoutTests
{
    private static T Find<T>(DependencyObject root, string automationName)
        where T : FrameworkElement =>
        WpfTestHelper.Descendants<T>(root).First(e => AutomationProperties.GetName(e) == automationName);

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    [Theory]
    [InlineData(1280)]
    [InlineData(1000)]
    [InlineData(1500)]
    public async Task サムネイル表示では_カードが一覧の中に収まり_間隔が均等になる(int windowWidth)
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        var library = harness.Library;
        List<Rect> cards = [];
        Rect viewport = default;
        var columns = 0;

        WpfTestHelper.RunOnStaThread(() =>
        {
            // 実際のアプリと同じく、画面の大きさが決まってから ViewModel がつながる（メインウィンドウのバインディング）
            var view = new LibraryView();
            var window = WpfTestHelper.WithAppTheme(new Window { Width = windowWidth, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();
            view.DataContext = library;
            WpfTestHelper.Pump();

            // 実際の操作と同じく、詳細表示で開いてから、サムネイル表示に切り替える
            library.IsThumbnailView = true;
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();

            var list = Find<ListBox>(view, "アーカイブのサムネイル一覧");
            var scroll = WpfTestHelper.Descendants<ScrollViewer>(list).First();
            viewport = new Rect(BoundsIn(scroll, list).TopLeft, new Size(scroll.ViewportWidth, scroll.ViewportHeight));
            cards = [.. WpfTestHelper.Descendants<StackPanel>(list)
                .Where(p => p.Width == 164 && p.DataContext is ArchiveListItem)
                .Select(p => BoundsIn(p, list))
                .OrderBy(r => r.Left)];
            columns = library.ColumnCount;
            window.Close();
        });

        // 1行目のカード（4件。幅が狭ければ、1行目は1行の枚数ぶん）
        Assert.InRange(columns, 1, 20);
        cards = [.. cards.Where(c => Math.Abs(c.Top - cards.Min(x => x.Top)) < 1).OrderBy(c => c.Left)];
        Assert.Equal(Math.Min(4, columns), cards.Count);
        Assert.True(cards.Count >= 2, "間隔を確かめるには、1行に2枚以上必要です");
        Assert.All(cards, card => Assert.True(
            card.Right <= viewport.Right + 0.5 && card.Left >= viewport.Left - 0.5,
            $"カードが一覧の外にはみ出しています: {card} / 表示領域 {viewport}"));

        // エクスプローラーと同じく、カードの間隔が均等（余った幅を、カードの間へ分ける）
        var gaps = cards.Zip(cards.Skip(1), (a, b) => b.Left - a.Right).ToList();
        Assert.True(gaps.Max() - gaps.Min() < 1.0, $"間隔が均等ではありません: {string.Join(", ", gaps)}");
        Assert.True(gaps.Min() >= 12 - 0.5, $"カードの間が狭すぎます: {gaps.Min()}");

        // もう1枚入る幅が残っていない（入るなら、1行の枚数が増えているはず）
        var cell = cards[1].Left - cards[0].Left;
        Assert.True(viewport.Width - (cell * columns) < LibraryViewModel.ThumbnailCellWidth,
            $"もう1枚入る幅があるのに、枚数が増えていません: 表示領域 {viewport.Width}, {columns}枚, 1枚 {cell}");
    }

    [Fact]
    public async Task 詳細表示では_列が一覧の幅に収まり_横スクロールが出ない_保存場所は管理フォルダからの相対で表示する()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        var library = harness.Library;
        var horizontal = Visibility.Visible;
        var locations = new List<string>();
        double rowHeight = 0;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new LibraryView { DataContext = library };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1280, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();

            var list = Find<ListView>(view, "アーカイブ一覧");
            horizontal = WpfTestHelper.Descendants<ScrollViewer>(list).First().ComputedHorizontalScrollBarVisibility;
            var rows = WpfTestHelper.Descendants<ListViewItem>(list).ToList();
            rowHeight = rows[0].ActualHeight;
            locations.AddRange(rows.Select(r => ((ArchiveListItem)r.DataContext).LocationText));
            window.Close();
        });

        Assert.Equal(Visibility.Collapsed, horizontal);
        Assert.InRange(rowHeight, 1, 36); // 行を詰めた（以前は約42）
        var root = Path.GetFileName(ctx.Workspace);
        Assert.Equal([root, root, root, root + @"\sub"], locations); // a.zip, b.zip, c.zip, sub\d.zip
    }

    [Fact]
    public void 確認のダイアログは_既定とEscがキャンセルで_お知らせはOKだけになる()
    {
        bool cancelIsDefault = false, cancelIsCancel = false, confirmIsDefault = true, closedConfirmed = true;
        bool informCancelVisible = true, informOkDefault = false, informOkCancel = false;
        string confirmText = string.Empty, detail = string.Empty;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var ask = new ConfirmDialog("アーカイブの削除", "a.zip を削除しますか？", "ごみ箱へ移動します。", "削除");
            var cancel = (Button)ask.FindName("CancelButton");
            var confirm = (Button)ask.FindName("ConfirmButton");
            cancelIsDefault = cancel.IsDefault;
            cancelIsCancel = cancel.IsCancel;
            confirmIsDefault = confirm.IsDefault;
            confirmText = (string)confirm.Content;
            detail = ((TextBlock)ask.FindName("DetailText")).Text;
            closedConfirmed = ask.Confirmed;

            var inform = new ConfirmDialog("35Viewer", "お知らせ", null, "OK", cancelText: null);
            informCancelVisible = ((Button)inform.FindName("CancelButton")).IsVisible
                || ((Button)inform.FindName("CancelButton")).Visibility == Visibility.Visible;
            var ok = (Button)inform.FindName("ConfirmButton");
            informOkDefault = ok.IsDefault;
            informOkCancel = ok.IsCancel;
        });

        Assert.True(cancelIsDefault);
        Assert.True(cancelIsCancel);
        Assert.False(confirmIsDefault); // 誤って Enter で削除しない
        Assert.Equal("削除", confirmText);
        Assert.Equal("ごみ箱へ移動します。", detail);
        Assert.False(closedConfirmed); // 何も押さずに閉じたら、確認されていない
        Assert.False(informCancelVisible);
        Assert.True(informOkDefault);
        Assert.True(informOkCancel);
    }
}
