using System.IO.Compression;
using System.Text.Json;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>アプリのデータのバックアップ（要件定義 24.5）。</summary>
public class BackupServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>時刻を、テストから進められる時計。ローカル時刻は UTC とする。</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    private static ManualClock NewClock() => new(new DateTimeOffset(2026, 11, 13, 10, 20, 30, TimeSpan.Zero));

    private static async Task<int> CountRowsAsync(string databaseFile, string table)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task バックアップには_DBと設定と説明が入り_DBは中身を写している()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = await ctx.RegisterAndScanAsync();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { Theme = AppTheme.Light });
        var service = ctx.CreateBackupService(settings, NewClock());

        var backup = await service.CreateAsync(BackupKind.Manual, ct: Ct);

        Assert.Equal("K35Viewer-backup-20261113-102030.zip", backup.FileName);
        Assert.Equal(BackupKind.Manual, backup.Kind);
        Assert.True(backup.SizeBytes > 0);
        using var zip = ZipFile.OpenRead(backup.FilePath);
        Assert.Equal(
            [BackupService.DatabaseEntry, BackupService.ManifestEntry, BackupService.SettingsEntry],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        var extracted = Path.Combine(ctx.CacheDirectory, "extracted");
        zip.ExtractToDirectory(extracted);
        Assert.Equal(2, await CountRowsAsync(Path.Combine(extracted, BackupService.DatabaseEntry), "archives"));
        Assert.False(File.Exists(Path.Combine(extracted, BackupService.DatabaseEntry + "-wal"))); // 1つのファイルとして持ち運べる
        Assert.Contains("\"Light\"", File.ReadAllText(Path.Combine(extracted, BackupService.SettingsEntry)), StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(extracted, BackupService.ManifestEntry)));
        Assert.Equal(BackupService.FormatVersion, manifest.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal("Manual", manifest.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(
            await ctx.ScalarAsync("SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version'"),
            manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task 設定のファイルがまだなくても_バックアップを作れる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var service = ctx.CreateBackupService(clock: NewClock());
        Assert.False(File.Exists(ctx.SettingsFile));

        var backup = await service.CreateAsync(BackupKind.Manual, ct: Ct);

        using var zip = ZipFile.OpenRead(backup.FilePath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName == BackupService.SettingsEntry);
        Assert.Contains(zip.Entries, e => e.FullName == BackupService.DatabaseEntry);
    }

    [Fact]
    public async Task 同じ秒に続けて作っても_上書きせず別の名前になり_作業用のフォルダは残らない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var service = ctx.CreateBackupService(clock: NewClock());

        var first = await service.CreateAsync(BackupKind.Manual, ct: Ct);
        var second = await service.CreateAsync(BackupKind.Manual, ct: Ct);

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal("K35Viewer-backup-20261113-102030-2.zip", second.FileName);
        Assert.Equal(2, service.List().Count);
        var temp = new AppPaths(ctx.CacheDirectory).TempDirectory;
        Assert.Empty(Directory.EnumerateDirectories(temp));
        Assert.Empty(Directory.EnumerateFiles(new AppPaths(ctx.CacheDirectory).BackupsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task 一覧は_新しいものが先で_種類を区別し_名前の形式に合わないファイルは含めない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var clock = NewClock();
        var service = ctx.CreateBackupService(clock: clock);
        _ = await service.CreateAsync(BackupKind.Manual, ct: Ct);
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await service.CreateAsync(BackupKind.Auto, ct: Ct);
        File.WriteAllText(Path.Combine(new AppPaths(ctx.CacheDirectory).BackupsDirectory, "メモ.zip"), "x");

        var list = service.List();

        Assert.Equal([BackupKind.Auto, BackupKind.Manual], list.Select(b => b.Kind));
        Assert.Equal(new DateTime(2026, 11, 13, 10, 25, 30), list[0].CreatedAt);
    }

    [Fact]
    public void バックアップがなければ_一覧は空()
    {
        using var ctx = new LibraryTestContext();

        Assert.Empty(ctx.CreateBackupService().List());
    }

    [Fact]
    public async Task 自動バックアップは_新しいものから設定の数だけ残し_手動のものは消さない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { AutoBackupKeepCount = 2 });
        var clock = NewClock();
        var service = ctx.CreateBackupService(settings, clock);
        var manual = await service.CreateAsync(BackupKind.Manual, ct: Ct); // いちばん古い手動
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            _ = await service.CreateAsync(BackupKind.Auto, ct: Ct);
        }

        var list = service.List();

        Assert.Equal(2, list.Count(b => b.Kind == BackupKind.Auto));
        Assert.Contains(list, b => b.FilePath == manual.FilePath); // 手動は、古くても残る
        Assert.Equal(
            ["K35Viewer-auto-20261113-102330.zip", "K35Viewer-auto-20261113-102430.zip"],
            list.Where(b => b.Kind == BackupKind.Auto).Select(b => b.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task 残す数を1にしても_復元元として守るファイルは消えない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { AutoBackupKeepCount = 1 });
        var clock = NewClock();
        var service = ctx.CreateBackupService(settings, clock);
        var source = await service.CreateAsync(BackupKind.Auto, ct: Ct); // 復元元（古い自動バックアップ）
        clock.Advance(TimeSpan.FromMinutes(1));

        // 復元の前の自動バックアップ。復元元は、残す数の外でも消さない
        var current = await service.CreateAsync(BackupKind.Auto, [source.FilePath], Ct);

        Assert.True(File.Exists(source.FilePath));
        Assert.True(File.Exists(current.FilePath));

        // 守るものがなければ、残す数のとおりに整理される
        service.PruneAutoBackups(1);
        Assert.False(File.Exists(source.FilePath));
        Assert.True(File.Exists(current.FilePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task 残す数が範囲の外でも_最低1つは残す(int keepCount)
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var clock = NewClock();
        var service = ctx.CreateBackupService(clock: clock);
        _ = await service.CreateAsync(BackupKind.Auto, ct: Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = await service.CreateAsync(BackupKind.Auto, ct: Ct);

        service.PruneAutoBackups(keepCount);

        _ = Assert.Single(service.List());
    }

    [Fact]
    public async Task 設定画面から作ると_通知され_最後のバックアップが表示され_保存場所を開ける()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        var paths = new AppPaths(ctx.CacheDirectory);
        using var viewModel = ctx.CreateSettingsViewModel(settings, new NoCache(), NewClock());
        await viewModel.InitializeAsync();
        Assert.Equal("まだありません", viewModel.LastBackupText);

        await viewModel.CreateBackupCommand.ExecuteAsync(null);
        viewModel.OpenBackupsFolderCommand.Execute(null);

        Assert.Equal("2026/11/13 10:20（手動）", viewModel.LastBackupText);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success && n.Message.Contains("K35Viewer-backup-", StringComparison.Ordinal));
        Assert.Equal([paths.BackupsDirectory], ctx.Explorer.OpenedFolders);
    }

    [Fact]
    public async Task 自動バックアップを残す数は_既定が5で_変えるとその場で保存され_範囲に収まる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        using var viewModel = ctx.CreateSettingsViewModel(settings, new NoCache());
        Assert.Equal(5, viewModel.AutoBackupKeepCount);
        Assert.Equal(Enumerable.Range(1, 20), viewModel.AutoBackupKeepChoices);

        viewModel.AutoBackupKeepCount = 1;
        Assert.Equal(1, ctx.CreateSettings().Current.AutoBackupKeepCount); // 再起動しても残る

        viewModel.AutoBackupKeepCount = 99;
        Assert.Equal(20, settings.Current.AutoBackupKeepCount);
    }

    /// <summary>キャッシュの管理の代わり（このテストでは使わない）。</summary>
    private sealed class NoCache : IThumbnailCacheMaintenance
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public string? ClearBlockedReason => null;

        public Task<long> GetCacheSizeAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<bool> ClearCacheAsync() => Task.FromResult(true);
    }
}
