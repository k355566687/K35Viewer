using K35Viewer.Domain;
using K35Viewer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Tests.Infrastructure;

public class DatabaseInitializerTests
{
    private static readonly string[] ExpectedTables =
    [
        "app_metadata", "library_folders", "scan_runs", "folders",
        "archives", "thumbnail_cache", "operation_history",
    ];

    [Fact]
    public async Task InitializeAsync_初回起動でDBと全テーブルを作成する()
    {
        using var db = new TempDatabase();
        Assert.False(File.Exists(db.DatabaseFile));

        await db.InitializeAsync();

        Assert.True(File.Exists(db.DatabaseFile));
        await using var connection = await db.OpenAsync();
        foreach (var table in ExpectedTables)
        {
            var count = await TempDatabase.ScalarLongAsync(
                connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'");
            Assert.Equal(1, count);
        }

        Assert.Equal(1, await MigrationRunner.GetSchemaVersionAsync(connection, TempDatabase.Ct));
    }

    [Fact]
    public async Task InitializeAsync_再起動してもDBを再作成しない()
    {
        using var db = new TempDatabase();
        await db.InitializeAsync();
        await using (var connection = await db.OpenAsync())
        {
            await TempDatabase.ExecuteAsync(
                connection,
                "INSERT INTO library_folders (path, normalized_path, created_at, updated_at) VALUES ('D:\\A', 'd:\\a', 1, 1)");
        }

        await db.InitializeAsync();

        await using var after = await db.OpenAsync();
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(after, "SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(1, await MigrationRunner.GetSchemaVersionAsync(after, TempDatabase.Ct));
    }

    [Fact]
    public async Task OpenAsync_接続PRAGMAが適用される()
    {
        using var db = new TempDatabase();

        await using var connection = await db.OpenAsync();

        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "PRAGMA foreign_keys"));
        Assert.Equal(5000, await TempDatabase.ScalarLongAsync(connection, "PRAGMA busy_timeout"));
        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await journal.ExecuteScalarAsync(TempDatabase.Ct));
    }

    [Fact]
    public async Task 外部キー制約が有効で_存在しないフォルダへのアーカイブ登録は失敗する()
    {
        using var db = new TempDatabase();
        await db.InitializeAsync();
        await using var connection = await db.OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(() => TempDatabase.ExecuteAsync(
            connection,
            "INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) " +
            "VALUES (999, 'a.zip', 'zip', 1, 1, 1, 1)"));
    }

    [Fact]
    public async Task 同一フォルダ内の同名ファイルは大文字小文字を区別せず重複登録できない()
    {
        using var db = new TempDatabase();
        await db.InitializeAsync();
        await using var connection = await db.OpenAsync();
        await TempDatabase.ExecuteAsync(
            connection,
            "INSERT INTO library_folders (id, path, normalized_path, created_at, updated_at) VALUES (1, 'D:\\A', 'd:\\a', 1, 1);" +
            "INSERT INTO folders (id, library_folder_id, name, full_path, normalized_path, relative_path, created_at, updated_at) " +
            "VALUES (1, 1, 'A', 'D:\\A', 'd:\\a', '', 1, 1);" +
            "INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) " +
            "VALUES (1, 'Book.zip', 'zip', 1, 1, 1, 1);");

        await Assert.ThrowsAsync<SqliteException>(() => TempDatabase.ExecuteAsync(
            connection,
            "INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) " +
            "VALUES (1, 'BOOK.ZIP', 'zip', 1, 1, 1, 1)"));
    }

    [Fact]
    public async Task 時刻は2038年以降でも64bit整数として保存_取得できる()
    {
        using var db = new TempDatabase();
        await db.InitializeAsync();
        var year2100 = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var seconds = UnixTime.ToUnixSeconds(year2100);
        Assert.True(seconds > int.MaxValue);

        await using var connection = await db.OpenAsync();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO library_folders (id, path, normalized_path, created_at, updated_at) VALUES (1, 'D:\\A', 'd:\\a', $t, $t)";
            insert.Parameters.AddWithValue("$t", seconds);
            await insert.ExecuteNonQueryAsync(TempDatabase.Ct);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT created_at FROM library_folders WHERE id = 1";
        var stored = (long)(await select.ExecuteScalarAsync(TempDatabase.Ct))!;

        Assert.Equal(seconds, stored);
        Assert.Equal(year2100, UnixTime.FromUnixSeconds(stored));
    }
}
