using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.ViewModels;

namespace K35Viewer.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();

        // 一覧の項目やツリーの項目が右クリックを処理済みにしても、右クリックした項目を選択できるようにする
        DetailList.AddHandler(
            MouseRightButtonDownEvent, new MouseButtonEventHandler(OnArchiveListMouseRightButtonDown), handledEventsToo: true);
        FolderTree.AddHandler(
            MouseRightButtonDownEvent, new MouseButtonEventHandler(OnTreeMouseRightButtonDown), handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
        Loaded += async (_, _) =>
        {
            if (DataContext is LibraryViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        };
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LibraryViewModel oldViewModel)
        {
            oldViewModel.ScrollToRowRequested -= OnScrollToRowRequested;
        }

        if (e.NewValue is LibraryViewModel newViewModel)
        {
            newViewModel.ScrollToRowRequested += OnScrollToRowRequested;
        }
    }

    // サムネイル表示: キーで動いた選択のある行を、画面に表示する（表示の準備が整ってから）
    private void OnScrollToRowRequested(object? sender, int rowIndex)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (ViewModel is { } viewModel && rowIndex >= 0 && rowIndex < viewModel.ThumbnailRows.Count)
                {
                    ThumbnailList.ScrollIntoView(viewModel.ThumbnailRows[rowIndex]);
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // 詳細表示: 行のダブルクリックで開く（空白部分やヘッダーのダブルクリックでは開かない）
    private void OnArchiveListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ItemsControl list
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is FrameworkElement { DataContext: ArchiveListItem item })
        {
            Open(item);
            e.Handled = true;
        }
    }

    // 詳細表示: 右クリックした行を選択してから、メニューを開く
    private void OnArchiveListMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ItemsControl list
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is FrameworkElement { DataContext: ArchiveListItem item })
        {
            ViewModel?.OnArchiveRightClicked(item);
        }
    }

    // 詳細表示: Enter（開く）、F2（名前変更）、Delete（削除）。矢印キーによる選択の移動は、一覧自体が処理する
    private void OnArchiveListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel?.HandleKey(e.Key, Keyboard.Modifiers) == true)
        {
            e.Handled = true;
        }
    }

    // サムネイル表示: 矢印キー・Home / End・PageUp / PageDown で選択を移動し、Enter・F2・Delete で操作する
    private void OnThumbnailListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel?.HandleKey(e.Key, Keyboard.Modifiers) == true)
        {
            e.Handled = true;
        }
    }

    // サムネイル表示: カードのクリックで選択し、ダブルクリックで開く。キー操作を受けられるよう、一覧にフォーカスを移す
    private void OnThumbnailCellMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArchiveListItem item })
        {
            ViewModel?.OnThumbnailClicked(item, e.ClickCount);
            _ = ThumbnailList.Focus();
            e.Handled = true;
        }
    }

    // サムネイル表示: 右クリックしたカードを選択してから、メニューを開く（e.Handled にしない）
    private void OnArchiveItemMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArchiveListItem item })
        {
            ViewModel?.OnArchiveRightClicked(item);
            _ = ThumbnailList.Focus();
        }
    }

    private void Open(ArchiveListItem item) => ViewModel?.OpenArchiveCommand.Execute(item);

    // 右クリックメニュー。選択中のアーカイブが対象で、実行できない状態（操作中など）では何もしない
    private void OnOpenMenuClick(object sender, RoutedEventArgs e) => Run(ViewModel?.OpenSelectedCommand);

    private void OnRenameMenuClick(object sender, RoutedEventArgs e) => Run(ViewModel?.RenameSelectedCommand);

    private void OnMoveMenuClick(object sender, RoutedEventArgs e) => Run(ViewModel?.MoveSelectedCommand);

    private void OnDeleteMenuClick(object sender, RoutedEventArgs e) => Run(ViewModel?.DeleteSelectedCommand);

    private void OnShowInExplorerMenuClick(object sender, RoutedEventArgs e) =>
        Run(ViewModel?.ShowSelectedInExplorerCommand);

    private void OnRemoveFolderMenuClick(object sender, RoutedEventArgs e) =>
        Run(ViewModel?.RemoveSelectedFolderCommand);

    private static void Run(IRelayCommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    // ツリー: 右クリックしたフォルダ（クリックした位置に最も近いツリー項目）を選択してから、メニューを開く。
    // 入れ子の項目では、ItemsControl.ContainerFromElement が最上位の項目を返してしまうため、自分で上へ辿る
    private void OnTreeMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        if (element is TreeViewItem { DataContext: FolderTreeItem item })
        {
            item.IsSelected = true;
            ViewModel?.OnFolderSelected(item);
        }
    }

    // サムネイル表示の領域の幅に合わせて、1行に並べる枚数を決め直す(縦スクロールバーの幅は除く)
    private void OnThumbnailListSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel?.UpdateViewportWidth(e.NewSize.Width - SystemParameters.VerticalScrollBarWidth - 8);

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeItem item)
        {
            ViewModel?.OnFolderSelected(item);
        }
    }
}
