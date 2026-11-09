using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K35Viewer.ViewModels;

namespace K35Viewer.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is LibraryViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        };
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

    // 詳細表示: 選択中の行をEnterで開く
    private void OnArchiveListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListView { SelectedItem: ArchiveListItem item })
        {
            Open(item);
            e.Handled = true;
        }
    }

    // サムネイル表示: カードのダブルクリックで開く
    private void OnThumbnailCellMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: ArchiveListItem item })
        {
            Open(item);
            e.Handled = true;
        }
    }

    private void Open(ArchiveListItem item)
    {
        if (DataContext is LibraryViewModel viewModel)
        {
            viewModel.OpenArchiveCommand.Execute(item);
        }
    }

    // サムネイル表示の領域の幅に合わせて、1行に並べる枚数を決め直す(縦スクロールバーの幅は除く)
    private void OnThumbnailListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel)
        {
            viewModel.UpdateViewportWidth(e.NewSize.Width - SystemParameters.VerticalScrollBarWidth - 8);
        }
    }

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeItem item && DataContext is LibraryViewModel viewModel)
        {
            viewModel.OnFolderSelected(item);
        }
    }
}
