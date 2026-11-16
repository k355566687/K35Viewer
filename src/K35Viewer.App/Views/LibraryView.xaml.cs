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
        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += async (_, _) =>
        {
            ApplyCompactRows();
            if (DataContext is LibraryViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        };
    }

    /// <summary>詳細表示の行の最小の高さ。テーマの既定（約40）より詰めて、1画面に表示できる行を増やす。</summary>
    private const double CompactRowMinHeight = 30;

    /// <summary>
    /// 詳細表示の行を詰める。テーマの行の見た目（選択の表示など）を引き継ぐため、テーマが実際に当てている行のスタイルを
    /// 元にして、高さと余白だけを変える。XAML の BasedOn では、読み込みの時点でテーマのスタイルを見つけられない場合があるため、
    /// 画面に置かれてから行う。
    /// </summary>
    private void ApplyCompactRows()
    {
        if (_compactRowsApplied)
        {
            return;
        }

        _compactRowsApplied = true;

        // テーマ（Fluent）は、一覧のスタイルで行のスタイル（ItemContainerStyle）を指定している。あればそれを、なければ
        // 行の既定のスタイルを元にする
        var baseStyle = DetailList.ItemContainerStyle ?? DetailList.TryFindResource(typeof(ListViewItem)) as Style;
        var style = new Style(typeof(ListViewItem), baseStyle);
        style.Setters.Add(new Setter(MinHeightProperty, CompactRowMinHeight));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        DetailList.ItemContainerStyle = style;
    }

    private bool _compactRowsApplied;

    // ビューアから戻ったとき（前後のアーカイブへ移っていた場合を含む）に、選ばれているアーカイブを画面に表示する。
    // プログラムから選んだだけでは、一覧はスクロールしないため（キー操作のときだけ、スクロールしている）
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ScrollToSelected();
        }
    }

    /// <summary>選ばれているアーカイブ（なければ何もしない）を、表示中の一覧（詳細 / サムネイル）の画面内へスクロールする。</summary>
    internal void ScrollToSelected()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 表示の準備（レイアウト）が整ってから行う
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (ViewModel is not { SelectedArchive: { } selected } viewModel)
                {
                    return;
                }

                if (viewModel.IsThumbnailView)
                {
                    var row = viewModel.GetSelectedRowIndex();
                    if (row >= 0 && row < viewModel.ThumbnailRows.Count)
                    {
                        ThumbnailList.ScrollIntoView(viewModel.ThumbnailRows[row]);
                    }
                }
                else
                {
                    DetailList.ScrollIntoView(selected);
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
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
            UpdateThumbnailColumns();
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

    private void OnConvertToZipMenuClick(object sender, RoutedEventArgs e) => Run(ViewModel?.ConvertSelectedToZipCommand);

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

    /// <summary>ファイル名の列の最小の幅。これより狭くなる場合だけ、横スクロールが出る。</summary>
    private const double MinFileNameColumnWidth = 160;

    // 詳細表示: ファイル名の列を、残りの幅いっぱいにする（他の列は固定の幅。横スクロールを出さない）
    private void OnDetailListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DetailList.View is not GridView grid)
        {
            return;
        }

        var others = grid.Columns.Where(c => !ReferenceEquals(c, FileNameColumn)).Sum(c => c.ActualWidth);
        var available = e.NewSize.Width - others - SystemParameters.VerticalScrollBarWidth - DetailList.BorderThickness.Left
            - DetailList.BorderThickness.Right - DetailList.Padding.Left - DetailList.Padding.Right - 12;
        FileNameColumn.Width = Math.Max(MinFileNameColumnWidth, available);
    }

    // サムネイル表示の領域の幅に合わせて、1行に並べる枚数を決め直す(縦スクロールバーの幅は除く)
    private void OnThumbnailListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumbnailColumns();

    // 一覧が表示されたとき（詳細表示から切り替えたとき）にも決め直す。大きさは変わらないため、SizeChanged が起きない
    private void OnThumbnailListIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateThumbnailColumns();

    /// <summary>
    /// 一覧の今の幅から、1行に並べる枚数を決め直す。起動直後は、ViewModel がつながる前に大きさが決まることがあるため、
    /// 大きさの変化だけでなく、ViewModel がつながったとき・一覧が表示されたときにも行う。
    /// </summary>
    private void UpdateThumbnailColumns()
    {
        if (ThumbnailList.ActualWidth > 0)
        {
            ViewModel?.UpdateViewportWidth(UsableWidth(ThumbnailList, ThumbnailList.ActualWidth));
        }
    }

    /// <summary>
    /// 一覧の中で、カードを並べるのに使える幅。一覧の枠と内側の余白、縦スクロールバーの幅を除く
    /// （テーマによって、一覧の内側に余白がある）。
    /// </summary>
    internal static double UsableWidth(Control list, double width) =>
        width - list.BorderThickness.Left - list.BorderThickness.Right - list.Padding.Left - list.Padding.Right
        - SystemParameters.VerticalScrollBarWidth - 4;

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeItem item)
        {
            ViewModel?.OnFolderSelected(item);
        }
    }
}
