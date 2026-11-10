using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Services;

public enum AddFolderOutcome
{
    /// <summary>登録した。続けて初回スキャンを行う。</summary>
    Added,

    /// <summary>同じフォルダが登録済み。</summary>
    AlreadyRegistered,

    /// <summary>登録済みフォルダの配下にあるため、追加しない。</summary>
    ContainedInExisting,

    /// <summary>既存の登録フォルダの親。統合の確認が必要。</summary>
    MergeRequired,

    /// <summary>存在しない、またはアクセスできない。</summary>
    NotAccessible,
}

public sealed record AddFolderResult(
    AddFolderOutcome Outcome,
    long? LibraryFolderId,
    string? RelatedPath,
    IReadOnlyList<string> MergeTargets)
{
    public static AddFolderResult Added(long id) => new(AddFolderOutcome.Added, id, null, []);

    public static AddFolderResult AlreadyRegistered(string path) =>
        new(AddFolderOutcome.AlreadyRegistered, null, path, []);

    public static AddFolderResult ContainedInExisting(string parentPath) =>
        new(AddFolderOutcome.ContainedInExisting, null, parentPath, []);

    public static AddFolderResult MergeRequired(IReadOnlyList<string> childPaths) =>
        new(AddFolderOutcome.MergeRequired, null, null, childPaths);

    public static AddFolderResult NotAccessible() => new(AddFolderOutcome.NotAccessible, null, null, []);
}

/// <summary>ライブラリ(管理フォルダ)に関する操作の窓口。</summary>
public sealed partial class LibraryService(
    LibraryRepository libraries,
    ArchiveRepository archives,
    HistoryRepository history,
    DatabaseWriter writer,
    FileSystemService fileSystem,
    ThumbnailCacheStore thumbnailCache,
    INotificationService notifications,
    ILogger<LibraryService> logger)
{
    public Task<IReadOnlyList<LibraryFolderSummary>> GetLibraryFoldersAsync(CancellationToken ct = default) =>
        libraries.GetSummariesAsync(ct);

    /// <summary>条件に一致するアーカイブを検索する(UC-03 / UC-04)。ZIP本体は開かず、SQLiteだけで応答する。</summary>
    public Task<PagedResult<ArchiveSummary>> QueryArchivesAsync(ArchiveQuery query, CancellationToken ct = default) =>
        archives.QueryAsync(query, ct);

    /// <summary>
    /// 登録フォルダを起点とするフォルダツリーを返す。起点は登録フォルダ(フルパス表示)、配下はフォルダ名で並べる。
    /// 見つからなくなったフォルダ(UNAVAILABLE)とその配下は含めない。
    /// 登録フォルダ自体に一時的にアクセスできない場合(ネットワーク切断等)は、起点を残して IsUnavailable とする。
    /// </summary>
    public async Task<IReadOnlyList<FolderNode>> GetFolderTreeAsync(CancellationToken ct = default)
    {
        var rows = await libraries.GetFolderTreeRowsAsync(ct);
        var childrenByParent = rows
            .Where(r => r.ParentFolderId is not null)
            .ToLookup(r => r.ParentFolderId!.Value);

        FolderNode Build(FolderTreeRow row, bool isRoot)
        {
            var children = childrenByParent[row.Id]
                .Where(c => !string.Equals(c.FolderAvailability, "UNAVAILABLE", StringComparison.Ordinal))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => Build(c, isRoot: false))
                .ToList();
            return new FolderNode(
                row.Id,
                row.LibraryFolderId,
                isRoot ? row.FullPath : row.Name,
                row.FullPath,
                isRoot,
                isRoot && string.Equals(row.LibraryAvailability, "UNAVAILABLE", StringComparison.Ordinal),
                children);
        }

        return rows
            .Where(r => r.ParentFolderId is null)
            .OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(r => Build(r, isRoot: true))
            .ToList();
    }

    /// <summary>
    /// 管理フォルダを追加する(UC-01)。スキャンは行わない。呼び出し側が Added を受けて ScanService を実行する。
    /// 既存の登録フォルダの親を追加する場合は、confirmMerge が true のときだけ統合する。
    /// </summary>
    public async Task<AddFolderResult> AddFolderAsync(string path, bool confirmMerge, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !fileSystem.CanAccessDirectory(path))
        {
            notifications.ShowError(string.Create(CultureInfo.CurrentCulture, $"フォルダにアクセスできません: {path}"));
            return AddFolderResult.NotAccessible();
        }

        var fullPath = Path.GetFullPath(path);
        var normalized = PathNormalizer.Normalize(fullPath);
        var existing = await libraries.GetLibraryFoldersAsync(ct);

        var same = existing.FirstOrDefault(f => f.NormalizedPath == normalized);
        if (same is not null)
        {
            notifications.ShowInfo(string.Create(CultureInfo.CurrentCulture, $"「{same.Path}」は既に登録されています"));
            return AddFolderResult.AlreadyRegistered(same.Path);
        }

        var parent = existing.FirstOrDefault(f => PathNormalizer.IsSameOrDescendant(normalized, f.NormalizedPath));
        if (parent is not null)
        {
            notifications.ShowInfo(string.Create(
                CultureInfo.CurrentCulture,
                $"「{parent.Path}」の配下にあるため、追加しません(親フォルダの管理対象に含まれます)"));
            return AddFolderResult.ContainedInExisting(parent.Path);
        }

        var children = existing
            .Where(f => PathNormalizer.IsSameOrDescendant(f.NormalizedPath, normalized))
            .ToList();
        if (children.Count > 0 && !confirmMerge)
        {
            return AddFolderResult.MergeRequired(children.Select(c => c.Path).ToList());
        }

        var isNetwork = fileSystem.IsNetworkPath(fullPath);
        var id = await writer.ExecuteAsync(
            (connection, transaction, token) =>
                RegisterAsync(connection, transaction, fullPath, normalized, isNetwork, children, token),
            ct);

        LogAdded(logger, fullPath, id, children.Count);
        return AddFolderResult.Added(id);
    }

    /// <summary>管理フォルダ配下のアーカイブの件数（確認ダイアログに表示する）。登録されていなければ null。</summary>
    public async Task<int?> GetArchiveCountAsync(long libraryFolderId, CancellationToken ct = default) =>
        (await libraries.GetSummariesAsync(ct)).FirstOrDefault(f => f.Id == libraryFolderId)?.ArchiveCount;

    /// <summary>
    /// 管理フォルダを管理対象から外す（UC-18）。配下のフォルダ・アーカイブ・サムネイルの管理情報をDBから削除し、
    /// サムネイル画像のファイルを削除する。実際のファイルとフォルダは変更しない。
    /// 操作履歴は残り、アーカイブとの関連（archive_id）だけがNULLになる（DB設計書 11.5）。
    /// 呼び出し側は、スキャン中でないこと、対象のアーカイブをビューアで開いていないことを確認してから呼ぶ。
    /// </summary>
    public async Task<OperationResult> RemoveFolderAsync(long libraryFolderId, CancellationToken ct = default)
    {
        IReadOnlyList<string> thumbnailPaths = [];
        OperationResult? result = null;
        try
        {
            result = await writer.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    var folder = await libraries.GetLibraryFolderAsync(connection, transaction, libraryFolderId, token);
                    if (folder is null)
                    {
                        return null;
                    }

                    var count = await libraries.CountArchivesAsync(connection, transaction, libraryFolderId, token);
                    thumbnailPaths = await libraries.GetThumbnailPathsAsync(connection, transaction, libraryFolderId, token);
                    await libraries.DeleteLibraryFolderAsync(connection, transaction, libraryFolderId, token);

                    var removed = new OperationResult(
                        OperationType.LibraryFolderRemove,
                        OperationStatus.Success,
                        string.Create(
                            CultureInfo.CurrentCulture,
                            $"「{folder.Path}」を管理対象から外しました（アーカイブ{count:N0}件。ファイルは削除していません）"),
                        AffectedCount: count,
                        OldPath: folder.Path);
                    await history.InsertAsync(connection, transaction, removed, token);
                    return removed;
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRemoveFailed(logger, ex, libraryFolderId);
            const string failure = "管理フォルダを管理対象から外せませんでした。ライブラリは変更していません";
            notifications.ShowError(failure);
            return new OperationResult(
                OperationType.LibraryFolderRemove, OperationStatus.Failed, failure, ErrorCode: "DATABASE_ERROR");
        }

        if (result is null)
        {
            const string missing = "管理フォルダが見つかりません";
            notifications.ShowWarning(missing);
            return new OperationResult(
                OperationType.LibraryFolderRemove, OperationStatus.Failed, missing, ErrorCode: "LIBRARY_FOLDER_NOT_FOUND");
        }

        // DBの更新が済んでから、サムネイル画像のファイル実体を削除する（削除できなくても処理は続ける）
        foreach (var path in thumbnailPaths)
        {
            thumbnailCache.Delete(path);
        }

        LogRemoved(logger, libraryFolderId, result.AffectedCount ?? 0);
        notifications.ShowSuccess(result.Message);
        return result;
    }

    /// <summary>
    /// 登録とフォルダ統合を1トランザクションで行う(DB設計書 11.6)。
    /// 子の登録フォルダの folders を親の配下へ付け替えてから、子の library_folders を削除する。
    /// 順序を逆にすると CASCADE で配下のアーカイブが消えるため、必ず付け替えを先に完了させる。
    /// </summary>
    private async Task<long> RegisterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fullPath,
        string normalized,
        bool isNetwork,
        List<LibraryFolder> children,
        CancellationToken ct)
    {
        var now = UnixTime.Now();
        var name = PathNormalizer.GetDisplayName(fullPath);
        var libraryId = await libraries.InsertLibraryFolderAsync(
            connection, transaction, fullPath, normalized, name, isNetwork, now, ct);
        var rootFolderId = await libraries.InsertFolderAsync(
            connection, transaction, libraryId, null, name, fullPath, normalized, string.Empty, 0, now, ct);

        foreach (var child in children)
        {
            await MergeChildAsync(connection, transaction, libraryId, rootFolderId, fullPath, normalized, child, now, ct);
        }

        var message = children.Count == 0
            ? string.Create(CultureInfo.CurrentCulture, $"管理フォルダを追加しました: {fullPath}")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"管理フォルダを追加しました: {fullPath}(配下の登録フォルダ{children.Count}件を統合)");
        await history.InsertAsync(
            connection,
            transaction,
            new OperationResult(
                OperationType.LibraryFolderAdd,
                OperationStatus.Success,
                message,
                AffectedCount: children.Count,
                NewPath: fullPath),
            ct);

        return libraryId;
    }

    private async Task MergeChildAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long parentLibraryId,
        long parentRootFolderId,
        string parentPath,
        string parentNormalized,
        LibraryFolder child,
        long now,
        CancellationToken ct)
    {
        // 親の起点から子の起点までの相対パス。間のフォルダは未登録のため、先に作成してツリーをつなぐ。
        var relative = Path.GetRelativePath(parentPath, child.Path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var parentFolderId = parentRootFolderId;
        var currentPath = parentPath;
        var currentNormalized = parentNormalized;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            currentPath = Path.Combine(currentPath, segments[i]);
            currentNormalized = PathNormalizer.Normalize(currentPath);
            var existingId = await libraries.FindFolderIdAsync(connection, transaction, currentNormalized, ct);
            parentFolderId = existingId ?? await libraries.InsertFolderAsync(
                connection,
                transaction,
                parentLibraryId,
                parentFolderId,
                segments[i],
                currentPath,
                currentNormalized,
                string.Join(Path.DirectorySeparatorChar, segments.Take(i + 1)),
                i + 1,
                now,
                ct);
        }

        await libraries.MoveFoldersToLibraryAsync(
            connection, transaction, child.Id, parentLibraryId, string.Join(Path.DirectorySeparatorChar, segments), segments.Length, now, ct);
        await libraries.SetFolderParentAsync(connection, transaction, child.NormalizedPath, parentFolderId, ct);
        await libraries.DeleteLibraryFolderAsync(connection, transaction, child.Id, ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "管理フォルダを管理対象から外しました。LibraryFolderId={Id} Archives={Count}")]
    private static partial void LogRemoved(ILogger logger, long id, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "管理フォルダを管理対象から外せませんでした。LibraryFolderId={Id}")]
    private static partial void LogRemoveFailed(ILogger logger, Exception exception, long id);

    [LoggerMessage(Level = LogLevel.Information, Message = "管理フォルダを追加しました。Path={Path} LibraryFolderId={Id} MergedChildren={Merged}")]
    private static partial void LogAdded(ILogger logger, string path, long id, int merged);
}
