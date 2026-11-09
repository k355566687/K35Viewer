using System.Windows;
using System.Windows.Controls;
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
