using System.Windows;
using K35Viewer.ViewModels;

namespace K35Viewer.Views;

/// <summary>設定画面（SCR-03）。メインウィンドウの上に重ねて開く（ShowDialog）。「閉じる」と Esc で閉じる（IsCancel）。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
