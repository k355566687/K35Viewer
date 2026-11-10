using K35Viewer.Domain;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>archives の永続化。</summary>
public sealed class ArchiveRepository(SqliteConnectionFactory connectionFactory)
{
    /// <summary>登録フォルダ配下の有効な(AVAILABLE)アーカイブ件数。</summary>
    public async Task<int> CountAvailableAsync(long libraryFolderId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            """
            SELECT COUNT(*)
              FROM archives a
              JOIN folders f ON a.folder_id = f.id
             WHERE f.library_folder_id = $id AND a.availability_status = 'AVAILABLE'
            """,
            ("$id", libraryFolderId));
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// 一覧用の検索。有効な(AVAILABLE)アーカイブだけを返す。
    /// フォルダ指定時は再帰CTEで、そのフォルダ自身と配下のすべてのフォルダを対象にする。
    /// SQLへ連結するのは固定の断片(並べ替え列・LIKE条件の枠)だけで、ユーザー入力は必ずパラメーターで渡す。
    /// </summary>
    public async Task<PagedResult<ArchiveSummary>> QueryAsync(ArchiveQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new List<(string Name, object? Value)>();
        var cte = string.Empty;
        var conditions = new List<string> { "a.availability_status = 'AVAILABLE'" };

        if (query.FolderId is { } folderId)
        {
            cte = """
                WITH RECURSIVE sub(id) AS (
                    SELECT $folder
                    UNION ALL
                    SELECT f.id FROM folders f JOIN sub s ON f.parent_folder_id = s.id)
                """;
            conditions.Add("a.folder_id IN (SELECT id FROM sub)");
            parameters.Add(("$folder", folderId));
        }

        var terms = (query.SearchText ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < terms.Length; i++)
        {
            conditions.Add($"a.file_name LIKE $term{i} ESCAPE '\\'");
            parameters.Add(($"$term{i}", "%" + EscapeLike(terms[i]) + "%"));
        }

        var where = string.Join(" AND ", conditions);
        var direction = query.Descending ? "DESC" : "ASC";
        var orderBy = query.SortKey switch
        {
            ArchiveSortKey.FileName => $"a.file_name COLLATE NOCASE {direction}",
            ArchiveSortKey.LastWriteTime => $"a.last_write_time {direction}",
            ArchiveSortKey.FileSize => $"a.file_size {direction}",
            // 未解析(NULL)は枚数0として扱い、昇順なら先頭、降順なら末尾に並べる
            ArchiveSortKey.ImageCount => $"COALESCE(a.image_count, 0) {direction}",
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.SortKey, null),
        };

        var paging = string.Empty;
        if (query.Limit is { } limit)
        {
            paging = " LIMIT $limit OFFSET $offset";
            parameters.Add(("$limit", limit));
            parameters.Add(("$offset", query.Offset ?? 0));
        }
        else if (query.Offset is { } offset)
        {
            paging = " LIMIT -1 OFFSET $offset";
            parameters.Add(("$offset", offset));
        }

        var countSql = $"{cte} SELECT COUNT(*) FROM archives a WHERE {where}";
        // サムネイルは READY のほか、再生成待ち(STALE)の間も古い画像を表示し続ける
        var listSql =
            $"{cte} SELECT a.id, a.file_name, f.full_path, a.file_size, a.last_write_time, a.image_count, a.analysis_status, " +
            "CASE WHEN t.status IN ('READY', 'STALE') THEN t.relative_path END, t.status " +
            "FROM archives a JOIN folders f ON a.folder_id = f.id " +
            $"LEFT JOIN thumbnail_cache t ON t.archive_id = a.id WHERE {where} " +
            $"ORDER BY {orderBy}, a.id{paging}";

        await using var connection = await connectionFactory.OpenAsync(ct);

        int total;
        await using (var countCommand = SqlHelper.CreateCommand(
            connection, null, countSql, parameters.Where(p => p.Name != "$limit" && p.Name != "$offset").ToArray()))
        {
            total = (int)(long)(await countCommand.ExecuteScalarAsync(ct))!;
        }

        var items = new List<ArchiveSummary>();
        await using (var listCommand = SqlHelper.CreateCommand(connection, null, listSql, [.. parameters]))
        {
            await using var reader = await listCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new ArchiveSummary(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetNullableString(7),
                    reader.GetNullableString(8)));
            }
        }

        return new PagedResult<ArchiveSummary>(items, total);
    }

    /// <summary>
    /// 解析またはサムネイル生成が必要なアーカイブのID。次のいずれか(有効なアーカイブのみ)。
    /// ・未解析(PENDING)、または前回ファイルを読めなかった(ERROR。呼び出し側が同一セッションでの再試行を制御する)
    /// ・解析済みで、サムネイルがない / 生成待ち(PENDING)/ 再生成待ち(STALE)/ 生成後にアーカイブが変更された
    /// 生成に失敗(FAILED)したものは、アーカイブが変更されるまで再試行しない。
    /// </summary>
    public async Task<IReadOnlyList<long>> GetPendingProcessingIdsAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            """
            SELECT a.id
              FROM archives a
              LEFT JOIN thumbnail_cache t ON t.archive_id = a.id
             WHERE a.availability_status = 'AVAILABLE'
               AND (a.analysis_status IN ('PENDING', 'ERROR')
                    OR (a.analysis_status = 'READY'
                        AND (t.archive_id IS NULL
                             OR t.status IN ('PENDING', 'STALE')
                             OR t.source_archive_size IS NOT a.file_size
                             OR t.source_last_write_time IS NOT a.last_write_time)))
             ORDER BY a.id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task<ArchiveProcessingTarget?> GetProcessingTargetAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            """
            SELECT a.id, f.full_path, a.file_name, a.file_size, a.last_write_time
              FROM archives a
              JOIN folders f ON a.folder_id = f.id
             WHERE a.id = $id AND a.availability_status = 'AVAILABLE'
            """,
            ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ArchiveProcessingTarget(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4))
            : null;
    }

    /// <summary>
    /// 解析結果を反映する。解析中にアーカイブが変更された場合(スキャンがサイズ・更新日時を更新した場合)に
    /// 古い内容の解析結果で上書きしないよう、解析時点のサイズ・更新日時が一致する場合のみ更新する。
    /// </summary>
    public async Task<bool> UpdateAnalysisAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArchiveProcessingTarget target,
        ArchiveAnalysisStatus status,
        int? imageCount,
        string? firstImageEntry,
        string? errorCode,
        string? errorMessage,
        long now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE archives
               SET analysis_status = $status, image_count = $count, first_image_entry = $entry,
                   error_code = $code, error_message = $message, updated_at = $now
             WHERE id = $id AND file_size = $size AND last_write_time = $mtime
               AND availability_status = 'AVAILABLE'
            """,
            ("$id", target.Id),
            ("$size", target.FileSize),
            ("$mtime", target.LastWriteTime),
            ("$status", status.ToDbValue()),
            ("$count", imageCount),
            ("$entry", firstImageEntry),
            ("$code", errorCode),
            ("$message", errorMessage),
            ("$now", now));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<ArchiveOpenInfo?> GetOpenInfoAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            """
            SELECT a.id, f.full_path, a.file_name, a.availability_status, a.last_view_entry, a.last_view_index
              FROM archives a
              JOIN folders f ON a.folder_id = f.id
             WHERE a.id = $id
            """,
            ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ArchiveOpenInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetNullableString(4),
                reader.IsDBNull(5) ? null : (int)reader.GetInt64(5))
            : null;
    }

    /// <summary>最後に閲覧した位置(画像エントリ名と、画像を自然順に並べたときの0始まりの位置)を記録する。</summary>
    public async Task UpdateLastViewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        string entryName,
        int index,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE archives
               SET last_view_entry = $entry, last_view_index = $index, last_viewed_at = $now, updated_at = $now
             WHERE id = $id
            """,
            ("$id", id),
            ("$entry", entryName),
            ("$index", index),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// ZIPの再構築（画像の削除）を反映する。サイズ・更新日時・画像枚数を新しいファイルの値にし、解析待ち(PENDING)に戻して
    /// サムネイルと表紙を作り直させる。最後に閲覧した位置は、更新後のアーカイブでの位置にする
    /// （位置がなければ、閲覧位置を消す）。スキャンが再構築の途中で不在(MISSING)にしていた場合も、有効に戻す。
    /// </summary>
    public async Task UpdateAfterRebuildAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        long fileSize,
        long lastWriteTime,
        int imageCount,
        string? lastViewEntry,
        int? lastViewIndex,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE archives
               SET file_size = $size, last_write_time = $mtime, image_count = $count, first_image_entry = NULL,
                   analysis_status = 'PENDING', error_code = NULL, error_message = NULL,
                   availability_status = 'AVAILABLE',
                   last_view_entry = $entry, last_view_index = $index,
                   last_viewed_at = CASE WHEN $entry IS NULL THEN NULL ELSE $now END,
                   updated_at = $now
             WHERE id = $id
            """,
            ("$id", id),
            ("$size", fileSize),
            ("$mtime", lastWriteTime),
            ("$count", imageCount),
            ("$entry", lastViewEntry),
            ("$index", lastViewIndex),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>LIKEのワイルドカード(% _)とエスケープ文字を、文字そのものとして扱えるようにする。</summary>
    internal static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>登録フォルダ配下のすべてのアーカイブ(不在を含む)。スキャンの差分判定用。</summary>
    public async Task<IReadOnlyList<ArchiveRecord>> GetByLibraryAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long libraryFolderId, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            SELECT a.id, a.folder_id, a.file_name, a.file_size, a.last_write_time, a.availability_status
              FROM archives a
              JOIN folders f ON a.folder_id = f.id
             WHERE f.library_folder_id = $id
            """,
            ("$id", libraryFolderId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<ArchiveRecord>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ArchiveRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5)));
        }

        return result;
    }

    /// <summary>1トランザクション内で大量の更新を行うための、準備済みコマンドの束。</summary>
    public ArchiveBatch CreateBatch(SqliteConnection connection, SqliteTransaction transaction) =>
        new(connection, transaction);
}

/// <summary>スキャン時のアーカイブ更新。コマンドを再利用して件数が多くても高速に処理する。</summary>
public sealed class ArchiveBatch : IDisposable
{
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _updateChanged;
    private readonly SqliteCommand _markStale;
    private readonly SqliteCommand _markSeen;
    private readonly SqliteCommand _markMissing;

    internal ArchiveBatch(SqliteConnection connection, SqliteTransaction transaction)
    {
        _insert = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time,
                                  analysis_status, availability_status, last_seen_scan_id, created_at, updated_at)
            VALUES ($folder, $name, $ext, $size, $mtime, 'PENDING', 'AVAILABLE', $scan, $now, $now)
            """);
        AddParameters(_insert, "$folder", "$name", "$ext", "$size", "$mtime", "$scan", "$now");

        _updateChanged = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE archives
               SET file_name = $name, file_size = $size, last_write_time = $mtime,
                   analysis_status = 'PENDING', availability_status = 'AVAILABLE',
                   error_code = NULL, error_message = NULL,
                   last_seen_scan_id = $scan, updated_at = $now
             WHERE id = $id
            """);
        AddParameters(_updateChanged, "$id", "$name", "$size", "$mtime", "$scan", "$now");

        _markStale = SqlHelper.CreateCommand(
            connection,
            transaction,
            "UPDATE thumbnail_cache SET status = 'STALE' WHERE archive_id = $id");
        AddParameters(_markStale, "$id");

        _markSeen = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE archives
               SET file_name = $name, availability_status = 'AVAILABLE',
                   last_seen_scan_id = $scan, updated_at = $now
             WHERE id = $id
            """);
        AddParameters(_markSeen, "$id", "$name", "$scan", "$now");

        _markMissing = SqlHelper.CreateCommand(
            connection,
            transaction,
            "UPDATE archives SET availability_status = 'MISSING', updated_at = $now WHERE id = $id");
        AddParameters(_markMissing, "$id", "$now");
    }

    public Task InsertAsync(
        long folderId, string fileName, long fileSize, long lastWriteTime, long scanId, long now, CancellationToken ct)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant() switch
        {
            "CBZ" => "cbz",
            _ => "zip",
        };
        return Run(
            _insert,
            ct,
            ("$folder", folderId),
            ("$name", fileName),
            ("$ext", extension),
            ("$size", fileSize),
            ("$mtime", lastWriteTime),
            ("$scan", scanId),
            ("$now", now));
    }

    /// <summary>サイズまたは更新日時が変わったアーカイブを更新し、解析待ちに戻し、サムネイルを古い扱いにする。</summary>
    public async Task UpdateChangedAsync(
        long id, string fileName, long fileSize, long lastWriteTime, long scanId, long now, CancellationToken ct)
    {
        await Run(
            _updateChanged,
            ct,
            ("$id", id),
            ("$name", fileName),
            ("$size", fileSize),
            ("$mtime", lastWriteTime),
            ("$scan", scanId),
            ("$now", now));
        await Run(_markStale, ct, ("$id", id));
    }

    public Task MarkSeenAsync(long id, string fileName, long scanId, long now, CancellationToken ct) =>
        Run(_markSeen, ct, ("$id", id), ("$name", fileName), ("$scan", scanId), ("$now", now));

    public Task MarkMissingAsync(long id, long now, CancellationToken ct) =>
        Run(_markMissing, ct, ("$id", id), ("$now", now));

    public void Dispose()
    {
        _insert.Dispose();
        _updateChanged.Dispose();
        _markStale.Dispose();
        _markSeen.Dispose();
        _markMissing.Dispose();
    }

    private static void AddParameters(SqliteCommand command, params string[] names)
    {
        foreach (var name in names)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }
    }

    private static async Task Run(SqliteCommand command, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            command.Parameters[name].Value = value ?? DBNull.Value;
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}
