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
/// スキャンは同時に1つだけ実行する(後から呼ばれたものは先のスキャンの完了を待つ)。
/// </summary>
public sealed partial class ScanService(
    FileSystemService fileSystem,
    LibraryRepository libraries,
    ArchiveRepository archives,
    ScanRepository scans,
    HistoryRepository history,
    DatabaseWriter writer,
    INotificationService notifications,
    ILogger<ScanService> logger) : IDisposable
{
    private const string Available = "AVAILABLE";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>テスト用。管理フォルダを列挙したあと、DBへ反映する前に呼ばれる（その間のアプリ内操作を再現するため）。</summary>
    internal Action<LibraryFolder>? AfterEnumerate { get; set; }

    /// <summary>指定した管理フォルダ1件をスキャンする(フォルダ追加直後の初回スキャン等)。</summary>
    public async Task<ScanSummary> ScanAsync(long libraryFolderId, ScanType scanType, CancellationToken ct = default)
    {
        var library = await libraries.GetLibraryFolderAsync(libraryFolderId, ct)
            ?? throw new ArgumentException($"管理フォルダが見つかりません: {libraryFolderId}", nameof(libraryFolderId));
        return await RunAsync([library], library.Id, scanType, ct);
    }

    /// <summary>
    /// 有効なすべての管理フォルダをまとめて1回のスキャンとして実行する(再スキャン・起動時チェック)。
    /// 開始・完了の通知と操作履歴は全体で1回にまとめる。対象がなければ何もせず null を返す。
    /// </summary>
    public async Task<ScanSummary?> ScanAllAsync(ScanType scanType, CancellationToken ct = default)
    {
        var targets = (await libraries.GetLibraryFoldersAsync(ct)).Where(l => l.IsEnabled).ToList();
        if (targets.Count == 0)
        {
            return null;
        }

        return await RunAsync(targets, null, scanType, ct);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ScanSummary> RunAsync(
        List<LibraryFolder> targets, long? scanRunLibraryId, ScanType scanType, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var single = targets.Count == 1 ? targets[0] : null;
            var label = single is null ? null : single.DisplayName ?? single.Path;

            notifications.ShowInfo(StartMessage(scanType, label));
            LogStarted(logger, targets.Count, scanType);

            var scanId = await writer.ExecuteAsync(
                (connection, transaction, token) =>
                    scans.StartAsync(connection, transaction, scanType, scanRunLibraryId, UnixTime.Now(), token),
                ct);

            var totals = new Counts();
            var accessible = 0;
            try
            {
                foreach (var library in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    var tree = await Task.Run(() => fileSystem.ScanDirectoryTree(library.Path, ct), ct);
                    AfterEnumerate?.Invoke(library);
                    var counts = await writer.ExecuteAsync(
                        (connection, transaction, token) =>
                            ApplyAsync(connection, transaction, library, scanId, tree, token),
                        ct);
                    totals.Add(counts);
                    if (counts.RootAccessible)
                    {
                        accessible++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                var cancelled = totals.ToSummary(scanId, OperationStatus.Cancelled, "スキャンをキャンセルしました");
                await FinishAsync(scanType, cancelled, "CANCELLED", single?.Path);
                notifications.ShowInfo(cancelled.Message);
                throw;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
                var failed = totals.ToSummary(
                    scanId,
                    OperationStatus.Failed,
                    label is null ? "スキャンに失敗しました" : Format($"「{label}」のスキャンに失敗しました"));
                await FinishAsync(scanType, failed, "UNEXPECTED_ERROR", single?.Path);
                notifications.ShowError(failed.Message);
                return failed;
            }

            // 全件にアクセスできなければ失敗、一部でも確認できなかった範囲があれば一部失敗
            var status = accessible == 0
                ? OperationStatus.Failed
                : totals.Failed > 0 ? OperationStatus.Partial : OperationStatus.Success;
            var message = status == OperationStatus.Failed && single is not null
                ? Format($"「{single.Path}」にアクセスできません")
                : BuildCountsMessage(scanType, totals);
            var summary = totals.ToSummary(scanId, status, message);

            await FinishAsync(scanType, summary, ErrorCodeOf(status), single?.Path);
            Notify(scanType, summary);
            LogCompleted(logger, summary.Status, summary.Added, summary.Changed, summary.Missing, summary.Failed);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 1つの管理フォルダについて、列挙結果をDBへ反映する。アクセスできなかった範囲の既存登録は「不在」にしない。
    /// </summary>
    private async Task<Counts> ApplyAsync(
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
            return new Counts { Failed = 1, RootAccessible = false };
        }

        var knownFolders = await libraries.GetFoldersAsync(connection, transaction, library.Id, ct);
        var folderIdByPath = knownFolders.ToDictionary(f => f.NormalizedPath, f => f.Id, StringComparer.Ordinal);
        var folderPathById = knownFolders.ToDictionary(f => f.Id, f => f.NormalizedPath);
        var folderFullPathById = knownFolders.ToDictionary(f => f.Id, f => f.FullPath);
        var knownArchives = await archives.GetByLibraryAsync(connection, transaction, library.Id, ct);
        var archiveByKey = knownArchives.ToDictionary(a => (a.FolderId, Key(a.FileName)));

        var seenFolderIds = new HashSet<long>();
        var seenArchiveIds = new HashSet<long>();
        var unverifiedRoots = new List<string>();
        var counts = new Counts { Failed = tree.FailedCount };

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
                counts.Discovered++;
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
                            counts.Failed++;
                            continue;
                        }

                        await batch.UpdateChangedAsync(
                            record.Id, file.FileName, file.FileSize, file.LastWriteTime, scanId, now, ct);
                        counts.Changed++;
                    }
                    else
                    {
                        await batch.MarkSeenAsync(record.Id, file.FileName, scanId, now, ct);
                        if (isRestored)
                        {
                            counts.Changed++;
                        }
                    }
                }
                else
                {
                    if (!StillMatches(fullPath, file))
                    {
                        counts.Failed++;
                        continue;
                    }

                    await batch.InsertAsync(folderId, file.FileName, file.FileSize, file.LastWriteTime, scanId, now, ct);
                    counts.Added++;
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

            // 列挙のあとに、アプリ内の操作（名前変更・移動）でDBの記録が変わっていれば、記録どおりの場所にファイルがある。
            // 古い列挙結果で「不在」にしない（アプリ内の操作の結果を優先する。内部アーキテクチャ設計書 13.3）
            if (fileSystem.TryGetFileState(Path.Combine(folderFullPathById[record.FolderId], record.FileName)) is not null)
            {
                continue;
            }

            await batch.MarkMissingAsync(record.Id, now, ct);
            counts.Missing++;
        }

        foreach (var folder in knownFolders)
        {
            if (!seenFolderIds.Contains(folder.Id) && !IsUnverified(folder.NormalizedPath, unverifiedRoots))
            {
                await libraries.MarkFolderUnavailableAsync(connection, transaction, folder.Id, now, ct);
            }
        }

        await libraries.SetScanStateAsync(connection, transaction, library.Id, Available, now, now, ct);
        return counts;
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
    /// スキャン全体の最終結果を、scan_runs の完了と操作履歴へ記録する(同一トランザクション)。
    /// キャンセル済みでも結果を残すため、この書き込みは呼び出し元のキャンセルトークンに従わない。
    /// </summary>
    private async Task FinishAsync(ScanType scanType, ScanSummary summary, string? errorCode, string? path)
    {
        var result = new OperationResult(
            scanType.ToOperationType(),
            summary.Status,
            summary.Message,
            AffectedCount: summary.Added + summary.Changed,
            NewPath: path,
            ErrorCode: summary.Status is OperationStatus.Success ? null : errorCode);

        await writer.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await scans.CompleteAsync(
                    connection, transaction, summary.ScanId, summary.Status, summary, UnixTime.Now(), token);
                await history.InsertAsync(connection, transaction, result, token);
            },
            CancellationToken.None);
    }

    private static string? ErrorCodeOf(OperationStatus status) => status switch
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
                notifications.ShowSuccess(
                    scanType == ScanType.Startup && summary.Added + summary.Changed + summary.Missing == 0
                        ? "ライブラリは最新です"
                        : summary.Message);
                break;
            case OperationStatus.Partial:
                notifications.ShowWarning(summary.Message);
                break;
            default:
                notifications.ShowError(summary.Message);
                break;
        }
    }

    private static string BuildCountsMessage(ScanType scanType, Counts c)
    {
        var prefix = scanType switch
        {
            ScanType.Initial => "スキャンが完了しました。",
            ScanType.Manual => "再スキャンが完了しました。",
            _ => "ライブラリを更新しました。",
        };
        var text = Format($"{prefix}追加{c.Added}件、変更{c.Changed}件、不在{c.Missing}件");
        return c.Failed > 0 ? Format($"{text}、確認できなかったもの{c.Failed}件") : text;
    }

    private static string StartMessage(ScanType scanType, string? label) => scanType switch
    {
        ScanType.Initial => label is null ? "スキャンを開始しました" : Format($"「{label}」のスキャンを開始しました"),
        ScanType.Manual => "ライブラリの再スキャンを開始しました",
        ScanType.Startup => "ライブラリの更新を確認しています",
        _ => throw new ArgumentOutOfRangeException(nameof(scanType), scanType, null),
    };

    private static string Format(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);

    /// <summary>1つ以上の管理フォルダのスキャン結果の集計。</summary>
    private sealed class Counts
    {
        public int Discovered { get; set; }

        public int Added { get; set; }

        public int Changed { get; set; }

        public int Missing { get; set; }

        public int Failed { get; set; }

        public bool RootAccessible { get; init; } = true;

        public void Add(Counts other)
        {
            Discovered += other.Discovered;
            Added += other.Added;
            Changed += other.Changed;
            Missing += other.Missing;
            Failed += other.Failed;
        }

        public ScanSummary ToSummary(long scanId, OperationStatus status, string message) =>
            new(scanId, status, Discovered, Added, Changed, Missing, Failed, message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "スキャンを開始します。対象フォルダ数={Count} Type={Type}")]
    private static partial void LogStarted(ILogger logger, int count, ScanType type);

    [LoggerMessage(Level = LogLevel.Information, Message = "スキャンが終了しました。Status={Status} Added={Added} Changed={Changed} Missing={Missing} Failed={Failed}")]
    private static partial void LogCompleted(ILogger logger, OperationStatus status, int added, int changed, int missing, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "スキャン中に予期しないエラーが発生しました。")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
