using Microsoft.Extensions.Logging;

namespace K35Viewer.Infrastructure.Database;

/// <summary>アプリ起動時のDB初期化。DBがなければ作成し、未適用のマイグレーションを適用する。</summary>
public sealed partial class DatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    MigrationRunner migrationRunner,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await InitializeAsync(Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly), ct);
    }

    internal async Task InitializeAsync(IReadOnlyList<Migration> migrations, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var version = await migrationRunner.MigrateAsync(connection, migrations, ct);
        LogInitialized(logger, connectionFactory.DatabaseFile, version);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DBを初期化しました。File={File} SchemaVersion={Version}")]
    private static partial void LogInitialized(ILogger logger, string file, int version);
}
