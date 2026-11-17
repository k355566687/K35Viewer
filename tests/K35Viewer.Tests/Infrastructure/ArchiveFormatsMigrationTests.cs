using K35Viewer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>
/// マイグレーション 0002（archives.extension に rar / cbr / 7z / cb7 を許す。表の作り直し）。
/// 既存のライブラリを壊さないこと（行、関連する表、索引、連番、外部キー）を確かめる。
/// </summary>
public class ArchiveFormatsMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<Migration> All => Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);

    private static IReadOnlyList<Migration> OnlyFirst => [.. All.Where(m => m.Version <= 1)];

    private static IReadOnlyList<Migration> UpToSecond => [.. All.Where(m => m.Version <= 2)];

    private static IReadOnlyList<Migration> UpToThird => [.. All.Where(m => m.Version <= 3)];

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) ?? "(null)";
    }

    /// <summary>バージョン1の状態の DB に、実際の使い方に近いデータを入れる（アーカイブ、サムネイル、操作履歴、削除済みの行）。</summary>
    private static async Task SeedAsync(SqliteConnection connection)
    {
        await TempDatabase.ExecuteAsync(connection, """
            INSERT INTO library_folders (id, path, normalized_path, created_at, updated_at) VALUES (1, 'D:\A', 'd:\a', 1, 1);
            INSERT INTO scan_runs (id, scan_type, status, started_at) VALUES (1, 'MANUAL', 'COMPLETED', 1);
            INSERT INTO folders (id, library_folder_id, name, full_path, normalized_path, relative_path, created_at, updated_at)
                VALUES (1, 1, 'A', 'D:\A', 'd:\a', '', 1, 1);
            INSERT INTO archives (id, folder_id, file_name, extension, file_size, last_write_time, image_count, first_image_entry,
                                  last_view_entry, last_view_index, last_viewed_at, analysis_status, error_code, error_message,
                                  last_seen_scan_id, created_at, updated_at)
                VALUES (1, 1, 'one.zip', 'zip', 1000, 10, 5, '001.jpg', '003.jpg', 2, 99, 'READY', NULL, NULL, 1, 1, 2),
                       (2, 1, 'two.cbz', 'cbz', 2000, 20, NULL, NULL, NULL, NULL, NULL, 'BROKEN', 'ARCHIVE_BROKEN', 'こわれている', NULL, 3, 4),
                       (3, 1, 'three.zip', 'zip', 3000, 30, 0, NULL, NULL, NULL, NULL, 'EMPTY', NULL, NULL, 1, 5, 6);
            INSERT INTO thumbnail_cache (archive_id, cache_key, relative_path, format, width, height, status)
                VALUES (1, 'archive-1', '0000\archive-1.jpg', 'jpg', 200, 300, 'READY');
            INSERT INTO operation_history (archive_id, operation_type, status, message, executed_at)
                VALUES (1, 'ARCHIVE_RENAME', 'SUCCESS', '名前を変えた', 7);
            -- id 4 まで使ってから消す（連番は、消えた行の分も進んでいる）
            INSERT INTO archives (id, folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at)
                VALUES (4, 1, 'gone.zip', 'zip', 1, 1, 1, 1);
            DELETE FROM archives WHERE id = 4;
            """);
    }

    [Fact]
    public async Task 新しいDBでは_すべての形式の拡張子を登録でき_それ以外は断る()
    {
        using var db = new TempDatabase();
        await db.InitializeAsync();
        await using var connection = await db.OpenAsync();
        await TempDatabase.ExecuteAsync(connection, """
            INSERT INTO library_folders (id, path, normalized_path, created_at, updated_at) VALUES (1, 'D:\A', 'd:\a', 1, 1);
            INSERT INTO folders (id, library_folder_id, name, full_path, normalized_path, relative_path, created_at, updated_at)
                VALUES (1, 1, 'A', 'D:\A', 'd:\a', '', 1, 1);
            """);

        foreach (var extension in new[] { "zip", "cbz", "rar", "cbr", "7z", "cb7" })
        {
            await TempDatabase.ExecuteAsync(
                connection,
                $"INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) VALUES (1, 'a.{extension}', '{extension}', 1, 1, 1, 1)");
        }

        Assert.Equal(6, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM archives"));
        var rejected = await Assert.ThrowsAsync<SqliteException>(() => TempDatabase.ExecuteAsync(
            connection,
            "INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) VALUES (1, 'a.exe', 'exe', 1, 1, 1, 1)"));
        Assert.Contains("CHECK", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task バージョン1のDBを移行しても_行と関連する表が変わらない()
    {
        using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        _ = await db.Runner.MigrateAsync(connection, OnlyFirst, Ct);
        await SeedAsync(connection);
        var before = await ScalarStringAsync(connection, """
            SELECT group_concat(id || '|' || folder_id || '|' || file_name || '|' || extension || '|' || file_size || '|' || last_write_time || '|' ||
                   IFNULL(image_count, 'n') || '|' || IFNULL(first_image_entry, 'n') || '|' || IFNULL(last_view_entry, 'n') || '|' ||
                   IFNULL(last_view_index, 'n') || '|' || IFNULL(last_viewed_at, 'n') || '|' || analysis_status || '|' || availability_status || '|' ||
                   IFNULL(error_code, 'n') || '|' || IFNULL(error_message, 'n') || '|' || IFNULL(last_seen_scan_id, 'n') || '|' ||
                   created_at || '|' || updated_at, ';')
            FROM (SELECT * FROM archives ORDER BY id)
            """);

        // 0002 だけを確かめる（0003 は EMPTY の行の状態を変えるため、別のテストで確かめる）
        var version = await db.Runner.MigrateAsync(connection, UpToSecond, Ct);

        Assert.Equal(2, version);
        var after = await ScalarStringAsync(connection, """
            SELECT group_concat(id || '|' || folder_id || '|' || file_name || '|' || extension || '|' || file_size || '|' || last_write_time || '|' ||
                   IFNULL(image_count, 'n') || '|' || IFNULL(first_image_entry, 'n') || '|' || IFNULL(last_view_entry, 'n') || '|' ||
                   IFNULL(last_view_index, 'n') || '|' || IFNULL(last_viewed_at, 'n') || '|' || analysis_status || '|' || availability_status || '|' ||
                   IFNULL(error_code, 'n') || '|' || IFNULL(error_message, 'n') || '|' || IFNULL(last_seen_scan_id, 'n') || '|' ||
                   created_at || '|' || updated_at, ';')
            FROM (SELECT * FROM archives ORDER BY id)
            """);
        Assert.Equal(before, after);
        Assert.Equal(3, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM archives"));

        // 子の表の行が、連鎖して消えていない（外部キーを切って作り直した効果）
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM thumbnail_cache WHERE archive_id = 1"));
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM operation_history WHERE archive_id = 1"));
    }

    [Fact]
    public async Task 移行のあとも_索引と連番と外部キーが元どおりに働く()
    {
        using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        _ = await db.Runner.MigrateAsync(connection, OnlyFirst, Ct);
        await SeedAsync(connection);
        var indexesBefore = await ScalarStringAsync(
            connection, "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'archives' AND sql IS NOT NULL ORDER BY name)");
        var sequenceBefore = await TempDatabase.ScalarLongAsync(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'archives'");
        Assert.Equal(4, sequenceBefore);

        _ = await db.Runner.MigrateAsync(connection, All, Ct);

        // 索引（作り直した7つ）
        Assert.Equal(
            indexesBefore,
            await ScalarStringAsync(
                connection, "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'archives' AND sql IS NOT NULL ORDER BY name)"));
        Assert.Contains("ux_archives_folder_file_name", indexesBefore, StringComparison.Ordinal);

        // 連番: 消えた行の分も、元と同じ位置から続く（ID が、再び使われない）
        Assert.Equal(sequenceBefore, await TempDatabase.ScalarLongAsync(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'archives'"));
        await TempDatabase.ExecuteAsync(
            connection,
            "INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) VALUES (1, 'new.7z', '7z', 1, 1, 1, 1)");
        Assert.Equal(5, await TempDatabase.ScalarLongAsync(connection, "SELECT id FROM archives WHERE file_name = 'new.7z'"));

        // 同じフォルダに、大文字小文字だけが違う同名は、登録できない（一意の索引）
        _ = await Assert.ThrowsAsync<SqliteException>(() => TempDatabase.ExecuteAsync(
            connection,
            "INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, created_at, updated_at) VALUES (1, 'ONE.zip', 'zip', 1, 1, 1, 1)"));

        // 外部キーは、オンに戻っていて、連鎖の削除も元どおり（アーカイブを消すと、サムネイルも消え、履歴の参照は空になる）
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "PRAGMA foreign_keys"));
        await TempDatabase.ExecuteAsync(connection, "DELETE FROM archives WHERE id = 1");
        Assert.Equal(0, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM thumbnail_cache"));
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM operation_history WHERE archive_id IS NULL"));
        await TempDatabase.ExecuteAsync(connection, "DELETE FROM folders WHERE id = 1");
        Assert.Equal(0, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM archives")); // フォルダを消すと、配下のアーカイブも消える
    }

    [Fact]
    public async Task 空のDBの移行でも_連番の行が不正にならない()
    {
        using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        _ = await db.Runner.MigrateAsync(connection, OnlyFirst, Ct);

        _ = await db.Runner.MigrateAsync(connection, All, Ct);

        Assert.Equal(0, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'archives' AND seq IS NULL"));
        Assert.Equal(0, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM archives"));
    }

    [Fact]
    public async Task 外部キーを切る移行で整合がとれなくなったら_全体を取り消し_外部キーはオンに戻る()
    {
        using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        var first = new Migration(1, "0001_base", """
            CREATE TABLE app_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE parent (id INTEGER PRIMARY KEY);
            CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
            INSERT INTO parent (id) VALUES (1);
            INSERT INTO child (id, parent_id) VALUES (1, 1);
            """);
        _ = await db.Runner.MigrateAsync(connection, [first], Ct);
        // 外部キーを切った状態では、親を消しても、子が残る（これを見逃さず、取り消すこと）
        var broken = new Migration(2, "0002_orphan", Migration.ForeignKeysOffMarker + "\nDELETE FROM parent WHERE id = 1;");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Runner.MigrateAsync(connection, [first, broken], Ct));

        Assert.Contains("外部キー", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM parent")); // 取り消された
        Assert.Equal(1, await MigrationRunner.GetSchemaVersionAsync(connection, Ct)); // 版も進まない
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "PRAGMA foreign_keys")); // オンに戻っている
    }

    [Fact]
    public async Task 画像なしのアーカイブだけが_解析待ちに戻る()
    {
        using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        _ = await db.Runner.MigrateAsync(connection, UpToSecond, Ct);
        await SeedAsync(connection);

        var version = await db.Runner.MigrateAsync(connection, UpToThird, Ct);

        Assert.Equal(3, version);
        Assert.Equal("PENDING", await ScalarStringAsync(connection, "SELECT analysis_status FROM archives WHERE id = 3")); // EMPTY だったもの
        Assert.Equal("READY", await ScalarStringAsync(connection, "SELECT analysis_status FROM archives WHERE id = 1"));
        Assert.Equal("BROKEN", await ScalarStringAsync(connection, "SELECT analysis_status FROM archives WHERE id = 2"));
        Assert.Equal("こわれている", await ScalarStringAsync(connection, "SELECT error_message FROM archives WHERE id = 2")); // ほかの行の内容は変わらない
        Assert.Equal(1, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM thumbnail_cache WHERE archive_id = 1"));
    }

    [Fact]
    public async Task 新着の列は_既存の行では0になり_0か1しか入らない()
    {
        using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        _ = await db.Runner.MigrateAsync(connection, UpToThird, Ct);
        await SeedAsync(connection);

        var version = await db.Runner.MigrateAsync(connection, All, Ct);

        Assert.Equal(4, version);
        Assert.Equal(0, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM archives WHERE is_new <> 0")); // 更新の直後に、全部が新着にならない
        Assert.Equal(3, await TempDatabase.ScalarLongAsync(connection, "SELECT COUNT(*) FROM archives"));
        _ = await Assert.ThrowsAsync<SqliteException>(() => TempDatabase.ExecuteAsync(
            connection, "UPDATE archives SET is_new = 2 WHERE id = 1"));
    }

    [Fact]
    public void 外部キーを切る移行は_SQLの印で見分ける()
    {
        Assert.True(new Migration(2, "x", Migration.ForeignKeysOffMarker + "\nSELECT 1;").DisablesForeignKeys);
        Assert.False(new Migration(2, "x", "SELECT 1;").DisablesForeignKeys);
        Assert.True(All.Single(m => m.Version == 2).DisablesForeignKeys);
        Assert.False(All.Single(m => m.Version == 1).DisablesForeignKeys);
    }
}
