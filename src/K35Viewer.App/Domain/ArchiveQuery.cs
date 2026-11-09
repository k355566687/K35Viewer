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
/// Offset / Limit が null の場合は全件。
/// </summary>
public sealed record ArchiveQuery(
    long? FolderId = null,
    string? SearchText = null,
    ArchiveSortKey SortKey = ArchiveSortKey.FileName,
    bool Descending = false,
    int? Offset = null,
    int? Limit = null);

/// <summary>一覧表示用のアーカイブ情報。ZIP本体を開かずにDBの値だけで組み立てる。</summary>
public sealed record ArchiveSummary(
    long Id,
    string FileName,
    string FolderPath,
    long FileSize,
    long LastWriteTime,
    int? ImageCount,
    string AnalysisStatus)
{
    public string FullPath => Path.Combine(FolderPath, FileName);
}

/// <summary>TotalCount は Offset / Limit を適用する前の、条件に一致する総件数。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>フォルダツリーの1ノード。登録フォルダ(IsLibraryRoot)はツリーの起点。</summary>
public sealed record FolderNode(
    long Id,
    string Name,
    string FullPath,
    bool IsLibraryRoot,
    bool IsUnavailable,
    IReadOnlyList<FolderNode> Children);
