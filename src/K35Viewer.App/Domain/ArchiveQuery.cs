namespace K35Viewer.Domain;

/// <summary>アーカイブ一覧の並べ替え項目。</summary>
public enum ArchiveSortKey
{
    FileName,
    LastWriteTime,
    FileSize,
    ImageCount,
}

/// <summary>
/// アーカイブ一覧の検索条件。FolderId が null の場合は全登録フォルダが対象。
/// 指定した場合は、そのフォルダ自身と配下のすべてのサブフォルダを対象にする。
/// SearchText は空白区切りで、すべての語がファイル名に含まれるものに一致する(部分一致、AND)。
/// Offset / Limit が null の場合は全件。NewOnly が true の場合は、「新着」の印のあるものだけ。
/// </summary>
public sealed record ArchiveQuery(
    long? FolderId = null,
    string? SearchText = null,
    ArchiveSortKey SortKey = ArchiveSortKey.FileName,
    bool Descending = false,
    int? Offset = null,
    int? Limit = null,
    bool NewOnly = false);

/// <summary>
/// 一覧表示用のアーカイブ情報。ZIP本体を開かずにDBの値だけで組み立てる。
/// LibraryRootPath は、このアーカイブが属する管理フォルダのパス（保存場所を管理フォルダからの相対パスで表示するため）。
/// </summary>
public sealed record ArchiveSummary(
    long Id,
    string FileName,
    string FolderPath,
    long FileSize,
    long LastWriteTime,
    int? ImageCount,
    string AnalysisStatus,
    string? ThumbnailPath = null,
    string? ThumbnailStatus = null,
    string? LibraryRootPath = null,
    bool IsNew = false)
{
    public string FullPath => Path.Combine(FolderPath, FileName);
}

/// <summary>アーカイブを開く(ビューアで閲覧する)ために必要な情報。</summary>
public sealed record ArchiveOpenInfo(
    long Id,
    string FolderPath,
    string FileName,
    string AvailabilityStatus,
    string? LastViewEntry,
    int? LastViewIndex)
{
    public string FullPath => Path.Combine(FolderPath, FileName);
}

/// <summary>
/// 解析・サムネイル生成の対象アーカイブ。FileSize / LastWriteTime はDBに記録されている値で、
/// 処理前にファイルの現在の状態と照合し、外部で変更されていれば処理しない。
/// </summary>
public sealed record ArchiveProcessingTarget(
    long Id,
    string FolderPath,
    string FileName,
    long FileSize,
    long LastWriteTime)
{
    public string FullPath => Path.Combine(FolderPath, FileName);
}

/// <summary>TotalCount は Offset / Limit を適用する前の、条件に一致する総件数。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>フォルダツリーの1ノード。登録フォルダ(IsLibraryRoot)はツリーの起点。</summary>
public sealed record FolderNode(
    long Id,
    long LibraryFolderId,
    string Name,
    string FullPath,
    bool IsLibraryRoot,
    bool IsUnavailable,
    IReadOnlyList<FolderNode> Children);
