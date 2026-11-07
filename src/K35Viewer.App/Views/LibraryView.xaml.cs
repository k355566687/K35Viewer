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
                await viewModel.LoadAsync();
            }
        };
    }
}
