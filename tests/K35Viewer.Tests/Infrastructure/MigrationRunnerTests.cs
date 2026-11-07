using K35Viewer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Tests.Infrastructure;

public class MigrationRunnerTests
{
    private const string CreateAppMetadata =
        "CREATE TABLE app_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL);";

    [Fact]
    public void LoadEmbedded_埋め込みマイグレーションが1から連番で読み込まれる()
    {
        var migrations = Migration.LoadEmbedded(typeof(Migration).Assembly);

        Assert.NotEmpty(migrations);
        for (var i = 0; i < migrations.Count; i++)
        {
            Assert.Equal(i + 1, migrations[i].Version);
        }
    }

    [Fact]
    public async Task MigrateAsync_失敗したマイグレーションはロールバックされ_バージョンは進まない()
    {
        using var db = new TempDatabase();
        var ok = new Migration(1, "0001_ok", CreateAppMetadata + " CREATE TABLE a (x INTEGER);");
        var broken = new Migration(2, "0002_broken", "CREATE TABLE b (x INTEGER); INSERT INTO no_such_table VALUES (1);");
        await using var connection = await db.OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(
            () => db.Runner.MigrateAsync(connection, [ok, broken], TempDatabase.Ct));

        Assert.Equal(1, await MigrationRunner.GetSchemaVersionAsync(connection, TempDatabase.Ct));
        Assert.Equal(0, await TempDatabase.ScalarLongAsync(
            connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'b'"));
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(
            connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'a'"));
    }

    [Fact]
    public async Task MigrateAsync_適用済みのマイグレーションは再実行されず_新しいものだけ適用される()
    {
        using var db = new TempDatabase();
        var first = new Migration(1, "0001_first", CreateAppMetadata);
        var second = new Migration(2, "0002_second", "CREATE TABLE added (x INTEGER);");
        await using var connection = await db.OpenAsync();

        Assert.Equal(1, await db.Runner.MigrateAsync(connection, [first], TempDatabase.Ct));
        // 0001を再実行するとテーブル重複で失敗するため、再実行されないことの確認になる
        Assert.Equal(2, await db.Runner.MigrateAsync(connection, [first, second], TempDatabase.Ct));
        Assert.Equal(2, await MigrationRunner.GetSchemaVersionAsync(connection, TempDatabase.Ct));
    }

    [Fact]
    public async Task MigrateAsync_アプリより新しいスキーマのDBは開けない()
    {
        using var db = new TempDatabase();
        var migrations = Migration.LoadEmbedded(typeof(Migration).Assembly);
        await db.InitializeAsync();
        await using var connection = await db.OpenAsync();
        await TempDatabase.ExecuteAsync(connection, "UPDATE app_metadata SET value = '999' WHERE key = 'schema_version'");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.Runner.MigrateAsync(connection, migrations, TempDatabase.Ct));
    }
}
