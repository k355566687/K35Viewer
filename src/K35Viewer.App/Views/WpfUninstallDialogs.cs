using System.Diagnostics;
using System.Windows;
using K35Viewer.Infrastructure.Uninstall;
using K35Viewer.Services;

namespace K35Viewer.Views;

/// <summary>アンインストールの確認のダイアログ（GitHub の Issue #24）。</summary>
public sealed class WpfUninstallDialogs : IUninstallDialogs
{
    // 設定画面（開いていれば）の上に出す。なければメインウィンドウ
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.MainWindow;

    public UninstallChoice? Prompt(UninstallPlan plan, string defaultBackupFolder, Func<string, string?> validateBackupFolder)
    {
        var dialog = new UninstallDialog(plan, defaultBackupFolder, validateBackupFolder);
        if (Owner is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? new UninstallChoice(dialog.BackupFolder) : null;
    }
}

/// <summary>
/// 消す処理を引き継ぐ控え（プログラムの一式の写し）を、%TEMP% に用意して起動し、このアプリを終了する。
/// 控えは、このアプリの終了を待ってから、プログラムのフォルダとデータを消す。
/// </summary>
public sealed class WpfUninstallLauncher : IUninstallLauncher
{
    public bool TryStart(UninstallTargets targets, out string? reason)
    {
        UninstallHandoffInfo? info = null;
        try
        {
            info = UninstallHandoff.Prepare(targets, Path.GetTempPath());
            using var started = Process.Start(new ProcessStartInfo(info.ExecutablePath)
            {
                Arguments = UninstallHandoff.ArgumentsFor(info.PlanFile, Environment.ProcessId),
                WorkingDirectory = Path.GetDirectoryName(info.ExecutablePath) ?? string.Empty,
                UseShellExecute = false,
            });
            if (started is null)
            {
                UninstallHandoff.TryDeleteCopy(info.CopyDirectory);
                reason = "アンインストールの控えを起動できませんでした。何も削除していません";
                return false;
            }

            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            if (info is not null)
            {
                UninstallHandoff.TryDeleteCopy(info.CopyDirectory);
            }

            reason = $"アンインストールの準備に失敗しました（{ex.Message}）。何も削除していません";
            return false;
        }
    }

    public void ExitApplication() => Application.Current.Shutdown();
}
