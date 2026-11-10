using K35Viewer.Domain;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>library_folders と folders の永続化。書き込みメソッドは呼び出し元のトランザクション内で実行する。</summary>
public sealed class LibraryRepository(SqliteConnectionFactory connectionFactory)
{
    private const string LibraryFolderColumns =
        "id, path, normalized_path, display_name, is_network, is_enabled, availability_status, last_scan_at";

    // ---- 読み取り(自前の接続) ----

    public async Task<IReadOnlyList<LibraryFolder>> GetLibraryFoldersAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await GetLibraryFoldersAsync(connection, null, ct);
    }

    public async Task<LibraryFolder?> GetLibraryFolderAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await GetLibraryFolderAsync(connection, null, id, ct);
    }

    public async Task<IReadOnlyList<LibraryFolderSummary>> GetSummariesAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            """
            SELECT lf.id, lf.path, lf.display_name, lf.availability_status, lf.last_scan_at,
                   (SELECT COUNT(*)
                      FROM archives a
                      JOIN folders f ON a.folder_id = f.id
                     WHERE f.library_folder_id = lf.id
                       AND a.availability_status = 'AVAILABLE')
              FROM library_folders lf
             ORDER BY lf.path COLLATE NOCASE
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<LibraryFolderSummary>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LibraryFolderSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetNullableString(2),
                reader.GetString(3),
                reader.GetNullableInt64(4),
                (int)reader.GetInt64(5)));
        }

        return result;
    }

    /// <summary>フォルダツリー用に、全フォルダを登録フォルダの状態つきで取得する。</summary>
    public async Task<IReadOnlyList<FolderTreeRow>> GetFolderTreeRowsAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            """
            SELECT f.id, f.parent_folder_id, f.name, f.full_path, f.availability_status, lf.availability_status,
                   f.library_folder_id
              FROM folders f
              JOIN library_folders lf ON f.library_folder_id = lf.id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<FolderTreeRow>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FolderTreeRow(
                reader.GetInt64(0),
                reader.GetNullableInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)));
        }

        return result;
    }

    // ---- トランザクション内の読み取り・書き込み ----

    /// <summary>登録フォルダ配下のサムネイル画像ファイルの相対パス（管理対象から外すときに、ファイル実体を削除するため）。</summary>
    public async Task<IReadOnlyList<string>> GetThumbnailPathsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long libraryFolderId, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            SELECT t.relative_path
              FROM thumbnail_cache t
              JOIN archives a ON t.archive_id = a.id
              JOIN folders f ON a.folder_id = f.id
             WHERE f.library_folder_id = $id AND t.relative_path IS NOT NULL
            """,
            ("$id", libraryFolderId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>登録フォルダ配下のアーカイブの件数（不在のものを含む）。</summary>
    public async Task<int> CountArchivesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long libraryFolderId, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            SELECT COUNT(*)
              FROM archives a
              JOIN folders f ON a.folder_id = f.id
             WHERE f.library_folder_id = $id
            """,
            ("$id", libraryFolderId));
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<LibraryFolder>> GetLibraryFoldersAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection, transaction, $"SELECT {LibraryFolderColumns} FROM library_folders ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<LibraryFolder>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadLibraryFolder(reader));
        }

        return result;
    }

    public async Task<LibraryFolder?> GetLibraryFolderAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long id, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection, transaction, $"SELECT {LibraryFolderColumns} FROM library_folders WHERE id = $id", ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLibraryFolder(reader) : null;
    }

    public async Task<long> InsertLibraryFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        string normalizedPath,
        string displayName,
        bool isNetwork,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO library_folders (path, normalized_path, display_name, is_network, created_at, updated_at)
            VALUES ($path, $normalized, $name, $network, $now, $now)
            RETURNING id
            """,
            ("$path", path),
            ("$normalized", normalizedPath),
            ("$name", displayName),
            ("$network", isNetwork ? 1 : 0),
            ("$now", now));
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task DeleteLibraryFolderAsync(
        SqliteConnection connection, SqliteTransaction transaction, long id, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection, transaction, "DELETE FROM library_folders WHERE id = $id", ("$id", id));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>スキャン終了時の状態を反映する。lastScanAt が null の場合は最終スキャン日時を変更しない。</summary>
    public async Task SetScanStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        string availabilityStatus,
        long? lastScanAt,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE library_folders
               SET availability_status = $status,
                   last_scan_at = COALESCE($scanAt, last_scan_at),
                   updated_at = $now
             WHERE id = $id
            """,
            ("$id", id),
            ("$status", availabilityStatus),
            ("$scanAt", lastScanAt),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FolderRecord>> GetFoldersAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long libraryFolderId, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            SELECT id, library_folder_id, parent_folder_id, name, full_path, normalized_path, relative_path, depth
              FROM folders
             WHERE library_folder_id = $id
            """,
            ("$id", libraryFolderId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<FolderRecord>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FolderRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetNullableInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                (int)reader.GetInt64(7)));
        }

        return result;
    }

    public async Task<long?> FindFolderIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, string normalizedPath, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            "SELECT id FROM folders WHERE normalized_path = $normalized",
            ("$normalized", normalizedPath));
        return (long?)await command.ExecuteScalarAsync(ct);
    }

    public async Task<long> InsertFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long libraryFolderId,
        long? parentFolderId,
        string name,
        string fullPath,
        string normalizedPath,
        string relativePath,
        int depth,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO folders (library_folder_id, parent_folder_id, name, full_path, normalized_path,
                                 relative_path, depth, availability_status, created_at, updated_at)
            VALUES ($library, $parent, $name, $full, $normalized, $relative, $depth, 'AVAILABLE', $now, $now)
            RETURNING id
            """,
            ("$library", libraryFolderId),
            ("$parent", parentFolderId),
            ("$name", name),
            ("$full", fullPath),
            ("$normalized", normalizedPath),
            ("$relative", relativePath),
            ("$depth", depth),
            ("$now", now));
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>スキャンで存在を確認したフォルダを更新する。</summary>
    public async Task MarkFolderSeenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        string fullPath,
        long scanId,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE folders
               SET full_path = $full, availability_status = 'AVAILABLE',
                   last_seen_scan_id = $scan, updated_at = $now
             WHERE id = $id
            """,
            ("$id", folderId),
            ("$full", fullPath),
            ("$scan", scanId),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFolderUnavailableAsync(
        SqliteConnection connection, SqliteTransaction transaction, long folderId, long now, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            "UPDATE folders SET availability_status = 'UNAVAILABLE', updated_at = $now WHERE id = $id",
            ("$id", folderId),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 登録フォルダ配下のフォルダを別の登録フォルダの配下へ付け替える(親フォルダ追加時の統合)。
    /// relativePrefix は新しい登録フォルダからの、移動元登録フォルダの相対パス。
    /// </summary>
    public async Task MoveFoldersToLibraryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fromLibraryFolderId,
        long toLibraryFolderId,
        string relativePrefix,
        int depthOffset,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE folders
               SET library_folder_id = $to,
                   relative_path = CASE WHEN relative_path = '' THEN $prefix
                                        ELSE $prefix || '\' || relative_path END,
                   depth = depth + $offset,
                   updated_at = $now
             WHERE library_folder_id = $from
            """,
            ("$from", fromLibraryFolderId),
            ("$to", toLibraryFolderId),
            ("$prefix", relativePrefix),
            ("$offset", depthOffset),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetFolderParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedPath,
        long parentFolderId,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            "UPDATE folders SET parent_folder_id = $parent WHERE normalized_path = $normalized",
            ("$parent", parentFolderId),
            ("$normalized", normalizedPath));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static LibraryFolder ReadLibraryFolder(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetNullableString(3),
            reader.GetInt64(4) == 1,
            reader.GetInt64(5) == 1,
            reader.GetString(6),
            reader.GetNullableInt64(7));
}

/// <summary>フォルダツリーの組み立てに使う、folders と登録フォルダの結合結果。</summary>
public sealed record FolderTreeRow(
    long Id,
    long? ParentFolderId,
    string Name,
    string FullPath,
    string FolderAvailability,
    string LibraryAvailability,
    long LibraryFolderId);
