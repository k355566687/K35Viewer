namespace K35Viewer.Infrastructure.Uninstall;

/// <summary>
/// アンインストールで消すもの（GitHub の Issue #24）。管理フォルダの中のファイル（アーカイブ）は、消さない。
/// <see cref="LibraryFolders"/> は、消さないものを守るための記録（これらが、消す場所の中にあれば、実行しない）。
/// </summary>
public sealed record UninstallTargets(
    string ProgramDirectory,
    string DataDirectory,
    IReadOnlyList<string> ShortcutFiles,
    IReadOnlyList<string> LibraryFolders);

/// <summary>消す前の確認の結果。<see cref="Blockers"/> が空でなければ、実行しない（理由の一覧）。</summary>
public sealed record UninstallPlan(
    UninstallTargets Targets,
    IReadOnlyList<string> Blockers,
    long ProgramBytes,
    int ProgramFileCount,
    long DataBytes)
{
    public bool CanRun => Blockers.Count == 0;
}

/// <summary>消せなかったもの。</summary>
public sealed record UninstallFailure(string Path, string Message);

/// <summary>
/// 消した結果。<see cref="Aborted"/> なら、何も消していない（<see cref="Blockers"/> に理由）。
/// <see cref="LeftoverPaths"/> は、データのフォルダに残した、35Viewer のものではないファイル（消さない）。
/// </summary>
public sealed record UninstallReport(
    bool Aborted,
    IReadOnlyList<string> Blockers,
    int DeletedShortcuts,
    bool ProgramDirectoryRemoved,
    bool DataDirectoryRemoved,
    IReadOnlyList<string> LeftoverPaths,
    IReadOnlyList<UninstallFailure> Failures)
{
    /// <summary>消せなかったものがなく、中止もしていない（残したものは、失敗に数えない）。</summary>
    public bool Succeeded => !Aborted && Failures.Count == 0;

    public static UninstallReport AbortedWith(IReadOnlyList<string> blockers) =>
        new(true, blockers, 0, false, false, [], []);
}
