using K35Viewer.Domain;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Logging;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>ログに記録された内容を集めるテスト用のロガー。</summary>
internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>安定化（Phase 10）: 異常終了後の後始末、想定外の例外の記録、接続の切断や容量不足などの異常系。</summary>
public class StabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempNameFor(string fileName) => $"{fileName}.{Guid.NewGuid():N}{FileSystemService.TempFileSuffix}";

    private static void MakeOld(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

    // ---- 管理フォルダの中に残った一時ファイル ----

    [Fact]
    public void 一時ファイルの名前から_元のファイル名を取り出せる()
    {
        Assert.Equal("a.zip", FileSystemService.GetTempSourceName(@"C:\x\" + TempNameFor("a.zip")));
        Assert.Equal("第1巻.v2.cbz", FileSystemService.GetTempSourceName(TempNameFor("第1巻.v2.cbz")));
        Assert.Null(FileSystemService.GetTempSourceName("a.zip.k35tmp")); // GUIDがない
        Assert.Null(FileSystemService.GetTempSourceName("a.zip.0123.k35tmp")); // GUIDの形式ではない
        Assert.Null(FileSystemService.GetTempSourceName("a.zip"));
        Assert.Null(FileSystemService.GetTempSourceName($".{Guid.NewGuid():N}.k35tmp")); // 元の名前がない

        var created = new FileSystemService().CreateTempPath(@"C:\x\book.zip");
        Assert.Equal("book.zip", FileSystemService.GetTempSourceName(created));
    }

    [Fact]
    public async Task スキャンは_元のファイルが残っている一時ファイルを削除し_元がないものは残して知らせる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        ctx.CreateZip(@"sub\b.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        var libraryId = await ctx.RegisterAndScanAsync();

        // 元が残っている（更新の作りかけ）: 削除する
        var garbage = ctx.PathOf(TempNameFor("a.zip"));
        File.WriteAllText(garbage, "作りかけ");
        MakeOld(garbage);
        var garbageInSub = ctx.PathOf(@"sub\" + TempNameFor("b.zip"));
        File.WriteAllText(garbageInSub, "作りかけ");
        MakeOld(garbageInSub);

        // 元がない（置き換えの途中で止まった更新後のファイル）: 残して知らせる
        var orphan = ctx.PathOf(TempNameFor("gone.zip"));
        File.WriteAllText(orphan, "更新後の内容");
        MakeOld(orphan);

        // 新しいもの（実行中の可能性）と、アプリの形式ではないもの: 触れない
        var recent = ctx.PathOf(TempNameFor("a.zip"));
        File.WriteAllText(recent, "実行中");
        var foreign = ctx.PathOf("other.k35tmp");
        File.WriteAllText(foreign, "他のもの");
        MakeOld(foreign);
        ctx.Notifications.Items.Clear();

        var summary = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        Assert.False(File.Exists(garbage));
        Assert.False(File.Exists(garbageInSub));
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
        Assert.Equal(0, summary.Added); // 一時ファイルは、アーカイブとして登録しない
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        var warning = Assert.Single(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning);
        Assert.Contains(orphan, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 使用中の一時ファイルは_削除できなくてもスキャンを続ける()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        var libraryId = await ctx.RegisterAndScanAsync();
        var busy = ctx.PathOf(TempNameFor("a.zip"));
        File.WriteAllText(busy, "使用中");
        MakeOld(busy);

        ScanSummary summary;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            summary = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);
        }

        Assert.Equal(OperationStatus.Success, summary.Status);
        Assert.True(File.Exists(busy));

        // 使われなくなれば、次のスキャンで削除される
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);
        Assert.False(File.Exists(busy));
    }

    // ---- 起動時の後始末 ----

    [Fact]
    public void 起動時の後始末は_サムネイルとアプリの一時フォルダの古い書きかけだけを削除する()
    {
        var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "maint-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        try
        {
            var cache = new K35Viewer.Infrastructure.Thumbnails.ThumbnailCacheStore(paths.ThumbnailsDirectory);
            var saved = cache.Save(12, [1, 2, 3]); // 正式なサムネイル
            var leftover = cache.Resolve(saved) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(leftover, [9]);
            MakeOld(leftover);
            var fresh = cache.Resolve(saved) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(fresh, [9]);
            var oldTemp = Path.Combine(paths.TempDirectory, "work.bin");
            File.WriteAllBytes(oldTemp, [1]);
            MakeOld(oldTemp);
            MakeOld(cache.Resolve(saved));

            var deleted = new StartupMaintenance(cache, paths, NullLogger<StartupMaintenance>.Instance).Run();

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(leftover));
            Assert.False(File.Exists(oldTemp));
            Assert.True(File.Exists(fresh)); // 書き込み中の可能性があるものには触れない
            Assert.True(cache.Exists(saved)); // サムネイル本体は削除しない
        }
        finally
        {
            TestFileOperations.DeleteDirectory(root);
        }
    }

    [Fact]
    public void 起動時の後始末は_フォルダがなくても失敗しない()
    {
        var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "maint-none-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);

        var deleted = new StartupMaintenance(
            new K35Viewer.Infrastructure.Thumbnails.ThumbnailCacheStore(paths.ThumbnailsDirectory),
            paths,
            NullLogger<StartupMaintenance>.Instance).Run();

        Assert.Equal(0, deleted);
        Assert.False(Directory.Exists(root));
    }

    // ---- 想定外の例外 ----

    [Fact]
    public void 画面のスレッドの想定外の例外は_ログに記録し_ログの場所を知らせる_続けて起きても知らせるのは1回()
    {
        var logger = new CollectingLogger<UnhandledExceptionReporter>();
        var shown = new List<string>();
        var reporter = new UnhandledExceptionReporter(logger, @"C:\logs") { ShowMessage = shown.Add };
        var first = new InvalidOperationException("想定外その1");

        reporter.HandleUiException(first);
        reporter.HandleUiException(new InvalidOperationException("想定外その2"));
        reporter.HandleUnobservedTaskException(new AggregateException(new IOException("タスク")));

        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(LogLevel.Error, logger.Entries[0].Level);
        Assert.Same(first, logger.Entries[0].Exception); // スタックトレースはログに残す
        var message = Assert.Single(shown);
        Assert.Contains(@"C:\logs", message, StringComparison.Ordinal);
        Assert.DoesNotContain("想定外その1", message, StringComparison.Ordinal); // 例外の詳細は利用者に見せない
    }

    [Fact]
    public void 画面のスレッドで処理されなかった例外は_処理済みにされ_アプリは続く()
    {
        var logger = new CollectingLogger<UnhandledExceptionReporter>();
        var shown = new List<string>();
        var survived = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var reporter = new UnhandledExceptionReporter(logger, "logs") { ShowMessage = shown.Add };
            reporter.Attach(dispatcher);
            try
            {
                _ = dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("画面の処理の不具合")));
                WpfTestHelper.Pump();
                _ = dispatcher.BeginInvoke(() => survived = true); // 例外のあとも、画面の処理が続く
                WpfTestHelper.Pump();
            }
            finally
            {
                reporter.Detach(dispatcher);
            }
        });

        Assert.True(survived);
        Assert.Contains(logger.Entries, e => e.Exception is InvalidOperationException { Message: "画面の処理の不具合" });
        Assert.Single(shown);
    }

    // ---- 接続の切断（NAS）・容量不足 ----

    [Fact]
    public async Task 閲覧中にネットワークが切断されても_読み込めない頁はエラーになり_接続が戻れば次の頁は読める()
    {
        var archive = new DisconnectableArchive(
            [("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)), ("3.jpg", TestImages.Jpeg(120, 80))]);
        using var session = new ViewerSession(1, "a.zip", @"\\nas\share\a.zip", archive, 0);
        using var loader = new PageImageLoader(session, new K35Viewer.Infrastructure.Viewer.ViewerImageDecoder());
        Assert.NotNull((await loader.GetAsync(0)).Image);

        archive.Connected = false;
        var failed = await loader.GetAsync(1);

        Assert.Null(failed.Image);
        Assert.Contains("ネットワーク", failed.Error, StringComparison.Ordinal); // 例外ではなく、頁の読み込み失敗として扱う

        archive.Connected = true;
        Assert.NotNull((await loader.GetAsync(2)).Image);
    }

    [Fact]
    public async Task 管理フォルダに接続できない間のスキャンは_配下のアーカイブを不在にせず_接続が戻れば元どおりになる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip(@"nas\a.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        var libraryId = await ctx.RegisterAndScanAsync("nas");
        var id = await ctx.ScalarAsync("SELECT id FROM archives");

        // 切断を模して、管理フォルダごと見えなくする
        TestFileOperations.MoveDirectory(ctx.PathOf("nas"), ctx.PathOf("nas-offline"));
        var offline = await ctx.Scan.ScanAllAsync(ScanType.Startup, Ct);

        Assert.Equal(OperationStatus.Failed, offline!.Status);
        Assert.Equal(0, offline.Missing);
        Assert.Equal("AVAILABLE", await ctx.StringAsync("SELECT availability_status FROM archives"));
        Assert.Equal("UNAVAILABLE", await ctx.StringAsync("SELECT availability_status FROM library_folders"));

        TestFileOperations.MoveDirectory(ctx.PathOf("nas-offline"), ctx.PathOf("nas"));
        var online = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        Assert.Equal(OperationStatus.Success, online.Status);
        Assert.Equal(id, await ctx.ScalarAsync("SELECT id FROM archives"));
        Assert.Equal("AVAILABLE", await ctx.StringAsync("SELECT availability_status FROM library_folders"));
    }

    [Fact]
    public async Task ディスクの空き容量が足りなければ_一時ZIPを作る前に更新を中止し_元のZIPを保持する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)));
        await ctx.RegisterAndScanAsync();
        var opened = await ctx.Viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct);
        using var session = opened.Session!;
        _ = session.SetPendingDelete([0], true);
        var request = session.CreateUpdateRequest()!;
        var original = File.ReadAllBytes(ctx.PathOf("a.zip"));
        var rebuilds = 0;
        var service = ctx.CreateArchiveService(
            new ScriptedArchiveWriter(new K35Viewer.Infrastructure.Archives.ArchiveWriter())
            {
                OnRebuild = (_, _, _, _) =>
                {
                    rebuilds++;
                    return null;
                },
            },
            freeSpace: _ => 1024); // 空き 1KB

        var result = await service.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal("DISK_FULL", result.ErrorCode);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(0, rebuilds);
        Assert.Equal(original, File.ReadAllBytes(ctx.PathOf("a.zip")));
        Assert.Contains("空き容量", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 一時ZIPの作成中に容量が足りなくなっても_書きかけを削除し_元のZIPを保持する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)));
        await ctx.RegisterAndScanAsync();
        var opened = await ctx.Viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct);
        using var session = opened.Session!;
        _ = session.SetPendingDelete([0], true);
        var original = File.ReadAllBytes(ctx.PathOf("a.zip"));
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new K35Viewer.Infrastructure.Archives.ArchiveWriter())
        {
            OnRebuild = (_, destination, _, _) =>
            {
                File.WriteAllText(destination, "途中まで");
                // 実際のディスク容量不足と同じ HRESULT の例外を、書き込み処理の分類に通す
                var code = K35Viewer.Infrastructure.Archives.ArchiveWriter.ClassifyIOException(
                    new IOException("ディスクがいっぱいです", unchecked((int)0x80070070)));
                return K35Viewer.Infrastructure.Archives.ArchiveWriteResult.Failure(code, "ディスクがいっぱいです");
            },
        });

        var result = await service.ApplyDeletionsAsync(session.CreateUpdateRequest()!, ct: Ct);

        Assert.Equal("DISK_FULL", result.ErrorCode);
        Assert.Contains("空き容量が不足", result.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(ctx.PathOf("a.zip")));
        Assert.Empty(Directory.GetFiles(ctx.Workspace, "*" + FileSystemService.TempFileSuffix));
    }

    /// <summary>
    /// 開いたままのアーカイブで、読み出しを途中から失敗させる（ネットワークの切断を模す）。
    /// 切断されたネットワーク上のファイルの読み出しは、IOException になる。
    /// </summary>
    private sealed class DisconnectableArchive(IReadOnlyList<(string Name, byte[] Data)> entries) : K35Viewer.Infrastructure.Archives.IOpenedArchive
    {
        public bool Connected { get; set; } = true;

        public IReadOnlyList<K35Viewer.Infrastructure.Archives.ArchiveImageEntry> Images { get; } =
            [.. entries.Select(e => new K35Viewer.Infrastructure.Archives.ArchiveImageEntry(e.Name, e.Data.Length))];

        public byte[] ReadEntry(string entryName, long maxBytes, CancellationToken ct = default) =>
            Connected
                ? entries.First(e => e.Name == entryName).Data
                : throw new IOException("指定されたネットワーク名は利用できません。");

        public void Dispose()
        {
        }
    }
}
