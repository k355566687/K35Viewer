using System.Windows;

namespace K35Viewer.Views;

/// <summary>
/// 確認・お知らせのダイアログ。Windows 標準のメッセージボックスはテーマ（ダーク）に追従しないため、その代わりに使う。
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <param name="title">タイトル。</param>
    /// <param name="message">本文（何をするか）。</param>
    /// <param name="detail">補足（結果や注意）。null なら表示しない。</param>
    /// <param name="confirmText">操作のボタンの文字（例: 「削除」）。</param>
    /// <param name="cancelText">キャンセルのボタンの文字。null ならお知らせ（「OK」だけ）として、操作のボタンを既定にする。</param>
    public ConfirmDialog(string title, string message, string? detail, string confirmText, string? cancelText = "キャンセル")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        DetailText.Text = detail ?? string.Empty;
        DetailText.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
        System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, confirmText);

        if (cancelText is null)
        {
            // お知らせ: 「OK」だけ。Enter と Esc で閉じる
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsDefault = false;
            CancelButton.IsCancel = false;
            ConfirmButton.IsDefault = true;
            ConfirmButton.IsCancel = true;
        }
        else
        {
            CancelButton.Content = cancelText;
            System.Windows.Automation.AutomationProperties.SetName(CancelButton, cancelText);
        }
    }

    /// <summary>操作のボタンが押されたか。キャンセル・閉じた場合は false。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>確認を表示し、操作が選ばれたら true。</summary>
    public static bool Ask(Window? owner, string title, string message, string? detail, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, detail, confirmText);
        SetOwner(dialog, owner);
        _ = dialog.ShowDialog();
        return dialog.Confirmed;
    }

    /// <summary>お知らせを表示する（「OK」だけ）。</summary>
    public static void Inform(Window? owner, string title, string message, string? detail = null)
    {
        var dialog = new ConfirmDialog(title, message, detail, "OK", cancelText: null);
        SetOwner(dialog, owner);
        _ = dialog.ShowDialog();
    }

    private static void SetOwner(Window dialog, Window? owner)
    {
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }
}
