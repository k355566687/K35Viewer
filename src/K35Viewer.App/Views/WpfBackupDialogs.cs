using System.Diagnostics;
using System.Windows;
using K35Viewer.Infrastructure;
using K35Viewer.Services;
using Microsoft.Win32;

namespace K35Viewer.Views;

/// <summary>復元のダイアログ。確認は、テーマに追従するアプリ内のダイアログで行い、既定の選択（Enter）と Esc はキャンセル。</summary>
public sealed class WpfBackupDialogs : IBackupDialogs
{
    // 設定画面（開いていれば）の上に出す。なければメインウィンドウ
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.MainWindow;

    public string? PickBackupFile(string initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "復元するバックアップを選択",
            Filter = "35Viewer のバックアップ (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool ConfirmRestore(DateTime createdAt) =>
        ConfirmDialog.Ask(
            Owner,
            "バックアップから復元",
            string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{createdAt:yyyy/MM/dd HH:mm} の状態に戻しますか？"),
            "今のデータは、戻す前に自動でバックアップします。アプリを再起動して戻します。サムネイルは作り直します。",
            "復元");
}

/// <summary>新しいプロセスを起動し、このアプリを終了する。</summary>
public sealed class WpfAppRestarter : IAppRestarter
{
    public bool TryRestart()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                return false;
            }

            using var started = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = RestartArguments.ForProcess(Environment.ProcessId),
                WorkingDirectory = AppContext.BaseDirectory,
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