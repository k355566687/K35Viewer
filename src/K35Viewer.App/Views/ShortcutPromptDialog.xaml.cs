using System.Windows;
using K35Viewer.Services;

namespace K35Viewer.Views;

/// <summary>
/// はじめて起動したときの、ショートカットの確認（GitHub の Issue #23）。作る場所（スタートメニュー、デスクトップ）を、
/// 選んでもらう。既定は、スタートメニューだけがオン。どちらもオフのときは、「作る」を押せない。
/// </summary>
public partial class ShortcutPromptDialog : Window
{
    public ShortcutPromptDialog()
    {
        InitializeComponent();
        UpdateState();
    }

    /// <summary>選ばれたもの。「今はしない」の場合は null。</summary>
    public ShortcutRequest? Request { get; private set; }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        // 初期化の途中（XAML のイベントが先に届く場合）は、まだ項目がない
        if (StartMenuBox is null || DesktopBox is null || OkButton is null)
        {
            return;
        }

        OkButton.IsEnabled = StartMenuBox.IsChecked == true || DesktopBox.IsChecked == true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Request = new ShortcutRequest(StartMenuBox.IsChecked == true, DesktopBox.IsChecked == true);
        DialogResult = true;
    }
}
