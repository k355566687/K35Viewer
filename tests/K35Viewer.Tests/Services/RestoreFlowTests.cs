using System.IO.Compression;
using System.Text;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.Backup;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>バックアップからの復元（要件定義 24.5）。準備、起動の最初の入れ替え、失敗したときの巻き戻し。</summary>
public class RestoreFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    /// <summary>実際のデータの保存先と同じ並び（<c>library.db</c> が保存先の直下）の、一時のデータ一式。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(AppPaths paths, SqliteConnectionFactory factory, SettingsService settings, ManualClock clock, int? supportedSchema)
        {
            Paths = paths;
            Factory = factory;
            Settings = settings;
            Clock = clock;
            Backups = new BackupService(paths, factory, settings, NullLogger<BackupService>.Instance, clock);
            Restore = new RestoreService(paths, Backups, NullLogger<RestoreService>.Instance, supportedSchema);
        }

        public AppPaths Paths { get; }

        public SqliteConnectionFactory Factory { get; }

        public SettingsService Settings { get; }

        public ManualClock Clock { get; }

        public BackupService Backups { get; }

        public RestoreService Restore { get; }

        public static async Task<Fixture> CreateAsync(int? supportedSchema = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "restore-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            var factory = new SqliteConnectionFactory(paths.DatabaseFile);
            await new DatabaseInitializer(factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
                NullLogger<DatabaseInitializer>.Instance).InitializeAsync(Ct);
            var settings = new SettingsService(new SettingsStore(paths.SettingsFile), NullLogger<SettingsService>.Instance);
            return new Fixture(paths, factory, settings, new ManualClock(new DateTimeOffset(2026, 11, 13, 10, 20, 30, TimeSpan.Zero)), supportedSchema);
        }

        public async Task SetMarkerAsync(string value)
        {
            await using var connection = await Factory.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO app_metadata (key, value, updated_at) VALUES ('test_marker', $v, 0) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            _ = command.Parameters.AddWithValue("$v", value);
            _ = await command.ExecuteNonQueryAsync(Ct);
        }

        public async Task<string?> GetMarkerAsync()
        {
            await using var connection = await Factory.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_metadata WHERE key = 'test_marker'";
            return await command.ExecuteScalarAsync(Ct) as string;
        }

        /// <summary>入れ替えの前に、DB のファイルを手放す（本物では、再起動で、プロセスごと手放す）。</summary>
        public static void ReleaseDatabase() => SqliteConnection.ClearAllPools();

        public void Dispose()
        {
            ReleaseDatabase();
            TestFileOperations.DeleteDirectory(Paths.Root);
        }
    }

    private static string MakeZip(string path, params (string Name, byte[] Data)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }

    private static byte[] Manifest(int schema = 1, int format = 1) =>
        Encoding.UTF8.GetBytes(
            $$"""{"FormatVersion":{{format}},"AppVersion":"9.9.9","SchemaVersion":{{schema}},"CreatedAt":"2026-11-13T10:20:30+00:00","Kind":"Manual"}""");

    // ---- 起動の最初の入れ替え ----

    [Fact]
    public async Task 復元すると_DBと設定が戻り_サムネイルのキャッシュは消え_作業用のファイルは残らない()
    {
        using var fx = await Fixture.CreateAsync();
        await fx.SetMarkerAsync("before");
        fx.Settings.Update(s => s with { Theme = AppTheme.Light });
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await fx.SetMarkerAsync("after");
        fx.Settings.Update(s => s with { Theme = AppTheme.System });
        var thumbnails = new ThumbnailCacheStore(fx.Paths.ThumbnailsDirectory);
        _ = thumbnails.Save(1, new byte[100]);
        await fx.Restore.PrepareAsync(backup.FilePath, Ct);
        Assert.True(File.Exists(fx.Paths.RestorePendingFile));
        Fixture.ReleaseDatabase();

        var result = PendingRestore.TryApply(fx.Paths);

        Assert.Equal(RestoreApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 10, 20, 30, TimeSpan.Zero), result.BackupCreatedAt);
        Assert.Equal("before", await fx.GetMarkerAsync());
        Assert.Equal(AppTheme.Light, new SettingsStore(fx.Paths.SettingsFile).Load().Settings.Theme);
        Assert.Equal(0, thumbnails.GetTotalBytes());
        Assert.False(File.Exists(fx.Paths.RestorePendingFile));
        Assert.False(Directory.Exists(fx.Paths.RestoreStagingDirectory));
        Assert.False(Directory.Exists(fx.Paths.RestoreRollbackDirectory));
        Assert.Single(fx.Backups.List(), b => b.Kind == BackupKind.Auto); // 復元の前の、今のデータ
    }

    [Fact]
    public async Task 復元前の自動バックアップには_復元の直前のデータが入っている()
    {
        using var fx = await Fixture.CreateAsync();
        await fx.SetMarkerAsync("before");
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await fx.SetMarkerAsync("after");
        await fx.Restore.PrepareAsync(backup.FilePath, Ct);
        Fixture.ReleaseDatabase();
        _ = PendingRestore.TryApply(fx.Paths);

        // 自動バックアップを使って、復元の前の状態へ戻せる
        var auto = Assert.Single(fx.Backups.List(), b => b.Kind == BackupKind.Auto);
        await fx.Restore.PrepareAsync(auto.FilePath, Ct);
        Fixture.ReleaseDatabase();
        Assert.Equal(RestoreApplyOutcome.Succeeded, PendingRestore.TryApply(fx.Paths).Outcome);
        Assert.Equal("after", await fx.GetMarkerAsync());
    }

    [Fact]
    public async Task バックアップに設定がなければ_復元のあとも設定のファイルはない()
    {
        using var fx = await Fixture.CreateAsync();
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct); // この時点では、設定のファイルがない
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        fx.Settings.Update(s => s with { Theme = AppTheme.Light });
        Assert.True(File.Exists(fx.Paths.SettingsFile));
        await fx.Restore.PrepareAsync(backup.FilePath, Ct);
        Fixture.ReleaseDatabase();

        var result = PendingRestore.TryApply(fx.Paths);

        Assert.Equal(RestoreApplyOutcome.Succeeded, result.Outcome);
        Assert.False(File.Exists(fx.Paths.SettingsFile));
    }

    [Fact]
    public async Task 入れ替えの途中で失敗したら_元のデータに戻り_予定と作業用のファイルは片付く()
    {
        using var fx = await Fixture.CreateAsync();
        await fx.SetMarkerAsync("before");
        fx.Settings.Update(s => s with { Theme = AppTheme.Light });
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await fx.SetMarkerAsync("after");
        fx.Settings.Update(s => s with { Theme = AppTheme.System });
        await fx.Restore.PrepareAsync(backup.FilePath, Ct);
        Fixture.ReleaseDatabase();

        RestoreApplyResult result;
        using (new FileStream(fx.Paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None)) // 設定のファイルを動かせない
        {
            result = PendingRestore.TryApply(fx.Paths);
        }

        Assert.Equal(RestoreApplyOutcome.Failed, result.Outcome);
        Assert.Contains("元のデータ", result.Message, StringComparison.Ordinal);
        Assert.Equal("after", await fx.GetMarkerAsync()); // DB は、元のまま
        Assert.Equal(AppTheme.System, new SettingsStore(fx.Paths.SettingsFile).Load().Settings.Theme);
        Assert.False(File.Exists(fx.Paths.RestorePendingFile));
        Assert.False(Directory.Exists(fx.Paths.RestoreStagingDirectory));
        Assert.False(Directory.Exists(fx.Paths.RestoreRollbackDirectory));
    }

    [Fact]
    public async Task 予定だけが残っていて展開したDBがない場合は_復元せず元のデータのまま()
    {
        using var fx = await Fixture.CreateAsync();
        await fx.SetMarkerAsync("current");
        PendingRestore.WriteMarker(fx.Paths, new PendingRestoreMarker("x.zip", "y.zip", DateTimeOffset.Now, DateTimeOffset.Now));
        Fixture.ReleaseDatabase();

        var result = PendingRestore.TryApply(fx.Paths);

        Assert.Equal(RestoreApplyOutcome.Failed, result.Outcome);
        Assert.Equal("current", await fx.GetMarkerAsync());
        Assert.False(File.Exists(fx.Paths.RestorePendingFile));
    }

    [Fact]
    public async Task 予定の目印が壊れていたら_復元せずに片付ける()
    {
        using var fx = await Fixture.CreateAsync();
        await fx.SetMarkerAsync("current");
        File.WriteAllText(fx.Paths.RestorePendingFile, "これはJSONではない");
        Fixture.ReleaseDatabase();

        var result = PendingRestore.TryApply(fx.Paths);

        Assert.Equal(RestoreApplyOutcome.Failed, result.Outcome);
        Assert.Equal("current", await fx.GetMarkerAsync());
        Assert.False(File.Exists(fx.Paths.RestorePendingFile));
    }

    [Fact]
    public async Task 前回の入れ替えが途中で止まっていたら_退避したファイルを元に戻す()
    {
        using var fx = await Fixture.CreateAsync();
        // 止まった状態: 退避したファイル（元の設定）と、置いたあとのファイル（復元元の設定）が、両方ある
        _ = Directory.CreateDirectory(fx.Paths.RestoreRollbackDirectory);
        File.WriteAllText(Path.Combine(fx.Paths.RestoreRollbackDirectory, "settings.json"), "ORIGINAL");
        File.WriteAllText(fx.Paths.SettingsFile, "NEW");
        PendingRestore.WriteMarker(fx.Paths, new PendingRestoreMarker("x.zip", "y.zip", DateTimeOffset.Now, DateTimeOffset.Now));

        var result = PendingRestore.TryApply(fx.Paths);

        Assert.Equal(RestoreApplyOutcome.Failed, result.Outcome);
        Assert.Contains("途中で止まった", result.Message, StringComparison.Ordinal);
        Assert.Equal("ORIGINAL", File.ReadAllText(fx.Paths.SettingsFile));
        Assert.False(Directory.Exists(fx.Paths.RestoreRollbackDirectory));
        Assert.False(File.Exists(fx.Paths.RestorePendingFile));
    }

    [Fact]
    public async Task 予定がなければ_何もしない()
    {
        using var fx = await Fixture.CreateAsync();
        await fx.SetMarkerAsync("current");
        Fixture.ReleaseDatabase();

        Assert.Equal(RestoreApplyOutcome.NothingPending, PendingRestore.TryApply(fx.Paths).Outcome);
        Assert.Equal("current", await fx.GetMarkerAsync());
    }

    // ---- 復元の準備 ----

    [Fact]
    public async Task 確認_ZIPではないファイルと_形の違うZIPは_理由を返す()
    {
        using var fx = await Fixture.CreateAsync();
        var notZip = Path.Combine(fx.Paths.Root, "memo.zip");
        File.WriteAllText(notZip, "ただのテキスト");
        var noDatabase = MakeZip(Path.Combine(fx.Paths.Root, "nodb.zip"), (BackupEntries.Manifest, Manifest()));
        var noManifest = MakeZip(Path.Combine(fx.Paths.Root, "nomanifest.zip"), (BackupEntries.Database, [1, 2, 3]));

        foreach (var file in new[] { notZip, noDatabase, noManifest })
        {
            var check = await fx.Restore.CheckAsync(file, Ct);
            Assert.False(check.IsValid);
            Assert.False(string.IsNullOrEmpty(check.Error));
        }
    }

    [Fact]
    public async Task 確認_今のアプリより新しい版で作ったバックアップは_復元できない()
    {
        using var fx = await Fixture.CreateAsync();
        var newerSchema = MakeZip(
            Path.Combine(fx.Paths.Root, "newer-schema.zip"),
            (BackupEntries.Database, [1]), (BackupEntries.Manifest, Manifest(schema: 999)));
        var newerFormat = MakeZip(
            Path.Combine(fx.Paths.Root, "newer-format.zip"),
            (BackupEntries.Database, [1]), (BackupEntries.Manifest, Manifest(format: 99)));

        foreach (var file in new[] { newerSchema, newerFormat })
        {
            var check = await fx.Restore.CheckAsync(file, Ct);
            Assert.False(check.IsValid);
            Assert.Contains("新しい版", check.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 確認_作ったバックアップは_復元できると判定される()
    {
        using var fx = await Fixture.CreateAsync();
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct);

        var check = await fx.Restore.CheckAsync(backup.FilePath, Ct);

        Assert.True(check.IsValid);
        Assert.Equal(BackupKind.Manual, check.Manifest!.Kind);
    }

    [Fact]
    public async Task 準備は_今のデータを自動でバックアップし_展開して_予定を残す()
    {
        using var fx = await Fixture.CreateAsync();
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(1));

        await fx.Restore.PrepareAsync(backup.FilePath, Ct);

        Assert.Single(fx.Backups.List(), b => b.Kind == BackupKind.Auto);
        Assert.True(File.Exists(Path.Combine(fx.Paths.RestoreStagingDirectory, BackupEntries.Database)));
        Assert.True(File.Exists(fx.Paths.RestorePendingFile));
        Assert.True(File.Exists(fx.Paths.DatabaseFile)); // 準備では、今のデータに触れない
    }

    [Fact]
    public async Task 残す数が1でも_復元元の自動バックアップは消えない()
    {
        using var fx = await Fixture.CreateAsync();
        fx.Settings.Update(s => s with { AutoBackupKeepCount = 1 });
        var source = await fx.Backups.CreateAsync(BackupKind.Auto, ct: Ct);
        fx.Clock.Advance(TimeSpan.FromMinutes(1));

        await fx.Restore.PrepareAsync(source.FilePath, Ct); // 復元前の自動バックアップを作る。復元元は、整理の対象から外す

        Assert.True(File.Exists(source.FilePath));
        Assert.Equal(2, fx.Backups.List().Count(b => b.Kind == BackupKind.Auto));
    }

    [Fact]
    public async Task DBが壊れているバックアップは_何も変更せずに断る()
    {
        using var fx = await Fixture.CreateAsync();
        var broken = MakeZip(
            Path.Combine(fx.Paths.Root, "broken.zip"),
            (BackupEntries.Database, Encoding.UTF8.GetBytes("これはSQLiteではない".PadRight(5000, '。'))),
            (BackupEntries.Manifest, Manifest()));

        var error = await Assert.ThrowsAsync<RestoreRejectedException>(() => fx.Restore.PrepareAsync(broken, Ct));

        Assert.False(string.IsNullOrEmpty(error.Message));
        Assert.False(Directory.Exists(fx.Paths.RestoreStagingDirectory));
        Assert.False(File.Exists(fx.Paths.RestorePendingFile));
        Assert.Empty(fx.Backups.List()); // 自動バックアップも作らない
    }

    [Fact]
    public async Task 展開は決まった名前のファイルだけ_ZIPの中の細工した名前では_ほかの場所へ書かれない()
    {
        using var fx = await Fixture.CreateAsync();
        var backup = await fx.Backups.CreateAsync(BackupKind.Manual, ct: Ct);
        using (var zip = ZipFile.Open(backup.FilePath, ZipArchiveMode.Update))
        {
            foreach (var name in new[] { "../evil.txt", "..\\evil2.txt", "extra/evil3.txt" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write("悪い"u8);
            }
        }

        fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await fx.Restore.PrepareAsync(backup.FilePath, Ct);

        Assert.False(File.Exists(Path.Combine(fx.Paths.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(fx.Paths.Root, "evil2.txt")));
        Assert.Equal(
            [BackupEntries.Database],
            Directory.EnumerateFileSystemEntries(fx.Paths.RestoreStagingDirectory).Select(Path.GetFileName));
    }

    // ---- 設定画面からの復元 ----

    [Fact]
    public async Task 設定画面で復元を選ぶと_確認して準備し_再起動する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        var backup = await ctx.CreateBackupService(settings).CreateAsync(BackupKind.Manual, ct: Ct);
        var dialogs = new FakeBackupDialogs { PickResult = backup.FilePath, Confirm = true };
        var restarter = new FakeAppRestarter();
        using var viewModel = ctx.CreateSettingsViewModel(settings, new NoCache(), dialogs: dialogs, restarter: restarter);

        await viewModel.RestoreBackupCommand.ExecuteAsync(null);

        _ = Assert.Single(dialogs.Confirmations);
        Assert.Equal(1, restarter.Calls);
        Assert.True(File.Exists(new AppPaths(ctx.CacheDirectory).RestorePendingFile));
    }

    [Fact]
    public async Task 設定画面で_ファイルを選ばない_または確認でキャンセルすると_何も変えない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        var backup = await ctx.CreateBackupService(settings).CreateAsync(BackupKind.Manual, ct: Ct);
        var dialogs = new FakeBackupDialogs { PickResult = null };
        var restarter = new FakeAppRestarter();
        using var viewModel = ctx.CreateSettingsViewModel(settings, new NoCache(), dialogs: dialogs, restarter: restarter);

        await viewModel.RestoreBackupCommand.ExecuteAsync(null); // ファイルを選ばない
        Assert.Empty(dialogs.Confirmations);

        dialogs.PickResult = backup.FilePath;
        dialogs.Confirm = false;
        await viewModel.RestoreBackupCommand.ExecuteAsync(null); // 確認でキャンセル

        _ = Assert.Single(dialogs.Confirmations);
        Assert.Equal(0, restarter.Calls);
        Assert.False(File.Exists(new AppPaths(ctx.CacheDirectory).RestorePendingFile));
        Assert.Single(ctx.CreateBackupService(settings).List()); // 自動バックアップも作らない
    }

    [Fact]
    public async Task 設定画面で復元できないファイルを選ぶと_確認せずに理由を知らせる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var notZip = Path.Combine(ctx.Workspace, "memo.zip");
        File.WriteAllText(notZip, "ただのテキスト");
        var dialogs = new FakeBackupDialogs { PickResult = notZip, Confirm = true };
        var restarter = new FakeAppRestarter();
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), dialogs: dialogs, restarter: restarter);

        await viewModel.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Confirmations);
        Assert.Equal(0, restarter.Calls);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error);
    }

    [Fact]
    public async Task 再起動できなかった場合は_復元の予定を残し_警告する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var settings = ctx.CreateSettings();
        var backup = await ctx.CreateBackupService(settings).CreateAsync(BackupKind.Manual, ct: Ct);
        var dialogs = new FakeBackupDialogs { PickResult = backup.FilePath, Confirm = true };
        var restarter = new FakeAppRestarter { Result = false };
        using var viewModel = ctx.CreateSettingsViewModel(settings, new NoCache(), dialogs: dialogs, restarter: restarter);

        await viewModel.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(1, restarter.Calls);
        Assert.True(File.Exists(new AppPaths(ctx.CacheDirectory).RestorePendingFile)); // 起動し直せば、復元される
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning);
    }

    // ---- 再起動の引数 ----

    [Theory]
    [InlineData("--wait-for-exit 1234", 1234)]
    [InlineData("--other --wait-for-exit 7", 7)]
    [InlineData("--wait-for-exit", null)]
    [InlineData("--wait-for-exit abc", null)]
    [InlineData("--wait-for-exit -5", null)]
    [InlineData("", null)]
    public void 再起動の引数から_終わりを待つプロセスの番号を読み取る(string arguments, int? expected)
    {
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expected, RestartArguments.ParseWaitProcessId(args));
    }

    [Fact]
    public void 再起動の引数は_作ったものを読み取れる_すでに終わったプロセスは待たない()
    {
        var args = RestartArguments.ForProcess(Environment.ProcessId).Split(' ');
        Assert.Equal(Environment.ProcessId, RestartArguments.ParseWaitProcessId(args));

        // 存在しない番号のプロセスは、待たずに戻る
        var started = DateTime.UtcNow;
        RestartArguments.WaitForPreviousProcess(["--wait-for-exit", "2147483000"]);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
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
