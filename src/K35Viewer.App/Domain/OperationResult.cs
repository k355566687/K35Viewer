namespace K35Viewer.Domain;

/// <summary>操作履歴(operation_history.operation_type)に記録するユーザー操作の種別。</summary>
public enum OperationType
{
    ImageDelete,
    ArchiveRename,
    ArchiveMove,
    ArchiveDelete,
    LibraryFolderAdd,
    LibraryFolderRemove,
    ScanInitial,
    ScanManual,
    ScanStartup,
}

public enum OperationStatus
{
    Success,
    Failed,
    Partial,
    Cancelled,
}

/// <summary>
/// ユーザー向けの確定した操作結果。トースト通知と操作履歴の共通データとして使う。
/// 例外やスタックトレースなどの障害調査情報は含めず、アプリケーションログへ記録する。
/// </summary>
public sealed record OperationResult(
    OperationType OperationType,
    OperationStatus Status,
    string Message,
    long? ArchiveId = null,
    string? ArchiveName = null,
    int? AffectedCount = null,
    string? OldPath = null,
    string? NewPath = null,
    string? ErrorCode = null,
    DateTimeOffset? ExecutedAt = null);

public static class OperationResultExtensions
{
    public static string ToDbValue(this OperationType type) => type switch
    {
        OperationType.ImageDelete => "IMAGE_DELETE",
        OperationType.ArchiveRename => "ARCHIVE_RENAME",
        OperationType.ArchiveMove => "ARCHIVE_MOVE",
        OperationType.ArchiveDelete => "ARCHIVE_DELETE",
        OperationType.LibraryFolderAdd => "LIBRARY_FOLDER_ADD",
        OperationType.LibraryFolderRemove => "LIBRARY_FOLDER_REMOVE",
        OperationType.ScanInitial => "SCAN_INITIAL",
        OperationType.ScanManual => "SCAN_MANUAL",
        OperationType.ScanStartup => "SCAN_STARTUP",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static string ToDbValue(this OperationStatus status) => status switch
    {
        OperationStatus.Success => "SUCCESS",
        OperationStatus.Failed => "FAILED",
        OperationStatus.Partial => "PARTIAL",
        OperationStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
