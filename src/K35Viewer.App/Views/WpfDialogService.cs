using System.Windows;
using K35Viewer.Services;
using Microsoft.Win32;

namespace K35Viewer.Views;

public sealed class WpfDialogService : IDialogService
{
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
        var message =
            $"「{parentPath}」は、登録済みの次のフォルダの親フォルダです。{Environment.NewLine}{Environment.NewLine}" +
            $"{children}{Environment.NewLine}{Environment.NewLine}" +
            "配下の登録済みフォルダを、この親フォルダの配下へ統合します。" +
            "アーカイブの記録(閲覧位置など)は引き継がれます。よろしいですか?";

        return MessageBox.Show(
            message,
            "管理フォルダの統合",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
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
        MessageBox.Show(
            $"{fileName} を削除しますか？{Environment.NewLine}ファイルはWindowsのごみ箱へ移動します。",
            "アーカイブの削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmPermanentDelete(string fileName, string reason) =>
        MessageBox.Show(
            $"{fileName} は、ごみ箱へ移動できません。{Environment.NewLine}（{reason}）{Environment.NewLine}{Environment.NewLine}" +
            $"完全に削除しますか？ この操作は元に戻せません。",
            "完全に削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmRemoveLibraryFolder(string folderPath, int archiveCount) =>
        MessageBox.Show(
            $"{folderPath} を管理対象から外しますか？{Environment.NewLine}" +
            $"配下の {archiveCount:N0} 件のアーカイブがライブラリから消えます。{Environment.NewLine}" +
            $"閲覧位置などの記録も削除されます。{Environment.NewLine}" +
            "ファイル自体は削除されません。",
            "管理対象から外す",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private static void SetOwner(Window dialog)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
    }

    public LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, string? updateUnavailableReason)
    {
        var dialog = new ArchiveChangeDialog(archiveName, pendingDeleteCount, updateUnavailableReason);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        _ = dialog.ShowDialog();
        return dialog.Choice;
    }
}
