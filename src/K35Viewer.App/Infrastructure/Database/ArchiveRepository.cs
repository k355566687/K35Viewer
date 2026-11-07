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
