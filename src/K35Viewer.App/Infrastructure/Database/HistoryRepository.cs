using K35Viewer.Domain;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>operation_history の永続化。</summary>
public sealed class HistoryRepository
{
    public async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, OperationResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var executedAt = UnixTime.ToUnixSeconds(result.ExecutedAt ?? DateTimeOffset.UtcNow);
        await using var command = SqlHelper.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO operation_history (archive_id, operation_type, status, archive_name, old_path, new_path,
                                           affected_count, message, error_code, executed_at)
            VALUES ($archive, $type, $status, $name, $old, $new, $count, $message, $error, $at)
            """,
            ("$archive", result.ArchiveId),
            ("$type", result.OperationType.ToDbValue()),
            ("$status", result.Status.ToDbValue()),
            ("$name", result.ArchiveName),
            ("$old", result.OldPath),
            ("$new", result.NewPath),
            ("$count", result.AffectedCount),
            ("$message", result.Message),
            ("$error", result.ErrorCode),
            ("$at", executedAt));
        await command.ExecuteNonQueryAsync(ct);
    }
}
