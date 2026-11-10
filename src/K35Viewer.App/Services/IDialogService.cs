namespace K35Viewer.Services;

/// <summary>未確定の変更があるアーカイブを離れるときの、ユーザーの選択（DLG-01）。</summary>
public enum LeaveChoice
{
    /// <summary>変更を反映してアーカイブを更新してから離れる。</summary>
    Update,

    /// <summary>変更を破棄して離れる。ZIP本体は変更しない。</summary>
    Discard,

    /// <summary>離れるのをやめて、ビューアに留まる。</summary>
    Cancel,
}

/// <summary>ユーザーに選択・確認を求めるダイアログ。ViewModelからWPFのダイアログを直接呼ばないための境界。</summary>
public interface IDialogService
{
    /// <summary>フォルダ選択ダイアログを表示する。キャンセルされた場合は null。</summary>
    string? PickFolder();

    /// <summary>既存の登録フォルダを、追加する親フォルダの配下へ統合してよいか確認する(UC-01)。</summary>
    bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths);

    /// <summary>
    /// 削除予約が残っているアーカイブを離れてよいか確認する(DLG-01、UC-11)。
    /// <paramref name="updateUnavailableReason"/> が null でなければ「更新する」を選べず、その理由を表示する
    /// （アーカイブが外部で変更された、ごみ箱を使えない保存先、など）。
    /// ダイアログを閉じた場合は <see cref="LeaveChoice.Cancel"/>。
    /// </summary>
    LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, string? updateUnavailableReason);

    /// <summary>
    /// 新しいファイル名の入力を求める（UC-12）。<paramref name="validate"/> は入力が不正なときにその説明を返し（有効なら null）、
    /// ダイアログは説明を表示して閉じない。キャンセルされた場合は null。
    /// </summary>
    string? PromptRename(string currentFileName, Func<string, string?> validate);

    /// <summary>移動先のフォルダを選ぶ（UC-13）。キャンセルされた場合は null。</summary>
    string? PickMoveDestination(string initialDirectory);

    /// <summary>アーカイブの削除を確認する（DLG-02）。ファイルはWindowsのごみ箱へ移動する。</summary>
    bool ConfirmDeleteArchive(string fileName);

    /// <summary>
    /// ごみ箱へ移動できない保存先のアーカイブを、完全に削除してよいか確認する（UC-14の異常フロー）。
    /// 元に戻せないことを明示し、既定の選択はキャンセル。
    /// </summary>
    bool ConfirmPermanentDelete(string fileName, string reason);

    /// <summary>管理フォルダを管理対象から外すことを確認する（DLG-03）。ファイル自体は削除されない。</summary>
    bool ConfirmRemoveLibraryFolder(string folderPath, int archiveCount);
}
