namespace K35Viewer.Services;

/// <summary>ユーザーに選択・確認を求めるダイアログ。ViewModelからWPFのダイアログを直接呼ばないための境界。</summary>
public interface IDialogService
{
    /// <summary>フォルダ選択ダイアログを表示する。キャンセルされた場合は null。</summary>
    string? PickFolder();

    /// <summary>既存の登録フォルダを、追加する親フォルダの配下へ統合してよいか確認する(UC-01)。</summary>
    bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths);
}
