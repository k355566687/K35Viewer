using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using K35Viewer.ViewModels;
using K35Viewer.Views;

namespace K35Viewer.Tests.Services;

/// <summary>実際のWPFのライブラリ画面で、選択・キー操作・右クリックメニュー・ボタンが操作に結びついていること。</summary>
[Collection(WpfViewsGroup.Name)]
public class LibraryViewOperationsTests
{
    private static T Find<T>(DependencyObject root, string automationName)
        where T : FrameworkElement =>
        WpfTestHelper.Descendants<T>(root).First(e => AutomationProperties.GetName(e) == automationName);

    private static KeyEventArgs KeyDown(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
    }

    /// <summary>
    /// 右クリックを、実際の入力と同じ形で発生させる。WPFでは、右ボタンの押下は MouseDown として経路をたどり、
    /// 各要素で MouseRightButtonDown に変換される。
    /// </summary>
    private static void RightClick(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = Mouse.MouseDownEvent,
        });

    private static StackPanel Card(ListBox list, string fileName) =>
        WpfTestHelper.Descendants<StackPanel>(list)
            .First(p => p.Width == 164 && p.DataContext is ArchiveListItem item && item.FileName == fileName);

    [Fact]
    public async Task サムネイル表示では_クリックで選択され_キーで選択が動き_右クリックメニューから操作できる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        var library = harness.Library;
        library.IsThumbnailView = true;
        harness.Dialogs.RenameInput = null;

        string selectedAfterClick = string.Empty, selectedAfterKey = string.Empty, selectedAfterRightClick = string.Empty;
        var checkMarkVisibleOnSelected = false;
        var checkMarkVisibleOnOther = true;
        var highlighted = false;
        var menuHeaders = new List<string>();
        var hasFocus = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new LibraryView { DataContext = library };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1280, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();
            var list = Find<ListBox>(view, "アーカイブのサムネイル一覧");

            // クリックで選択される
            Card(list, "b.zip").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            });
            WpfTestHelper.Pump();
            selectedAfterClick = library.SelectedArchive?.FileName ?? string.Empty;
            hasFocus = list.IsKeyboardFocusWithin;
            var selectedCard = Card(list, "b.zip");
            checkMarkVisibleOnSelected = WpfTestHelper.Descendants<TextBlock>(selectedCard).Any(t => t.Text == "✓ 選択中" && t.IsVisible);
            checkMarkVisibleOnOther = WpfTestHelper.Descendants<TextBlock>(Card(list, "a.zip")).Any(t => t.Text == "✓ 選択中" && t.IsVisible);
            highlighted = selectedCard.Background is SolidColorBrush selectedBrush
                && selectedBrush.Color == ((SolidColorBrush)window.FindResource("K35.SelectionFillBrush")).Color;

            // キー操作で選択が動く
            list.RaiseEvent(KeyDown(list, Key.Right));
            WpfTestHelper.Pump();
            selectedAfterKey = library.SelectedArchive?.FileName ?? string.Empty;

            // 右クリックで選択され、メニューには操作が並ぶ
            var cardD = Card(list, "d.zip");
            RightClick(WpfTestHelper.Descendants<TextBlock>(cardD).First());
            WpfTestHelper.Pump();
            selectedAfterRightClick = library.SelectedArchive?.FileName ?? string.Empty;
            menuHeaders.AddRange(cardD.ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header));

            // メニューの「名前を変更」で、名前変更のダイアログが開く
            cardD.ContextMenu.Items.OfType<MenuItem>().First(m => (string)m.Header == "名前を変更 (F2)")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            WpfTestHelper.Pump();

            window.Close();
        });

        Assert.Equal("b.zip", selectedAfterClick);
        Assert.True(hasFocus, "クリックしたあと、一覧がキー操作を受けられる状態になっていません。");
        Assert.True(checkMarkVisibleOnSelected, "選択したカードに「選択中」の表示がありません。");
        Assert.False(checkMarkVisibleOnOther);
        Assert.True(highlighted, "選択したカードの背景が変わっていません。");
        Assert.Equal("c.zip", selectedAfterKey);
        Assert.Equal("d.zip", selectedAfterRightClick);
        Assert.Equal(["開く", "名前を変更 (F2)", "別のフォルダへ移動…", "削除 (Delete)", "ZIP に変換…", "エクスプローラーで表示"], menuHeaders); // 「ZIP に変換…」は、RAR / 7z のときだけ表示される
        Assert.Equal(["d.zip"], harness.Dialogs.RenamePrompts);
    }

    [Fact]
    public async Task 詳細表示では_一覧の選択とViewModelの選択が同期し_キーと右クリックメニューで操作できる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        var library = harness.Library;
        harness.Dialogs.RenameInput = null;
        harness.Dialogs.DeleteConfirmed = false;

        string viewSelectionAfterViewModelChange = string.Empty, viewModelSelectionAfterListChange = string.Empty;
        string selectedAfterRightClick = string.Empty;
        var menuHeaders = new List<string>();
        var buttonStates = new Dictionary<string, bool>();

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new LibraryView { DataContext = library };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1280, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();
            var list = Find<ListView>(view, "アーカイブ一覧");

            // 操作ボタンは、選択がなければ使えない
            string[] buttonNames =
            [
                "選択中のアーカイブを開く",
                "選択中のアーカイブの名前を変更",
                "選択中のアーカイブを別のフォルダへ移動",
                "選択中のアーカイブを削除",
                "選択中のアーカイブをエクスプローラーで表示",
            ];
            foreach (var name in buttonNames)
            {
                buttonStates[name + "(選択前)"] = Find<Button>(view, name).IsEnabled;
            }

            library.SelectedArchive = library.Archives[1];
            WpfTestHelper.Pump();
            viewSelectionAfterViewModelChange = (list.SelectedItem as ArchiveListItem)?.FileName ?? string.Empty;
            foreach (var name in buttonNames)
            {
                buttonStates[name + "(選択後)"] = Find<Button>(view, name).IsEnabled;
            }

            // 利用者の操作と同じく、バインディングを保ったまま選択する（代入すると、バインディングが外れる）
            list.SetCurrentValue(Selector.SelectedItemProperty, library.Archives[2]);
            WpfTestHelper.Pump();
            viewModelSelectionAfterListChange = library.SelectedArchive?.FileName ?? string.Empty;

            // 右クリックした行が選択される
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(library.Archives[3]);
            // 実際のマウス操作と同じく、行の中身（テキスト）が操作の対象になる
            RightClick(WpfTestHelper.Descendants<TextBlock>(row).First());
            WpfTestHelper.Pump();
            selectedAfterRightClick = library.SelectedArchive?.FileName ?? string.Empty;
            menuHeaders.AddRange(list.ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header));

            // F2 と Delete で操作が始まる
            list.RaiseEvent(KeyDown(list, Key.F2));
            WpfTestHelper.Pump();
            list.RaiseEvent(KeyDown(list, Key.Delete));
            WpfTestHelper.Pump();

            window.Close();
        });

        Assert.All(buttonStates.Where(s => s.Key.EndsWith("(選択前)", StringComparison.Ordinal)), s => Assert.False(s.Value, s.Key));
        Assert.All(buttonStates.Where(s => s.Key.EndsWith("(選択後)", StringComparison.Ordinal)), s => Assert.True(s.Value, s.Key));
        Assert.Equal("b.zip", viewSelectionAfterViewModelChange);
        Assert.Equal("c.zip", viewModelSelectionAfterListChange);
        Assert.Equal("d.zip", selectedAfterRightClick);
        Assert.Equal(["開く", "名前を変更 (F2)", "別のフォルダへ移動…", "削除 (Delete)", "ZIP に変換…", "エクスプローラーで表示"], menuHeaders); // 「ZIP に変換…」は、RAR / 7z のときだけ表示される
        Assert.Equal(["d.zip"], harness.Dialogs.RenamePrompts);
        Assert.Equal(["d.zip"], harness.Dialogs.DeleteConfirmations);
    }

    [Fact]
    public async Task 管理対象から外すボタンは_管理フォルダを選んだときだけ使える()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        var library = harness.Library;
        var before = true;
        var onRoot = false;
        var onSub = true;
        var subSelectedByRightClick = false;
        var menuHeaders = new List<string>();

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new LibraryView { DataContext = library };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1280, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();
            var button = Find<Button>(view, "選択中の管理フォルダを管理対象から外す");
            var tree = Find<TreeView>(view, "フォルダツリー");

            before = button.IsEnabled; // 「すべて」が選ばれている
            var root = library.FolderRoots.Single(f => f.IsLibraryRoot);
            root.IsSelected = true;
            WpfTestHelper.Pump();
            onRoot = button.IsEnabled;

            // 入れ子のサブフォルダを右クリックすると、その項目が選ばれる（最上位の項目ではない）
            var sub = root.Children.First(c => c.Header == "sub");
            root.IsExpanded = true;
            WpfTestHelper.Pump();
            var subLabel = WpfTestHelper.Descendants<TextBlock>(tree).First(t => t.Text == "sub");
            RightClick(subLabel);
            WpfTestHelper.Pump();
            subSelectedByRightClick = sub.IsSelected && ReferenceEquals(library.SelectedFolder, sub);
            onSub = button.IsEnabled;
            menuHeaders.AddRange(tree.ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header));

            window.Close();
        });

        Assert.False(before);
        Assert.True(onRoot);
        Assert.True(subSelectedByRightClick, "右クリックしたサブフォルダが選択されていません。");
        Assert.False(onSub);
        Assert.Equal(["管理対象から外す"], menuHeaders);
    }
}
