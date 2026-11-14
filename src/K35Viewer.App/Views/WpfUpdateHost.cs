using System.Diagnostics;
using System.Windows;
using K35Viewer.Services;

namespace K35Viewer.Views;

/// <summary>更新の確認のダイアログ。既定の選択（Enter）と Esc は、キャンセル。</summary>
public sealed class WpfUpdateDialogs : IUpdateDialogs
{
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.MainWindow;

    public bool ConfirmUpdate(string displayVersion) =>
        ConfirmDialog.Ask(
            Owner,
            "アプリの更新",
            $"{displayVersion} に更新しますか？",
            "アプリのデータをバックアップしてから、ダウンロードして、アプリを再起動して入れ替えます。ダウンロードが終わるまで、アプリは使えます。",
            "更新");
}

/// <summary>新しい版の実行ファイルを起動して、このアプリを終了する。</summary>
public sealed class WpfUpdateLauncher : IUpdateLauncher
{
    public bool TryLaunchAndExit(string executable, string arguments)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
            });
            if (started is null)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }

        Application.Current.Shutdown();
        return true;
    }
}