using K35Viewer.ViewModels;

namespace K35Viewer.Views;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
