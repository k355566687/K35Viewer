using System.Diagnostics;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

/// <summary>
/// サムネイル生成の処理速度と、生成済みのライブラリの起動時確認の速度。
/// 時間はテスト出力へ記録し、極端な劣化だけを緩い上限で検知する。
/// </summary>
public class ThumbnailPerformanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 画像入りZIPのサムネイル生成は_現実的な速度で完了する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        const int count = 120;
        var large = TestImages.Jpeg(1600, 1200); // 実際のページ画像に近い大きさ
        for (var i = 0; i < count; i++)
        {
            ctx.CreateZip($"book{i:D3}.zip", ("001.jpg", large), ("002.jpg", large), ("003.jpg", large));
        }

        await ctx.RegisterAndScanAsync();

        var stopwatch = Stopwatch.StartNew();
        var summary = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var elapsed = stopwatch.Elapsed;

        var perSecond = count / elapsed.TotalSeconds;
        TestContext.Current.SendDiagnosticMessage(
            $"サムネイル生成: {count}件(各3枚、1600x1200) {elapsed.TotalSeconds:F2}秒 / {perSecond:F1}件/秒 / " +
            $"1万件では約{10_000 / perSecond / 60:F1}分");
        Assert.Equal(count, summary!.Processed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(count, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.True(elapsed < TimeSpan.FromSeconds(60), $"生成が遅すぎます: {elapsed}");
    }

    [Fact]
    public async Task 生成済みの2000件は_起動時の確認でZIPを開かず短時間で終わる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        const int count = 2_000;
        var thumbnail = new ThumbnailGenerator().Generate(TestImages.Jpeg(300, 400)).Data;

        // 生成済みの状態(アーカイブ、サムネイルの記録、画像ファイル)を直接作る
        await ctx.ExecuteAsync("""
            INSERT INTO library_folders (id, path, normalized_path, created_at, updated_at) VALUES (1, 'C:\L', 'C:\L', 1, 1);
            INSERT INTO folders (id, library_folder_id, parent_folder_id, name, full_path, normalized_path, relative_path,
                                 depth, created_at, updated_at)
            VALUES (1, 1, NULL, 'L', 'C:\L', 'C:\L', '', 0, 1, 1);
            """);
        await ctx.ExecuteAsync($$"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {{count}})
            INSERT INTO archives (id, folder_id, file_name, extension, file_size, last_write_time, image_count,
                                  analysis_status, created_at, updated_at)
            SELECT i, 1, printf('book%05d.zip', i), 'zip', 1000 + i, 1700000000 + i, 10, 'READY', 1, 1 FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {{count}})
            INSERT INTO thumbnail_cache (archive_id, cache_key, relative_path, format, width, height, source_entry,
                                         source_archive_size, source_last_write_time, status, generated_at)
            SELECT i, 'archive-' || i, printf('%04d\archive-%d.jpg', i / 1000, i), 'jpg', 225, 300, '001.jpg',
                   1000 + i, 1700000000 + i, 'READY', 1 FROM n;
            """);
        for (var id = 1; id <= count; id++)
        {
            ctx.Cache.Save(id, thumbnail);
        }

        var stopwatch = Stopwatch.StartNew();
        var summary = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var elapsed = stopwatch.Elapsed;

        TestContext.Current.SendDiagnosticMessage(
            $"起動時の確認: 生成済み{count}件 {elapsed.TotalMilliseconds:F0}ms (ZIPを開いた回数 {ctx.Reader.TotalCalls})");
        Assert.Equal(0, summary!.Total);
        Assert.Equal(0, ctx.Reader.TotalCalls);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"起動時の確認が遅すぎます: {elapsed}");

        // 生成済みの一覧は、画像ファイルの場所を含めて取得できる
        var list = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(), Ct);
        Assert.Equal(count, list.TotalCount);
        Assert.All(list.Items, item => Assert.True(ctx.Cache.Exists(item.ThumbnailPath!)));
    }
}
