using System.Globalization;
using K35Viewer.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>テスト専用の一時ディレクトリに作るSQLite DB。破棄時にディレクトリごと削除する。</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", Guid.NewGuid().ToString("N"));
        Factory = new SqliteConnectionFactory(Path.Combine(_directory, "library.db"));
        Runner = new MigrationRunner(NullLogger<MigrationRunner>.Instance);
        Initializer = new DatabaseInitializer(Factory, Runner, NullLogger<DatabaseInitializer>.Instance);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SqliteConnectionFactory Factory { get; }

    public MigrationRunner Runner { get; }

    public DatabaseInitializer Initializer { get; }

    public string DatabaseFile => Factory.DatabaseFile;

    /// <summary>DBを開いて返す。</summary>
    public Task<SqliteConnection> OpenAsync() => Factory.OpenAsync(Ct);

    /// <summary>通常の初期化(全マイグレーション適用)を行う。</summary>
    public Task InitializeAsync() => Initializer.InitializeAsync(Ct);

    public void Dispose()
    {
        // 接続プールがファイルを掴んだままだと削除できないため解放する
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
