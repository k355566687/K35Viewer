using System.Globalization;
using System.Windows;
using K35Viewer.Services;

namespace K35Viewer.Views;

/// <summary>
/// アーカイブ変更確認ダイアログ（DLG-01）。削除予約が残っているアーカイブを離れるときに、
/// 「更新する」「変更を破棄」「キャンセル」から選ぶ。誤って破壊的な操作にならないよう、既定はキャンセル。
/// </summary>
public partial class ArchiveChangeDialog : Window
{
    public ArchiveChangeDialog(string archiveName, int pendingDeleteCount, bool canUpdate)
    {
        InitializeComponent();
        MessageText.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{archiveName} で{pendingDeleteCount:N0}枚の画像が削除対象になっています。{Environment.NewLine}アーカイブを更新しますか？");
        UpdateButton.IsEnabled = canUpdate;
        NoteText.Text = canUpdate
            ? "「変更を破棄」を選ぶと、削除予約を取り消して元のアーカイブのまま離れます。"
            : "アーカイブの更新はまだ利用できません。「変更を破棄」を選ぶと、削除予約を取り消して元のアーカイブのまま離れます。";
    }

    /// <summary>ユーザーの選択。ダイアログを閉じた場合はキャンセル。</summary>
    public LeaveChoice Choice { get; private set; } = LeaveChoice.Cancel;

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        Choice = LeaveChoice.Update;
        DialogResult = true;
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        Choice = LeaveChoice.Discard;
        DialogResult = true;
    }
}
