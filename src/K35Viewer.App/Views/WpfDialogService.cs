using System.Globalization;
using System.Windows;
using K35Viewer.Services;
using K35Viewer.ViewModels;
using Microsoft.Win32;

namespace K35Viewer.Views;

/// <summary>
/// WPFのダイアログ。確認は、テーマ（ダーク）に追従するアプリ内のダイアログ（<see cref="ConfirmDialog"/> など）で行う。
/// どの確認も、既定の選択（Enter）と Esc はキャンセル。
/// </summary>
public sealed class WpfDialogService(Func<SettingsViewModel> createSettings) : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public void ShowSettings()
    {
        var dialog = new SettingsWindow(createSettings());
        SetOwner(dialog);
        _ = dialog.ShowDialog();
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "管理フォルダを選択",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths)
    {
        var children = string.Join(Environment.NewLine, childPaths.Select(p => "  " + p));
        return ConfirmDialog.Ask(
            Owner,
            "管理フォルダの統合",
            $"「{parentPath}」は、登録済みの次のフォルダの親フォルダです。{Environment.NewLine}{children}",
            "配下の登録済みフォルダを、この親フォルダの配下へ統合します。アーカイブの記録（閲覧位置など）は引き継がれます。",
            "統合して追加");
    }

    public string? PromptRename(string currentFileName, Func<string, string?> validate)
    {
        var dialog = new RenameDialog(currentFileName, validate);
        SetOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.NewName : null;
    }

    public string? PickMoveDestination(string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "移動先のフォルダを選択",
            Multiselect = false,
            InitialDirectory = initialDirectory,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool ConfirmDeleteArchive(string fileName) =>
        ConfirmDialog.Ask(
            Owner,
            "アーカイブの削除",
            $"{fileName} を削除しますか？",
            "ファイルはWindowsのごみ箱へ移動します。",
            "削除");

    public bool ConfirmPermanentDelete(string fileName, string reason) =>
        ConfirmDialog.Ask(
            Owner,
            "完全に削除",
            $"{fileName} は、ごみ箱へ移動できません（{reason}）。完全に削除しますか？",
            "この操作は元に戻せません。",
            "完全に削除");

    public bool ConfirmRemoveLibraryFolder(string folderPath, int archiveCount) =>
        ConfirmDialog.Ask(
            Owner,
            "管理対象から外す",
            string.Create(CultureInfo.CurrentCulture, $"{folderPath} を管理対象から外しますか？"),
            string.Create(
                CultureInfo.CurrentCulture,
                $"配下の {archiveCount:N0} 件のアーカイブがライブラリから消えます。閲覧位置などの記録も削除されます。ファイル自体は削除されません。"),
            "外す");

    public LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, string? updateUnavailableReason)
    {
        var dialog = new ArchiveChangeDialog(archiveName, pendingDeleteCount, updateUnavailableReason);
        SetOwner(dialog);
        _ = dialog.ShowDialog();
        return dialog.Choice;
    }

    private static void SetOwner(Window dialog)
    {
        if (Owner is { IsVisible: true } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
    }
}
