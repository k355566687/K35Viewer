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
        // 開いたときにキャッシュの大きさを調べ、閉じたときにライブラリ側の通知の購読をやめる
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
