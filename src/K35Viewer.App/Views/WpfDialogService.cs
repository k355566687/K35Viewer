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

    public LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, bool canUpdate)
    {
        var dialog = new ArchiveChangeDialog(archiveName, pendingDeleteCount, canUpdate);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        _ = dialog.ShowDialog();
        return dialog.Choice;
    }
}
