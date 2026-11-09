using System.Diagnostics;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>
/// ビューアの実用速度とメモリの確認。時間はテスト出力へ記録し、極端な劣化だけを緩い上限で検知する。
/// 「全画像を同時に展開しない」ことを、メモリ使用量の上限で確かめる。
/// </summary>
[Collection(MemoryMeasurementGroup.Name)]
public class ViewerPerformanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// プロセス全体のメモリ使用量（プライベートバイト）。WPFの画像のピクセルデータはマネージドヒープの外にあるため、
    /// GC.GetTotalMemory では測れない。
    /// </summary>
    private static double PrivateMemoryMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / 1024.0 / 1024.0;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task 大きなページ画像を連続して閲覧しても_速く切り替わり_メモリは増え続けない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();

        // 実際のコミックの1頁に近い大きさ（2400x3400、約816万画素）を、300頁ぶん
        const int pages = 300;
        var page = TestImages.JpegPattern(2400, 3400);
        ctx.CreateZip("big.zip", [.. Enumerable.Range(1, pages).Select(i => ($"{i:D4}.jpg", page))]);
        await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        var zipMb = new FileInfo(ctx.PathOf("big.zip")).Length / 1024.0 / 1024.0;

        // ZIPを開く（300エントリの一覧を自然順に並べる）
        var stopwatch = Stopwatch.StartNew();
        using (var probe = new ArchiveReader().Open(ctx.PathOf("big.zip"), Ct).Archive!)
        {
            Assert.Equal(pages, probe.Images.Count);
        }

        var openMs = stopwatch.ElapsedMilliseconds;

        using var viewer = new ViewerViewModel(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);
        viewer.UpdateViewport(1600, 900, 1.0);

        // 最初の頁を表示するまで（読み出し＋デコード）
        stopwatch.Restart();
        Assert.True(await viewer.OpenAsync(id, Ct));
        var firstPageMs = stopwatch.ElapsedMilliseconds;
        Assert.Equal(2400, viewer.CurrentImage!.PixelWidth);
        Assert.Equal(900.0 / 3400, viewer.EffectiveScale, 3); // 縦がウィンドウに収まる倍率

        // 先読みが済むのを待ち、頁めくりの速さを測る（読み進める場合）
        await Task.Delay(1500, Ct);
        var turnTimes = new List<long>();
        var before = PrivateMemoryMb();
        var peak = before;
        for (var i = 1; i <= 100; i++)
        {
            var previousImage = viewer.CurrentImage;
            var turn = Stopwatch.StartNew();
            viewer.NextCommand.Execute(null);
            await WaitUntilAsync(() => !ReferenceEquals(viewer.CurrentImage, previousImage));
            turnTimes.Add(turn.ElapsedMilliseconds);
            await Task.Delay(40, Ct); // 人がめくる程度の間隔（先読みが追いつく時間）
            if (i % 10 == 0)
            {
                peak = Math.Max(peak, PrivateMemoryMb());
            }
        }

        await Task.Delay(1000, Ct);
        var after = PrivateMemoryMb();
        peak = Math.Max(peak, after);
        var growthMb = after - before;
        var peakGrowthMb = peak - before;
        var sorted = turnTimes.Order().ToList();

        TestContext.Current.SendDiagnosticMessage(
            $"ビューア: ZIP {zipMb:F0}MB({pages}頁) / 開く(一覧) {openMs}ms / 最初の頁 {firstPageMs}ms / " +
            $"頁送り100回 中央値 {sorted[sorted.Count / 2]}ms・最大 {sorted[^1]}ms / " +
            $"メモリ(プライベートバイト) 開始時 {before:F0}MB → 100頁後の増加 {growthMb:F0}MB・途中の最大の増加 {peakGrowthMb:F0}MB" +
            $"（1頁のデコード後サイズは約{2400 * 3400 * 4 / 1024.0 / 1024.0:F0}MB）");

        Assert.Equal("101 / 300", viewer.PositionText);
        // 前後の数頁（現在位置の前1・後2）だけを保持する。100頁ぶんを保持していれば約3GBになる
        Assert.True(peakGrowthMb < 600, $"メモリが増え続けています: 最大 {peakGrowthMb:F0}MB");
        Assert.True(sorted[sorted.Count / 2] < 1000, $"頁送りが遅すぎます（中央値）: {sorted[sorted.Count / 2]}ms");
        Assert.True(firstPageMs < 10_000, $"最初の頁の表示が遅すぎます: {firstPageMs}ms");
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示は_大きなページ画像でも画面が埋まるまでが短く_全体を見て回ってもメモリは増え続けない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();

        const int pages = 150;
        var page = TestImages.JpegPattern(2400, 3400);
        ctx.CreateZip("big.zip", [.. Enumerable.Range(1, pages).Select(i => ($"{i:D4}.jpg", page))]);
        await ctx.RegisterAndScanAsync();
        using var viewer = new ViewerViewModel(
            ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);
        Assert.True(await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct));
        viewer.UpdateListViewport(ViewerViewModel.PageCellWidth * 6);
        viewer.ToggleViewModeCommand.Execute(null);

        // 画面に最初に出る分（6列×3行）のサムネイルが揃うまで
        var visible = viewer.Pages.Take(18).ToList();
        var stopwatch = Stopwatch.StartNew();
        await RequestAndWaitAsync(visible);
        var firstScreenMs = stopwatch.ElapsedMilliseconds;

        // 画面に出る分ずつ、全体を見て回る（スクロール）
        var before = PrivateMemoryMb();
        var peak = before;
        stopwatch.Restart();
        for (var start = 18; start < pages; start += 18)
        {
            await RequestAndWaitAsync(viewer.Pages.Skip(start).Take(18).ToList());
            peak = Math.Max(peak, PrivateMemoryMb());
        }

        var scrollMs = stopwatch.ElapsedMilliseconds;
        var after = PrivateMemoryMb();
        var growthMb = after - before;
        var peakGrowthMb = Math.Max(peak, after) - before;

        TestContext.Current.SendDiagnosticMessage(
            $"ビューア一覧: ZIP {pages}頁(2400x3400) / 最初の画面(18枚) {firstScreenMs}ms / 残りの{pages - 18}枚を18枚ずつ表示 {scrollMs}ms / " +
            $"メモリ(プライベートバイト) 増加 {growthMb:F0}MB・最大の増加 {peakGrowthMb:F0}MB");

        Assert.True(firstScreenMs < 20_000, $"最初の画面が埋まるまでが遅すぎます: {firstScreenMs}ms");
        // 全頁のサムネイルを表示しても、デコード済みの画像は上限枚数までしか保持しない
        Assert.True(peakGrowthMb < 600, $"メモリが増え続けています: 最大 {peakGrowthMb:F0}MB");
        await viewer.CloseAsync();
    }

    /// <summary>指定した画像のサムネイルを要求し、すべて表示できるようになるまで待つ。</summary>
    private static async Task RequestAndWaitAsync(List<PageItem> items)
    {
        var remaining = items.Count;
        var done = new TaskCompletionSource();
        foreach (var item in items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageItem.Thumbnail) && Interlocked.Decrement(ref remaining) == 0)
                {
                    done.TrySetResult();
                }
            };
            _ = item.Thumbnail; // 画面に出た（バインドされた）ことで、生成が始まる
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }
}
