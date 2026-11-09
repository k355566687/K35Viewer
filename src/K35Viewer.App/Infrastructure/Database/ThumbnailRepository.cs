using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>thumbnail_cache(サムネイル画像のメタデータ)の永続化。画像の実体は ThumbnailCacheStore が管理する。</summary>
public sealed class ThumbnailRepository(SqliteConnectionFactory connectionFactory)
{
    /// <summary>生成済み(READY)のサムネイルと、そのファイルの相対パス。ファイルの欠落確認に使う。</summary>
    public async Task<IReadOnlyList<(long ArchiveId, string RelativePath)>> GetReadyAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = SqlHelper.CreateCommand(
            connection,
            null,
            "SELECT archive_id, relative_path FROM thumbnail_cache WHERE status = 'READY' AND relative_path IS NOT NULL");
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<(long, string)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>ファイルが失われたサムネイルを、再生成待ち(STALE)にする。</summary>
    public async Task MarkStaleAsync(
        SqliteConnection connection, SqliteTransaction transaction, long archiveId, CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            "UPDATE thumbnail_cache SET status = 'STALE' WHERE archive_id = $id",
            ("$id", archiveId));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 生成に成功したサムネイルを記録する。sourceFileSize / sourceLastWriteTime は生成時点のアーカイブの状態で、
    /// 後からアーカイブが変わったことの検出(キャッシュの無効化)に使う。
    /// </summary>
    public async Task UpsertReadyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long archiveId,
        string relativePath,
        int width,
        int height,
        string sourceEntry,
        long sourceFileSize,
        long sourceLastWriteTime,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO thumbnail_cache (archive_id, cache_key, relative_path, format, width, height, source_entry,
                                         source_archive_size, source_last_write_time, status, generated_at, error_message)
            VALUES ($id, $key, $path, 'jpg', $width, $height, $entry, $size, $mtime, 'READY', $now, NULL)
            ON CONFLICT(archive_id) DO UPDATE SET
                relative_path = excluded.relative_path, format = excluded.format,
                width = excluded.width, height = excluded.height, source_entry = excluded.source_entry,
                source_archive_size = excluded.source_archive_size,
                source_last_write_time = excluded.source_last_write_time,
                status = 'READY', generated_at = excluded.generated_at, error_message = NULL
            """,
            ("$id", archiveId),
            ("$key", CacheKey(archiveId)),
            ("$path", relativePath),
            ("$width", width),
            ("$height", height),
            ("$entry", sourceEntry),
            ("$size", sourceFileSize),
            ("$mtime", sourceLastWriteTime),
            ("$now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 生成に失敗したことを記録する。アーカイブが変更されるまで再試行しない
    /// (sourceFileSize / sourceLastWriteTime が現在の状態と一致している間は対象外)。
    /// </summary>
    public async Task UpsertFailedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long archiveId,
        long sourceFileSize,
        long sourceLastWriteTime,
        string errorMessage,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO thumbnail_cache (archive_id, cache_key, relative_path, format, width, height, source_entry,
                                         source_archive_size, source_last_write_time, status, generated_at, error_message)
            VALUES ($id, $key, NULL, NULL, NULL, NULL, NULL, $size, $mtime, 'FAILED', $now, $message)
            ON CONFLICT(archive_id) DO UPDATE SET
                relative_path = NULL, format = NULL, width = NULL, height = NULL, source_entry = NULL,
                source_archive_size = excluded.source_archive_size,
                source_last_write_time = excluded.source_last_write_time,
                status = 'FAILED', generated_at = excluded.generated_at, error_message = excluded.error_message
            """,
            ("$id", archiveId),
            ("$key", CacheKey(archiveId)),
            ("$size", sourceFileSize),
            ("$mtime", sourceLastWriteTime),
            ("$now", now),
            ("$message", errorMessage));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>サムネイルの記録を削除し、削除前のファイルの相対パスを返す(なければ null)。</summary>
    public async Task<string?> DeleteAsync(
        SqliteConnection connection, SqliteTransaction transaction, long archiveId, CancellationToken ct)
    {
        string? relativePath;
        await using (var select = SqlHelper.CreateCommand(
            connection,
            transaction,
            "SELECT relative_path FROM thumbnail_cache WHERE archive_id = $id",
            ("$id", archiveId)))
        {
            relativePath = await select.ExecuteScalarAsync(ct) as string;
        }

        await using var delete = SqlHelper.CreateCommand(
            connection,
            transaction,
            "DELETE FROM thumbnail_cache WHERE archive_id = $id",
            ("$id", archiveId));
        await delete.ExecuteNonQueryAsync(ct);
        return relativePath;
    }

    private static string CacheKey(long archiveId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"archive-{archiveId}");
}
