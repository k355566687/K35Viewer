using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>
/// SQLite接続の生成元。接続は操作単位で開閉し、開くたびに設定PRAGMAを適用する。
/// </summary>
public sealed class SqliteConnectionFactory
{
    // foreign_keys / synchronous / busy_timeout は接続ごとの設定のため毎回適用する。
    // journal_mode=WAL はDBファイルに永続化されるが、適用を省略しても害はないため同じ箇所に置く。
    private const string ConnectionPragmas =
        "PRAGMA foreign_keys = ON; " +
        "PRAGMA journal_mode = WAL; " +
        "PRAGMA synchronous = NORMAL; " +
        "PRAGMA busy_timeout = 5000;";

    private readonly string _connectionString;

    public SqliteConnectionFactory(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);
        DatabaseFile = databaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DatabaseFile { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = ConnectionPragmas;
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
