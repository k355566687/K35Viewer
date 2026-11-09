using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

public class ThumbnailServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ArchiveSummary> SingleAsync(LibraryTestContext ctx, string search)
    {
        var result = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(SearchText: search), Ct);
        return Assert.Single(result.Items);
    }

    [Fact]
    public async Task ProcessPendingAsync_画像枚数と表紙を取得し_自然順の先頭をサムネイルにする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("img10.jpg", TestImages.Jpeg(400, 200)), // 自然順では最後
            ("img2.jpg", TestImages.Jpeg(1200, 800)), // 先頭(表紙)
            ("img3.png", TestImages.Png(50, 50)),
            ("notes.txt", [1, 2, 3]));
        await ctx.RegisterAndScanAsync();

        var summary = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(new ThumbnailRunSummary(1, 1, 0), summary);
        var archive = await SingleAsync(ctx, "book");
        Assert.Equal(3, archive.ImageCount);
        Assert.Equal("READY", archive.AnalysisStatus);
        Assert.Equal("READY", archive.ThumbnailStatus);
        Assert.NotNull(archive.ThumbnailPath);
        Assert.True(ctx.Cache.Exists(archive.ThumbnailPath));
        Assert.Equal((300, 200), TestImages.SizeOf(ctx.Cache.Read(archive.ThumbnailPath))); // img2.jpg(1200x800)から作られた
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM archives WHERE first_image_entry = 'img2.jpg' AND error_code IS NULL"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY' AND format = 'jpg' AND width = 300 AND height = 200 " +
            "AND source_entry = 'img2.jpg' AND cache_key = 'archive-' || archive_id"));
    }

    [Fact]
    public async Task ProcessPendingAsync_サムネイルは_ArchiveID基準のパスに保存される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(ThumbnailCacheStore.GetRelativePath(id), (await SingleAsync(ctx, "a.zip")).ThumbnailPath);
    }

    [Fact]
    public async Task ProcessPendingAsync_画像のないアーカイブは_Emptyとして識別できる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("docs.zip", ("a.txt", [1]));
        await ctx.RegisterAndScanAsync();

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        var archive = await SingleAsync(ctx, "docs");
        Assert.Equal("EMPTY", archive.AnalysisStatus);
        Assert.Equal(0, archive.ImageCount);
        Assert.Null(archive.ThumbnailPath);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache"));
    }

    [Fact]
    public async Task ProcessPendingAsync_壊れたZIPは_Brokenとして記録し_他のアーカイブの処理は続ける()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("broken.zip", size: 100);
        ctx.CreateFile("empty.zip", size: 0);
        ctx.CreateZip("good.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        await ctx.RegisterAndScanAsync();

        var summary = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(3, summary!.Processed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM archives WHERE analysis_status = 'BROKEN' AND error_code = 'ARCHIVE_BROKEN' AND image_count IS NULL"));
        Assert.Equal("READY", (await SingleAsync(ctx, "good")).ThumbnailStatus);
    }

    [Fact]
    public async Task ProcessPendingAsync_暗号化されたアーカイブは_Unsupportedにする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        TestZips.CreateEncryptedLooking(ctx.PathOf("secret.zip"), ("1.jpg", TestImages.Jpeg(100, 100)));
        await ctx.RegisterAndScanAsync();

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal("UNSUPPORTED", (await SingleAsync(ctx, "secret")).AnalysisStatus);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE error_code = 'ARCHIVE_ENCRYPTED'"));
    }

    [Fact]
    public async Task ProcessPendingAsync_表紙が壊れていれば_次の画像をサムネイルにする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("1.jpg", TestImages.Corrupt()),
            ("2.jpg", TestImages.Jpeg(600, 300)),
            ("3.jpg", TestImages.Jpeg(100, 100)));
        await ctx.RegisterAndScanAsync();

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        var archive = await SingleAsync(ctx, "book");
        Assert.Equal("READY", archive.ThumbnailStatus);
        Assert.Equal(3, archive.ImageCount);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE source_entry = '2.jpg'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE first_image_entry = '1.jpg'"));
    }

    [Fact]
    public async Task ProcessPendingAsync_表紙候補がすべて壊れていれば_サムネイルだけ失敗とし_再試行しない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("bad.zip", ("1.jpg", TestImages.Corrupt()), ("2.png", TestImages.Corrupt()));
        await ctx.RegisterAndScanAsync();

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        var archive = await SingleAsync(ctx, "bad");
        Assert.Equal("READY", archive.AnalysisStatus); // ZIPとしては正常で、画像枚数は分かる
        Assert.Equal(2, archive.ImageCount);
        Assert.Equal("FAILED", archive.ThumbnailStatus);
        Assert.Null(archive.ThumbnailPath);
        var calls = ctx.Reader.TotalCalls;

        var second = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(0, second!.Total); // 変更されるまで再試行しない
        Assert.Equal(calls, ctx.Reader.TotalCalls);
    }

    [Fact]
    public async Task ProcessPendingAsync_透過PNGの表紙も白背景のサムネイルにできる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("png.zip", ("cover.png", TestImages.PngHalfTransparent(600, 400)));
        await ctx.RegisterAndScanAsync();

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        var archive = await SingleAsync(ctx, "png");
        var (red, green, blue) = TestImages.PixelAt(ctx.Cache.Read(archive.ThumbnailPath!), 10, 10);
        Assert.InRange(red, 240, 255);
        Assert.InRange(green, 240, 255);
        Assert.InRange(blue, 240, 255);
    }

    [Fact]
    public async Task ProcessPendingAsync_ファイルが見つからなければ_Errorとし_同じ起動中は再試行せず_次の起動で再試行する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var path = ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        await ctx.RegisterAndScanAsync();
        var backup = path + ".bak";
        File.Move(path, backup); // 一時的な切断を模す(再スキャンはしない)

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM archives WHERE analysis_status = 'ERROR' AND error_code = 'FILE_NOT_FOUND'"));

        File.Move(backup, path);
        var sameSession = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        Assert.Equal(0, sameSession!.Total); // 同じ起動中は再試行しない

        var nextSession = await ctx.CreateThumbnailService().ProcessPendingAsync(ct: Ct);
        Assert.Equal(1, nextSession!.Total); // 次の起動では再試行する
        Assert.Equal("READY", (await SingleAsync(ctx, "a.zip")).AnalysisStatus);
    }

    [Fact]
    public async Task ProcessAsync_スキャン前に外部で変更されたファイルは_処理せず見送る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 100)), ("2.jpg", TestImages.Jpeg(100, 100))); // サイズが変わる

        var result = await ctx.Thumbnails.ProcessAsync(id, Ct);

        Assert.Null(result);
        Assert.Equal("PENDING", (await SingleAsync(ctx, "a.zip")).AnalysisStatus);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache"));
    }

    [Fact]
    public async Task ProcessPendingAsync_アーカイブが変更されると再スキャンで再生成待ちになり_古い画像を残したまま作り直す()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(600, 300)));
        var libraryId = await ctx.RegisterAndScanAsync();
        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var before = await SingleAsync(ctx, "a.zip");
        var oldBytes = ctx.Cache.Read(before.ThumbnailPath!);
        Assert.Equal((300, 150), TestImages.SizeOf(oldBytes));

        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(300, 600)), ("2.jpg", TestImages.Jpeg(10, 10))); // 内容を差し替え
        await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        var stale = await SingleAsync(ctx, "a.zip");
        Assert.Equal("STALE", stale.ThumbnailStatus);
        Assert.Equal(before.ThumbnailPath, stale.ThumbnailPath); // 再生成が終わるまでは古い画像を表示し続ける
        Assert.Equal("PENDING", stale.AnalysisStatus);

        var summary = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(1, summary!.Total);
        var after = await SingleAsync(ctx, "a.zip");
        Assert.Equal("READY", after.ThumbnailStatus);
        Assert.Equal(2, after.ImageCount);
        Assert.Equal((150, 300), TestImages.SizeOf(ctx.Cache.Read(after.ThumbnailPath!)));
    }

    [Fact]
    public async Task ProcessPendingAsync_キャッシュのファイルが失われていれば_再生成する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(400, 400)));
        await ctx.RegisterAndScanAsync();
        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var archive = await SingleAsync(ctx, "a.zip");
        ctx.Cache.Delete(archive.ThumbnailPath!);
        Assert.False(ctx.Cache.Exists(archive.ThumbnailPath!));

        var summary = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(1, summary!.Total);
        Assert.True(ctx.Cache.Exists(archive.ThumbnailPath!));
        Assert.Equal("READY", (await SingleAsync(ctx, "a.zip")).ThumbnailStatus);
    }

    [Fact]
    public async Task 生成済みのライブラリは_一覧表示でも再起動後の確認でも_ZIPを開かない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 10; i++)
        {
            ctx.CreateZip($"book{i:D2}.zip", ("1.jpg", TestImages.Jpeg(300, 400)), ("2.jpg", TestImages.Jpeg(300, 400)));
        }

        await ctx.RegisterAndScanAsync();
        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        Assert.Equal(10, ctx.Reader.AnalyzeCalls); // 生成のために1回ずつ開いた
        var opened = ctx.Reader.TotalCalls;

        // 一覧の取得、並べ替え、検索、サムネイル画像の読み込み
        var all = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(), Ct);
        _ = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(SearchText: "book0", SortKey: ArchiveSortKey.FileSize), Ct);
        Assert.All(all.Items, item =>
        {
            Assert.NotNull(item.ThumbnailPath);
            Assert.Equal((225, 300), TestImages.SizeOf(ctx.Cache.Read(item.ThumbnailPath)));
        });

        // 再起動後の確認(生成済みのものは対象外)
        var restarted = await ctx.CreateThumbnailService().ProcessPendingAsync(ct: Ct);

        Assert.Equal(0, restarted!.Total);
        Assert.Equal(opened, ctx.Reader.TotalCalls);
    }

    [Fact]
    public async Task ProcessPendingAsync_処理の都度イベントを通知し_進捗を報告する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 6; i++)
        {
            ctx.CreateZip($"b{i}.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        }

        ctx.CreateFile("broken.zip", size: 10);
        await ctx.RegisterAndScanAsync();
        var events = new System.Collections.Concurrent.ConcurrentBag<ArchiveProcessed>();
        ctx.Thumbnails.ArchiveProcessed += (_, e) => events.Add(e);
        var reports = new System.Collections.Concurrent.ConcurrentBag<ThumbnailProgress>();

        await ctx.Thumbnails.ProcessPendingAsync(new SynchronousProgress<ThumbnailProgress>(reports.Add), Ct);

        Assert.Equal(7, events.Count);
        Assert.Equal(6, events.Count(e => e.AnalysisStatus == "READY" && e.ThumbnailPath is not null && e.ImageCount == 1));
        Assert.Equal(1, events.Count(e => e.AnalysisStatus == "BROKEN" && e.ThumbnailPath is null && e.ImageCount is null));
        Assert.Contains(new ThumbnailProgress(0, 7), reports);
        Assert.Contains(new ThumbnailProgress(7, 7), reports);
    }

    [Fact]
    public async Task ProcessPendingAsync_同時に2回呼ばれた場合は_片方は何もしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 20; i++)
        {
            ctx.CreateZip($"b{i:D2}.zip", ("1.jpg", TestImages.Jpeg(200, 200)));
        }

        await ctx.RegisterAndScanAsync();

        var results = await Task.WhenAll(
            ctx.Thumbnails.ProcessPendingAsync(ct: Ct),
            ctx.Thumbnails.ProcessPendingAsync(ct: Ct));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(20, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.Equal(20, ctx.Reader.AnalyzeCalls); // 二重に処理していない
    }

    [Fact]
    public async Task ProcessPendingAsync_キャンセルすると処理を中断し_処理済みの結果は残る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 30; i++)
        {
            ctx.CreateZip($"b{i:D2}.zip", ("1.jpg", TestImages.Jpeg(800, 800)));
        }

        await ctx.RegisterAndScanAsync();
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<ThumbnailProgress>(p =>
        {
            if (p.Completed >= 3)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctx.Thumbnails.ProcessPendingAsync(progress, cts.Token));

        var done = await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'");
        Assert.InRange(done, 3, 29);
        Assert.False(ctx.Thumbnails.IsRunning);

        // 続きから再開できる
        var resumed = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        Assert.Equal(30 - done, resumed!.Total);
        Assert.Equal(30, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
