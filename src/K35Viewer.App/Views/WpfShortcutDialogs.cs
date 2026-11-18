using System.Windows;
using K35Viewer.Services;

namespace K35Viewer.Views;

/// <summary>ショートカットに関する、利用者に聞くダイアログ（GitHub の Issue #23）。</summary>
public sealed class WpfShortcutDialogs : IShortcutDialogs
{
    // 設定画面（開いていれば）の上に出す。なければメインウィンドウ
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.MainWindow;

    public ShortcutRequest? PromptFirstRun()
    {
        var dialog = new ShortcutPromptDialog();
        if (Owner is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public bool ConfirmTemporaryLocation(string executablePath) =>
        ConfirmDialog.Ask(
            Owner,
            "ショートカットを作る",
            "今のアプリは、一時的な場所にあります。ここへのショートカットを作りますか？",
            $"{executablePath}{Environment.NewLine}{Environment.NewLine}この場所は、あとで消えることがあります。そのときは、ショートカットが使えなくなります。",
            "作る");
}
