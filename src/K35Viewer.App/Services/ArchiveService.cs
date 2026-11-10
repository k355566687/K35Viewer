using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>アーカイブファイルのサイズと更新日時（UTC Unix秒）。外部で変更されていないかの確認に使う。</summary>
public sealed record ArchiveFileState(long FileSize, long LastWriteTime);

/// <summary>
/// 削除予約を反映するZIP更新の依頼（UC-11）。<see cref="ExpectedState"/> は、ビューアでアーカイブを開いた時点のファイルの状態で、
/// 更新の直前にファイルがこれと一致しなければ、外部で変更されたものとして更新しない。
/// </summary>
public sealed record ArchiveUpdateRequest(
    long ArchiveId,
    string ArchiveName,
    string FullPath,
    ArchiveFileState? ExpectedState,
    IReadOnlyList<string> EntriesToRemove,
    int RemainingImageCount,
    string? ResumeEntry,
    int? ResumeIndex);

/// <summary>ZIP更新の進行状況。<see cref="Replacing"/> 以降は、キャンセルできない。</summary>
public enum ArchiveUpdatePhase
{
    /// <summary>更新できるかの確認。</summary>
    Preparing,

    /// <summary>削除対象以外を、新しいZIPへコピーしている。</summary>
    Rebuilding,

    /// <summary>新しいZIPを開き直して確認している。</summary>
    Verifying,

    /// <summary>元のZIPをごみ箱へ移し、新しいZIPに置き換えている。キャンセルできない（安全境界を越えた）。</summary>
    Replacing,

    /// <summary>DBの更新と履歴の記録。</summary>
    Finishing,
}

public enum ArchiveUpdateStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// ZIP更新の結果。Message は利用者向けの文で、通知済み。
/// 失敗した場合、<see cref="OriginalPreserved"/> が true なら元のアーカイブは変更されておらず、削除予約を保持したまま再試行・破棄できる。
/// </summary>
public sealed record ArchiveUpdateResult(
    ArchiveUpdateStatus Status,
    string Message,
    string? ErrorCode = null,
    int RemovedCount = 0,
    bool OriginalPreserved = true)
{
    public bool Succeeded => Status == ArchiveUpdateStatus.Succeeded;
}

/// <summary>
/// アーカイブ（ZIP / CBZ）自体に対する、元ファイルの変更を伴う操作の統括（内部アーキテクチャ設計書 5.4、11章）。
/// 現在は、削除予約の反映（ZIP再構築）を担当する。
/// </summary>
public sealed partial class ArchiveService(
    IArchiveWriter writer,
    IRecycleBinService recycleBin,
    FileSystemService fileSystem,
    ArchiveRepository archives,
    ThumbnailRepository thumbnails,
    HistoryRepository history,
    LibraryRepository libraries,
    ThumbnailCacheStore thumbnailCache,
    IExplorerLauncher explorer,
    DatabaseWriter databaseWriter,
    INotificationService notifications,
    ILogger<ArchiveService> logger)
{
    /// <summary>一時ZIPを作るために、元のZIPの大きさに加えて確保しておく空き容量。</summary>
    private const long FreeSpaceMargin = 16L * 1024 * 1024;

    private static readonly TimeSpan[] ReplaceRetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2)];

    private readonly Lock _gate = new();
    private readonly HashSet<long> _updating = [];

    /// <summary>テストで待ち時間を短くするための、置換の再試行の待ち時間。</summary>
    internal IReadOnlyList<TimeSpan> ReplaceDelays { get; init; } = ReplaceRetryDelays;

    /// <summary>
    /// ファイルが、アーカイブを開いた時点から外部で変更されていないか。
    /// 開いた時点の状態が不明（null）の場合は、確認できないため変更なしとして扱う。ファイルがなければ変更ありとする。
    /// </summary>
    public bool IsChangedExternally(string fullPath, ArchiveFileState? opened)
    {
        if (opened is null)
        {
            return false;
        }

        return fileSystem.TryGetFileState(fullPath) is not { } state
            || state.FileSize != opened.FileSize
            || state.LastWriteTime != opened.LastWriteTime;
    }

    /// <summary>
    /// ZIPを更新できない理由（確認ダイアログに表示する）。更新できる見込みなら null。
    /// ファイルの外部変更と、元のファイルをごみ箱へ退避できるか（ネットワークドライブ等は不可）を確認する。
    /// </summary>
    public string? GetUpdateBlockReason(ArchiveUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (fileSystem.TryGetFileState(request.FullPath) is null)
        {
            return "アーカイブのファイルが見つからないため、更新できません";
        }

        if (IsChangedExternally(request.FullPath, request.ExpectedState))
        {
            return "アーカイブが外部で変更されたため、更新できません";
        }

        var recycleReason = recycleBin.CheckAvailability(request.FullPath);
        return recycleReason is null ? null : $"更新できません（{recycleReason}）";
    }

    /// <summary>
    /// 削除予約された画像を除いたZIPを作り、元のZIPをごみ箱へ退避して置き換える（UC-11）。
    /// 一時ZIPの作成と検証まではキャンセルでき、元のZIPのごみ箱への退避を始めた時点（安全境界）以降は最後まで完了させる。
    /// 退避より前の失敗・キャンセルでは、元のZIPと削除予約を保持する。結果は通知する。
    /// </summary>
    public async Task<ArchiveUpdateResult> ApplyDeletionsAsync(
        ArchiveUpdateRequest request, IProgress<ArchiveUpdatePhase>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EntriesToRemove.Count == 0)
        {
            return new ArchiveUpdateResult(ArchiveUpdateStatus.Failed, "削除対象の画像がありません", "NOTHING_TO_DELETE");
        }

        // 同じアーカイブへの更新は同時に行わない
        lock (_gate)
        {
            if (!_updating.Add(request.ArchiveId))
            {
                return Failed(
                    request, "UPDATE_IN_PROGRESS", Format($"「{request.ArchiveName}」はすでに更新中です"), notify: false);
            }
        }

        try
        {
            notifications.ShowInfo(Format($"「{request.ArchiveName}」の更新を開始しました"));
            LogStarted(logger, request.ArchiveId, request.EntriesToRemove.Count);
            return await RunAsync(request, progress, ct);
        }
        finally
        {
            lock (_gate)
            {
                _ = _updating.Remove(request.ArchiveId);
            }
        }
    }

    private async Task<ArchiveUpdateResult> RunAsync(
        ArchiveUpdateRequest request, IProgress<ArchiveUpdatePhase>? progress, CancellationToken ct)
    {
        var tempPath = string.Empty;
        try
        {
            // ---- 準備: 更新できるか確認する ----
            Report(progress, ArchiveUpdatePhase.Preparing);
            ct.ThrowIfCancellationRequested();

            if (fileSystem.TryGetFileState(request.FullPath) is not { } state)
            {
                return Failed(
                    request,
                    "FILE_NOT_FOUND",
                    Format($"「{request.ArchiveName}」が見つからないため、更新できません。元のアーカイブは変更していません"));
            }

            if (IsChangedExternally(request.FullPath, request.ExpectedState))
            {
                return Failed(
                    request,
                    "ARCHIVE_CHANGED",
                    Format($"「{request.ArchiveName}」が外部で変更されたため、更新できません。ライブラリへ戻って開き直すか、変更を破棄してください"));
            }

            // ごみ箱へ退避できないなら、何も作らずに中止する（元のZIPを退避できないまま置き換えない）
            var recycleReason = recycleBin.CheckAvailability(request.FullPath);
            if (recycleReason is not null)
            {
                return Failed(
                    request,
                    "RECYCLE_BIN_UNAVAILABLE",
                    Format($"「{request.ArchiveName}」を更新できません（{recycleReason}）。元のアーカイブは変更していません"));
            }

            if (fileSystem.GetAvailableFreeSpace(request.FullPath) is { } free && free < state.FileSize + FreeSpaceMargin)
            {
                return Failed(
                    request,
                    "DISK_FULL",
                    Format($"ディスクの空き容量が不足しているため、「{request.ArchiveName}」を更新できません。元のアーカイブは変更していません"));
            }

            // ---- 一時ZIPの作成と検証（ここまではキャンセルできる） ----
            tempPath = fileSystem.CreateTempPath(request.FullPath);
            Report(progress, ArchiveUpdatePhase.Rebuilding);
            var excluded = request.EntriesToRemove.ToHashSet(StringComparer.Ordinal);
            var rebuilt = await Task.Run(() => writer.Rebuild(request.FullPath, tempPath, excluded, ct), ct);
            if (!rebuilt.Succeeded)
            {
                return RebuildFailed(request, rebuilt, tempPath);
            }

            Report(progress, ArchiveUpdatePhase.Verifying);
            var verified = await Task.Run(() => writer.Verify(tempPath, rebuilt.Entries ?? [], ct), ct);
            if (!verified.Succeeded)
            {
                return RebuildFailed(request, verified, tempPath);
            }

            // 安全境界の直前に、元のZIPがその間に変更されていないことをもう一度確認する
            ct.ThrowIfCancellationRequested();
            if (IsChangedExternally(request.FullPath, request.ExpectedState))
            {
                return Failed(
                    request,
                    "ARCHIVE_CHANGED",
                    Format($"「{request.ArchiveName}」が更新の途中で外部から変更されたため、更新を中止しました。元のアーカイブは変更していません"));
            }
        }
        catch (OperationCanceledException)
        {
            _ = TryDeleteTemp(tempPath);
            return Cancelled(request);
        }
        catch (Exception ex)
        {
            LogUnexpected(logger, ex, request.ArchiveId);
            _ = TryDeleteTemp(tempPath);
            return Failed(
                request, "UPDATE_FAILED", Format($"「{request.ArchiveName}」を更新できませんでした。元のアーカイブは変更していません"));
        }

        // ---- 安全境界: ここから先はキャンセルしない。置き換え・DB更新・履歴記録まで完了させる ----
        return await ReplaceAsync(request, tempPath, progress);
    }

    private async Task<ArchiveUpdateResult> ReplaceAsync(
        ArchiveUpdateRequest request, string tempPath, IProgress<ArchiveUpdatePhase>? progress)
    {
        Report(progress, ArchiveUpdatePhase.Replacing);

        var moved = await Task.Run(() => recycleBin.MoveToRecycleBin(request.FullPath));
        if (!moved.Moved && File.Exists(request.FullPath))
        {
            // 元のZIPがそのまま残っている。一時ZIPを捨てて、元の状態で中止する
            _ = TryDeleteTemp(tempPath);
            LogRecycleFailed(logger, request.ArchiveId, moved.Status, moved.Message);
            return moved.Status == RecycleBinStatus.Unavailable
                ? Failed(
                    request,
                    "RECYCLE_BIN_UNAVAILABLE",
                    Format($"「{request.ArchiveName}」を更新できません（{moved.Message}）。元のアーカイブは変更していません"))
                : Failed(
                    request,
                    "RECYCLE_FAILED",
                    Format($"「{request.ArchiveName}」をごみ箱へ移動できなかったため、更新を中止しました。他のプログラムが使用中の可能性があります。元のアーカイブは変更していません"));
        }

        // 一時ZIPを元の名前にする（ウイルス対策ソフト等が一時的に掴んでいる場合に備えて、少し待って再試行する）
        Exception? replaceError = null;
        for (var attempt = 0; attempt <= ReplaceDelays.Count; attempt++)
        {
            try
            {
                fileSystem.MoveFile(tempPath, request.FullPath);
                replaceError = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                replaceError = ex;
                if (attempt < ReplaceDelays.Count)
                {
                    await Task.Delay(ReplaceDelays[attempt]);
                }
            }
        }

        if (replaceError is not null)
        {
            // 元のZIPはごみ箱にあり、新しいZIPは一時ファイルのまま残っている。復旧に必要な場所を記録して知らせる
            LogReplaceFailed(logger, replaceError, request.ArchiveId, tempPath);
            var message = Format(
                $"「{request.ArchiveName}」の置き換えに失敗しました。更新前のファイルはごみ箱にあり、更新後のファイルは「{tempPath}」にあります");
            await RecordFailureAsync(request, message, tempPath);
            notifications.ShowError(message);
            return new ArchiveUpdateResult(ArchiveUpdateStatus.Failed, message, "REPLACE_FAILED", 0, OriginalPreserved: false);
        }

        // ---- DBの更新と履歴 ----
        Report(progress, ArchiveUpdatePhase.Finishing);
        var removed = request.EntriesToRemove.Count;
        var recorded = await RecordSuccessAsync(request, removed);

        var done = recorded
            ? Format($"「{request.ArchiveName}」を更新しました。{removed:N0}枚の画像を削除しました")
            : Format($"「{request.ArchiveName}」を更新しました。{removed:N0}枚の画像を削除しました（記録の更新に失敗したため、次回のスキャンで反映されます）");
        if (recorded)
        {
            notifications.ShowSuccess(done);
        }
        else
        {
            notifications.ShowWarning(done);
        }

        LogUpdated(logger, request.ArchiveId, removed);
        return new ArchiveUpdateResult(ArchiveUpdateStatus.Succeeded, done, RemovedCount: removed, OriginalPreserved: false);
    }

    /// <summary>新しいファイルの状態をDBへ反映し、サムネイルを再生成待ちにして、操作履歴を追加する（1トランザクション）。</summary>
    private async Task<bool> RecordSuccessAsync(ArchiveUpdateRequest request, int removed)
    {
        try
        {
            var newState = fileSystem.TryGetFileState(request.FullPath)
                ?? throw new FileNotFoundException("更新後のファイルを確認できません", request.FullPath);
            var now = UnixTime.Now();
            await databaseWriter.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    await archives.UpdateAfterRebuildAsync(
                        connection,
                        transaction,
                        request.ArchiveId,
                        newState.FileSize,
                        newState.LastWriteTime,
                        request.RemainingImageCount,
                        request.ResumeEntry,
                        request.ResumeIndex,
                        now,
                        token);
                    await thumbnails.MarkStaleAsync(connection, transaction, request.ArchiveId, token);
                    await history.InsertAsync(
                        connection,
                        transaction,
                        new OperationResult(
                            OperationType.ImageDelete,
                            OperationStatus.Success,
                            Format($"{removed:N0}枚の画像を削除しました"),
                            request.ArchiveId,
                            request.ArchiveName,
                            removed,
                            request.FullPath,
                            request.FullPath),
                        token);
                },
                CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            // ファイルの置き換えは済んでいる。次回のスキャンが、実ファイルから状態を同期する
            LogRecordFailed(logger, ex, request.ArchiveId);
            return false;
        }
    }

    private async Task RecordFailureAsync(ArchiveUpdateRequest request, string message, string tempPath)
    {
        try
        {
            await databaseWriter.ExecuteAsync(
                (connection, transaction, token) => history.InsertAsync(
                    connection,
                    transaction,
                    new OperationResult(
                        OperationType.ImageDelete,
                        OperationStatus.Failed,
                        message,
                        request.ArchiveId,
                        request.ArchiveName,
                        request.EntriesToRemove.Count,
                        request.FullPath,
                        tempPath,
                        "REPLACE_FAILED"),
                    token),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogRecordFailed(logger, ex, request.ArchiveId);
        }
    }

    private ArchiveUpdateResult RebuildFailed(ArchiveUpdateRequest request, ArchiveWriteResult result, string tempPath)
    {
        _ = TryDeleteTemp(tempPath);
        LogRebuildFailed(logger, request.ArchiveId, result.ErrorCode, result.ErrorMessage);
        var code = result.ErrorCode ?? "UPDATE_FAILED";
        var reason = code switch
        {
            "DISK_FULL" => "ディスクの空き容量が不足しています",
            "ACCESS_DENIED" => "アクセスが拒否されました",
            "ARCHIVE_BROKEN" => "アーカイブの内容を読み取れません",
            "ENTRY_NOT_FOUND" or "UNSUPPORTED_ENTRY_NAME" => result.ErrorMessage ?? "アーカイブの内容を処理できません",
            "VERIFY_FAILED" => "作成したアーカイブを確認できませんでした",
            _ => "ファイルの読み書きに失敗しました",
        };
        return Failed(
            request, code, Format($"「{request.ArchiveName}」を更新できませんでした（{reason}）。元のアーカイブは変更していません"));
    }

    private ArchiveUpdateResult Cancelled(ArchiveUpdateRequest request)
    {
        var message = Format($"「{request.ArchiveName}」の更新をキャンセルしました。元のアーカイブは変更していません");
        notifications.ShowInfo(message);
        LogCancelled(logger, request.ArchiveId);
        return new ArchiveUpdateResult(ArchiveUpdateStatus.Cancelled, message, "CANCELLED");
    }

    private ArchiveUpdateResult Failed(ArchiveUpdateRequest request, string code, string message, bool notify = true)
    {
        LogFailed(logger, request.ArchiveId, code);
        if (notify)
        {
            notifications.ShowError(message);
        }

        return new ArchiveUpdateResult(ArchiveUpdateStatus.Failed, message, code);
    }

    private bool TryDeleteTemp(string tempPath)
    {
        if (tempPath.Length == 0)
        {
            return true;
        }

        var deleted = fileSystem.TryDelete(tempPath);
        if (!deleted)
        {
            LogTempNotDeleted(logger, tempPath);
        }

        return deleted;
    }

    /// <summary>
    /// 進行状況を報告する。受け取り側（画面）の不具合で例外が起きても、更新の処理は止めない
    /// （特に、元のZIPの退避を始めた後は、最後まで完了させる必要がある）。
    /// </summary>
    private void Report(IProgress<ArchiveUpdatePhase>? progress, ArchiveUpdatePhase phase)
    {
        try
        {
            progress?.Report(phase);
        }
        catch (Exception ex)
        {
            LogProgressFailed(logger, ex, phase);
        }
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "アーカイブの更新を開始します。ArchiveId={Id} 削除対象={Count}")]
    private static partial void LogStarted(ILogger logger, long id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "アーカイブを更新しました。ArchiveId={Id} 削除した画像={Count}")]
    private static partial void LogUpdated(ILogger logger, long id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "アーカイブの更新をキャンセルしました。ArchiveId={Id}")]
    private static partial void LogCancelled(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "アーカイブを更新できませんでした。ArchiveId={Id} ErrorCode={Code}")]
    private static partial void LogFailed(ILogger logger, long id, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ZIPの再構築に失敗しました。ArchiveId={Id} ErrorCode={Code} Message={Message}")]
    private static partial void LogRebuildFailed(ILogger logger, long id, string? code, string? message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "元のZIPをごみ箱へ移動できませんでした。ArchiveId={Id} Status={Status} Message={Message}")]
    private static partial void LogRecycleFailed(ILogger logger, long id, RecycleBinStatus status, string? message);

    [LoggerMessage(Level = LogLevel.Error, Message = "新しいZIPへの置き換えに失敗しました。更新後のファイルは一時ファイルに残っています。ArchiveId={Id} TempPath={TempPath}")]
    private static partial void LogReplaceFailed(ILogger logger, Exception exception, long id, string tempPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "更新結果をDBへ記録できませんでした。ArchiveId={Id}")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "更新の進行状況を報告できませんでした。Phase={Phase}")]
    private static partial void LogProgressFailed(ILogger logger, Exception exception, ArchiveUpdatePhase phase);

    [LoggerMessage(Level = LogLevel.Error, Message = "アーカイブの更新で想定外のエラーが起きました。ArchiveId={Id}")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "一時ファイルを削除できませんでした。手動で削除してください。Path={Path}")]
    private static partial void LogTempNotDeleted(ILogger logger, string path);
}
