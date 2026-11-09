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

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeItem item && DataContext is LibraryViewModel viewModel)
        {
            viewModel.OnFolderSelected(item);
        }
    }
}
