using System.Globalization;
using K35Viewer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Infrastructure.Database;

/// <summary>
/// 未適用のマイグレーションを順に適用する。1マイグレーションを1トランザクションで実行し、
/// 成功後に app_metadata.schema_version を更新する。失敗時はそのマイグレーションをロールバックする。
/// </summary>
public sealed partial class MigrationRunner(ILogger<MigrationRunner> logger)
{
    public const string SchemaVersionKey = "schema_version";

    /// <summary>適用後の schema_version を返す。</summary>
    public async Task<int> MigrateAsync(
        SqliteConnection connection,
        IReadOnlyList<Migration> migrations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        var latest = migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);
        var current = await GetSchemaVersionAsync(connection, ct);

        if (current > latest)
        {
            throw new InvalidOperationException(
                $"DBのスキーマバージョン({current})がアプリが対応するバージョン({latest})より新しいため、開けません。");
        }

        foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await ApplyAsync(connection, migration, ct);
            current = migration.Version;
        }

        return current;
    }

    /// <summary>schema_version を返す。app_metadata が未作成(新規DB)の場合は0。</summary>
    public static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_metadata'";
            var count = Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (count == 0)
            {
                return 0;
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = $key";
        command.Parameters.AddWithValue("$key", SchemaVersionKey);
        var value = await command.ExecuteScalarAsync(ct) as string;
        return value is null ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private async Task ApplyAsync(SqliteConnection connection, Migration migration, CancellationToken ct)
    {
        LogApplying(logger, migration.Name);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var script = connection.CreateCommand())
            {
                script.Transaction = transaction;
                // マイグレーションSQLはアプリに埋め込んだ固定スクリプトであり、外部入力は含まない。
#pragma warning disable CA2100
                script.CommandText = migration.Sql;
#pragma warning restore CA2100
                await script.ExecuteNonQueryAsync(ct);
            }

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText =
                    "INSERT INTO app_metadata (key, value, updated_at) VALUES ($key, $value, $now) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at";
                version.Parameters.AddWithValue("$key", SchemaVersionKey);
                version.Parameters.AddWithValue("$value", migration.Version.ToString(CultureInfo.InvariantCulture));
                version.Parameters.AddWithValue("$now", UnixTime.Now());
                await version.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, migration.Name);
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        LogApplied(logger, migration.Name);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "マイグレーションを適用します: {Name}")]
    private static partial void LogApplying(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "マイグレーションを適用しました: {Name}")]
    private static partial void LogApplied(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "マイグレーションに失敗したためロールバックしました: {Name}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string name);
}
