using K35Viewer.Domain;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>scan_runs の永続化。</summary>
public sealed class ScanRepository
{
    public async Task<long> StartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScanType scanType,
        long libraryFolderId,
        long now,
        CancellationToken ct)
    {
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO scan_runs (scan_type, library_folder_id, status, started_at)
            VALUES ($type, $library, 'RUNNING', $now)
            RETURNING id
            """,
            ("$type", scanType.ToDbValue()),
            ("$library", libraryFolderId),
            ("$now", now));
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task CompleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long scanId,
        OperationStatus status,
        ScanSummary counts,
        long now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(counts);

        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            UPDATE scan_runs
               SET status = $status, completed_at = $now,
                   discovered_count = $discovered, added_count = $added, changed_count = $changed,
                   missing_count = $missing, failed_count = $failed, message = $message
             WHERE id = $id
            """,
            ("$id", scanId),
            ("$status", status.ToDbValue()),
            ("$now", now),
            ("$discovered", counts.Discovered),
            ("$added", counts.Added),
            ("$changed", counts.Changed),
            ("$missing", counts.Missing),
            ("$failed", counts.Failed),
            ("$message", counts.Message));
        await command.ExecuteNonQueryAsync(ct);
    }
}
