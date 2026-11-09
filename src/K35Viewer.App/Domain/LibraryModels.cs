namespace K35Viewer.Domain;

/// <summary>ユーザーが追加した管理対象フォルダ(library_folders)。</summary>
public sealed record LibraryFolder(
    long Id,
    string Path,
    string NormalizedPath,
    string? DisplayName,
    bool IsNetwork,
    bool IsEnabled,
    string AvailabilityStatus,
    long? LastScanAt);

/// <summary>画面表示用の管理フォルダ情報(配下の有効なアーカイブ件数つき)。</summary>
public sealed record LibraryFolderSummary(
    long Id,
    string Path,
    string? DisplayName,
    string AvailabilityStatus,
    long? LastScanAt,
    int ArchiveCount);

/// <summary>実フォルダ(folders)。登録フォルダ自身も1件のレコードを持つ。</summary>
public sealed record FolderRecord(
    long Id,
    long LibraryFolderId,
    long? ParentFolderId,
    string Name,
    string FullPath,
    string NormalizedPath,
    string RelativePath,
    int Depth);

/// <summary>スキャンの差分判定に使うアーカイブ(archives)の最小情報。</summary>
public sealed record ArchiveRecord(
    long Id,
    long FolderId,
    string FileName,
    long FileSize,
    long LastWriteTime,
    string AvailabilityStatus);

/// <summary>スキャン結果の集計。</summary>
public sealed record ScanSummary(
    long ScanId,
    OperationStatus Status,
    int Discovered,
    int Added,
    int Changed,
    int Missing,
    int Failed,
    string Message);
