using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>
/// フォルダスキャンと差分更新(UC-02 / UC-16 / UC-17)。
/// ファイル列挙は書き込みなしで行い、結果のDB反映だけを書き込みキュー経由の1トランザクションで行う。
/// 画像枚数・表紙・サムネイルの取得は含まない(解析状態 PENDING のまま登録し、後続Phaseで処理する)。
/// </summary>
public sealed partial class ScanService(
    FileSystemService fileSystem,
    LibraryRepository libraries,
    ArchiveRepository archives,
    ScanRepository scans,
    HistoryRepository history,
    DatabaseWriter writer,
    INotificationService notifications,
    ILogger<ScanService> logger)
{
    private const string Available = "AVAILABLE";

    public async Task<ScanSummary> ScanAsync(long libraryFolderId, ScanType scanType, CancellationToken ct = default)
    {
        var library = await libraries.GetLibraryFolderAsync(libraryFolderId, ct)
            ?? throw new ArgumentException($"管理フォルダが見つかりません: {libraryFolderId}", nameof(libraryFolderId));
        var displayName = library.DisplayName ?? library.Path;

        notifications.ShowInfo(StartMessage(scanType, displayName));
        LogStarted(logger, library.Path, scanType);

        var scanId = await writer.ExecuteAsync(
            (connection, transaction, token) =>
                scans.StartAsync(connection, transaction, scanType, library.Id, UnixTime.Now(), token),
            ct);

        ScanSummary summary;
        try
        {
            var tree = await Task.Run(() => fileSystem.ScanDirectoryTree(library.Path, ct), ct);
            summary = await writer.ExecuteAsync(
                (connection, transaction, token) =>
                    ApplyAsync(connection, transaction, library, scanId, tree, token),
                ct);
        }
        catch (OperationCanceledException)
        {
            summary = new ScanSummary(scanId, OperationStatus.Cancelled, 0, 0, 0, 0, 0, "スキャンをキャンセルしました");
            await FinishAsync(library, scanType, summary, "CANCELLED", closeScanRun: true);
            notifications.ShowInfo(summary.Message);
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, library.Path);
            summary = new ScanSummary(
                scanId, OperationStatus.Failed, 0, 0, 0, 0, 0,
                string.Create(CultureInfo.CurrentCulture, $"「{displayName}」のスキャンに失敗しました"));
            await FinishAsync(library, scanType, summary, "UNEXPECTED_ERROR", closeScanRun: true);
            notifications.ShowError(summary.Message);
            return summary;
        }

        await FinishAsync(library, scanType, summary, ErrorCodeOf(summary), closeScanRun: false);
        Notify(scanType, summary);
        LogCompleted(logger, library.Path, summary.Status, summary.Added, summary.Changed, summary.Missing, summary.Failed);
        return summary;
    }

    /// <summary>
    /// 列挙結果をDBへ反映する。アクセスできなかった範囲の既存登録は「不在」にしない。
    /// </summary>
    private async Task<ScanSummary> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryFolder library,
        long scanId,
        ScanTree tree,
        CancellationToken ct)
    {
        var now = UnixTime.Now();

        if (!tree.RootAccessible)
        {
            // 登録フォルダ自体に入れない(ネットワーク切断など)。配下の登録は一切変更しない。
            await libraries.SetScanStateAsync(connection, transaction, library.Id, "UNAVAILABLE", null, now, ct);
            var unavailable = new ScanSummary(
                scanId, OperationStatus.Failed, 0, 0, 0, 0, 0,
                string.Create(CultureInfo.CurrentCulture, $"「{library.Path}」にアクセスできません"));
            await scans.CompleteAsync(connection, transaction, scanId, unavailable.Status, unavailable, now, ct);
            return unavailable;
        }

        var knownFolders = await libraries.GetFoldersAsync(connection, transaction, library.Id, ct);
        var folderIdByPath = knownFolders.ToDictionary(f => f.NormalizedPath, f => f.Id, StringComparer.Ordinal);
        var folderPathById = knownFolders.ToDictionary(f => f.Id, f => f.NormalizedPath);
        var knownArchives = await archives.GetByLibraryAsync(connection, transaction, library.Id, ct);
        var archiveByKey = knownArchives.ToDictionary(a => (a.FolderId, Key(a.FileName)));

        var seenFolderIds = new HashSet<long>();
        var seenArchiveIds = new HashSet<long>();
        var unverifiedRoots = new List<string>();
        var discovered = 0;
        var added = 0;
        var changed = 0;
        var missing = 0;
        var failed = tree.FailedCount;

        using var batch = archives.CreateBatch(connection, transaction);

        foreach (var directory in tree.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var normalized = PathNormalizer.Normalize(directory.FullPath);
            if (!folderIdByPath.TryGetValue(normalized, out var folderId))
            {
                folderId = await InsertFolderAsync(
                    connection, transaction, library, directory, folderIdByPath, now, ct);
                folderIdByPath[normalized] = folderId;
                folderPathById[folderId] = normalized;
            }
            else
            {
                await libraries.MarkFolderSeenAsync(
                    connection, transaction, folderId, directory.FullPath, scanId, now, ct);
            }

            seenFolderIds.Add(folderId);

            if (directory.Failed)
            {
                unverifiedRoots.Add(normalized);
                continue;
            }

            foreach (var file in directory.Archives)
            {
                discovered++;
                var fullPath = Path.Combine(directory.FullPath, file.FileName);

                if (archiveByKey.TryGetValue((folderId, Key(file.FileName)), out var record))
                {
                    seenArchiveIds.Add(record.Id);
                    var isChanged = record.FileSize != file.FileSize || record.LastWriteTime != file.LastWriteTime;
                    var isRestored = !string.Equals(record.AvailabilityStatus, Available, StringComparison.Ordinal);

                    if (isChanged)
                    {
                        if (!StillMatches(fullPath, file))
                        {
                            failed++;
                            continue;
                        }

                        await batch.UpdateChangedAsync(
                            record.Id, file.FileName, file.FileSize, file.LastWriteTime, scanId, now, ct);
                        changed++;
                    }
                    else
                    {
                        await batch.MarkSeenAsync(record.Id, file.FileName, scanId, now, ct);
                        if (isRestored)
                        {
                            changed++;
                        }
                    }
                }
                else
                {
                    if (!StillMatches(fullPath, file))
                    {
                        failed++;
                        continue;
                    }

                    await batch.InsertAsync(folderId, file.FileName, file.FileSize, file.LastWriteTime, scanId, now, ct);
                    added++;
                }
            }
        }

        // 確認できた範囲で見つからなかったアーカイブとフォルダを「不在」にする
        foreach (var record in knownArchives)
        {
            if (seenArchiveIds.Contains(record.Id)
                || string.Equals(record.AvailabilityStatus, "MISSING", StringComparison.Ordinal)
                || IsUnverified(folderPathById[record.FolderId], unverifiedRoots))
            {
                continue;
            }

            await batch.MarkMissingAsync(record.Id, now, ct);
            missing++;
        }

        foreach (var folder in knownFolders)
        {
            if (!seenFolderIds.Contains(folder.Id) && !IsUnverified(folder.NormalizedPath, unverifiedRoots))
            {
                await libraries.MarkFolderUnavailableAsync(connection, transaction, folder.Id, now, ct);
            }
        }

        var status = failed == 0 ? OperationStatus.Success : OperationStatus.Partial;
        var summary = new ScanSummary(scanId, status, discovered, added, changed, missing, failed, string.Empty);
        summary = summary with { Message = BuildCountsMessage(summary) };

        await libraries.SetScanStateAsync(connection, transaction, library.Id, Available, now, now, ct);
        await scans.CompleteAsync(connection, transaction, scanId, status, summary, now, ct);
        return summary;
    }

    private async Task<long> InsertFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryFolder library,
        ScannedDirectory directory,
        Dictionary<string, long> folderIdByPath,
        long now,
        CancellationToken ct)
    {
        long? parentId = null;
        if (directory.ParentFullPath is not null)
        {
            parentId = folderIdByPath[PathNormalizer.Normalize(directory.ParentFullPath)];
        }

        var relative = Path.GetRelativePath(library.Path, directory.FullPath);
        if (relative == ".")
        {
            relative = string.Empty;
        }

        var depth = relative.Length == 0
            ? 0
            : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        var name = PathNormalizer.GetDisplayName(directory.FullPath);

        return await libraries.InsertFolderAsync(
            connection,
            transaction,
            library.Id,
            parentId,
            name,
            directory.FullPath,
            PathNormalizer.Normalize(directory.FullPath),
            relative,
            depth,
            now,
            ct);
    }

    /// <summary>
    /// 書き込み直前の再確認(内部アーキテクチャ設計書 13.3)。列挙後にファイルが変化していた場合は
    /// そのファイルの結果を破棄し、次回のスキャン対象とする。
    /// </summary>
    private bool StillMatches(string fullPath, ScannedArchive scanned)
    {
        var current = fileSystem.TryGetFileState(fullPath);
        return current is { } state
            && state.FileSize == scanned.FileSize
            && state.LastWriteTime == scanned.LastWriteTime;
    }

    private static bool IsUnverified(string normalizedPath, List<string> unverifiedRoots) =>
        unverifiedRoots.Exists(root => PathNormalizer.IsSameOrDescendant(normalizedPath, root));

    private static string Key(string fileName) => fileName.ToUpperInvariant();

    /// <summary>
    /// スキャンの最終結果を操作履歴へ記録する。キャンセル済みの場合でも結果を残すため、
    /// ここでの書き込みは呼び出し元のキャンセルトークンに従わない。
    /// closeScanRun が true の場合は、ロールバックされて RUNNING のまま残った scan_runs も閉じる。
    /// </summary>
    private async Task FinishAsync(
        LibraryFolder library, ScanType scanType, ScanSummary summary, string? errorCode, bool closeScanRun)
    {
        var result = new OperationResult(
            scanType.ToOperationType(),
            summary.Status,
            summary.Message,
            AffectedCount: summary.Added + summary.Changed,
            NewPath: library.Path,
            ErrorCode: summary.Status is OperationStatus.Success ? null : errorCode);

        await writer.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                if (closeScanRun)
                {
                    await scans.CompleteAsync(
                        connection, transaction, summary.ScanId, summary.Status, summary, UnixTime.Now(), token);
                }

                await history.InsertAsync(connection, transaction, result, token);
            },
            CancellationToken.None);
    }

    private static string? ErrorCodeOf(ScanSummary summary) => summary.Status switch
    {
        OperationStatus.Failed => "FILE_NOT_FOUND",
        OperationStatus.Partial => "ACCESS_DENIED",
        _ => null,
    };

    private void Notify(ScanType scanType, ScanSummary summary)
    {
        switch (summary.Status)
        {
            case OperationStatus.Success:
                if (scanType == ScanType.Startup && summary.Added + summary.Changed + summary.Missing == 0)
                {
                    notifications.ShowSuccess("ライブラリは最新です");
                }
                else
                {
                    notifications.ShowSuccess(summary.Message);
                }

                break;
            case OperationStatus.Partial:
                notifications.ShowWarning(summary.Message);
                break;
            default:
                notifications.ShowError(summary.Message);
                break;
        }
    }

    private static string BuildCountsMessage(ScanSummary s)
    {
        var text = string.Create(
            CultureInfo.CurrentCulture,
            $"スキャンが完了しました。追加{s.Added}件、変更{s.Changed}件、不在{s.Missing}件");
        return s.Failed > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{text}、確認できなかったもの{s.Failed}件")
            : text;
    }

    private static string StartMessage(ScanType scanType, string displayName) => scanType switch
    {
        ScanType.Initial => string.Create(CultureInfo.CurrentCulture, $"「{displayName}」のスキャンを開始しました"),
        ScanType.Manual => "ライブラリの再スキャンを開始しました",
        ScanType.Startup => "ライブラリの更新を確認しています",
        _ => throw new ArgumentOutOfRangeException(nameof(scanType), scanType, null),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "スキャンを開始します。Path={Path} Type={Type}")]
    private static partial void LogStarted(ILogger logger, string path, ScanType type);

    [LoggerMessage(Level = LogLevel.Information, Message = "スキャンが終了しました。Path={Path} Status={Status} Added={Added} Changed={Changed} Missing={Missing} Failed={Failed}")]
    private static partial void LogCompleted(ILogger logger, string path, OperationStatus status, int added, int changed, int missing, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "スキャン中に予期しないエラーが発生しました。Path={Path}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string path);
}
