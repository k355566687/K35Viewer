using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
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
    HistoryRepository history,
    DatabaseWriter writer,
    FileSystemService fileSystem,
    INotificationService notifications,
    ILogger<LibraryService> logger)
{
    public Task<IReadOnlyList<LibraryFolderSummary>> GetLibraryFoldersAsync(CancellationToken ct = default) =>
        libraries.GetSummariesAsync(ct);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "管理フォルダを追加しました。Path={Path} LibraryFolderId={Id} MergedChildren={Merged}")]
    private static partial void LogAdded(ILogger logger, string path, long id, int merged);
}
