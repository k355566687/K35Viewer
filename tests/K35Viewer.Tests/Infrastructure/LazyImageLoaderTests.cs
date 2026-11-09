using System.Collections.Concurrent;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Services;

namespace K35Viewer.Tests.Infrastructure;

public class LazyImageLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 読み込めなかった画像は_失敗の通知を受け取り_無効化するまで再試行しない()
    {
        var calls = 0;
        var failing = true;
        using var loader = new LazyImageLoader<int>(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return failing ? throw new InvalidDataException("壊れている") : TestImages.Jpeg(20, 20);
            },
            capacity: 4,
            workerCount: 1);
        var failed = new TaskCompletionSource();
        var loaded = new TaskCompletionSource();

        Assert.Null(loader.TryGet(7, () => loaded.TrySetResult(), () => failed.TrySetResult()));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(loader.IsFailed(7));
        Assert.False(loaded.Task.IsCompleted);
        Assert.Null(loader.TryGet(7, () => { }, () => { }));
        await Task.Delay(100, Ct);
        Assert.Equal(1, calls); // 再試行していない

        failing = false;
        loader.Invalidate(7);
        Assert.False(loader.IsFailed(7));
        Assert.Null(loader.TryGet(7, () => loaded.TrySetResult(), () => failed.TrySetResult()));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(loader.TryGet(7, () => { }));
    }

    [Fact]
    public async Task 最後に要求した画像から先に読み込む()
    {
        var order = new ConcurrentQueue<int>();
        using var gate = new ManualResetEventSlim(false);
        using var loader = new LazyImageLoader<int>(
            key =>
            {
                order.Enqueue(key);
                if (key == 0)
                {
                    gate.Wait(Ct); // 最初の要求を止めておき、その間に残りを要求する
                }

                return TestImages.Jpeg(10, 10);
            },
            capacity: 10,
            workerCount: 1);
        var remaining = 4;
        var done = new TaskCompletionSource();
        void OnLoaded()
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                done.TrySetResult();
            }
        }

        loader.TryGet(0, OnLoaded);
        await WaitUntilAsync(() => order.Contains(0)); // 0 の読み込みが始まってから
        loader.TryGet(1, OnLoaded);
        loader.TryGet(2, OnLoaded);
        loader.TryGet(3, OnLoaded);
        gate.Set();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // 画面に最後に出た（新しい）画像ほど優先される
        Assert.Equal([0, 3, 2, 1], order.ToArray());
    }

    [Fact]
    public async Task 再び要求された画像は_優先して読み込む()
    {
        var order = new ConcurrentQueue<int>();
        using var gate = new ManualResetEventSlim(false);
        using var loader = new LazyImageLoader<int>(
            key =>
            {
                order.Enqueue(key);
                if (key == 0)
                {
                    gate.Wait(Ct);
                }

                return TestImages.Jpeg(10, 10);
            },
            capacity: 10,
            workerCount: 1);
        var remaining = 4;
        var done = new TaskCompletionSource();
        void OnLoaded()
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                done.TrySetResult();
            }
        }

        loader.TryGet(0, OnLoaded);
        await WaitUntilAsync(() => order.Contains(0));
        loader.TryGet(1, () => { }); // 1, 2, 3 の順に要求したあと、1 を再び要求する
        loader.TryGet(2, OnLoaded);
        loader.TryGet(3, OnLoaded);
        loader.TryGet(1, OnLoaded);
        gate.Set();
        await WaitUntilAsync(() => order.Count == 4);

        Assert.Equal([0, 1, 3, 2], order.ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(5, Ct);
        }
    }
}

public class PageThumbnailSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ViewerSession> OpenAsync(LibraryTestContext ctx, params (string Name, byte[] Data)[] pages)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", pages);
        await ctx.RegisterAndScanAsync();
        return (await ctx.Viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct)).Session!;
    }

    private static (string, byte[])[] Pages(int count, int width = 600, int height = 800) =>
        [.. Enumerable.Range(1, count).Select(i => ($"{i:D3}.jpg", TestImages.Jpeg(width, height)))];

    [Fact]
    public async Task TryGet_画面に出た頁だけを生成し_ほかの頁のZIPは読まない()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenAsync(ctx, Pages(200));
        using var source = new PageThumbnailSource(session, new ThumbnailGenerator());
        var loaded = new TaskCompletionSource();

        // 画面に出た10枚だけを要求する
        var remaining = 10;
        for (var i = 50; i < 60; i++)
        {
            Assert.Null(source.TryGet(i, () =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    loaded.TrySetResult();
                }
            }));
        }

        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(10, source.GeneratedCount);
        Assert.Equal(10, ctx.Reader.OpenedReadCalls); // 200頁のうち、読んだのは10頁だけ
        var thumbnail = (System.Windows.Media.Imaging.BitmapSource)source.TryGet(55, () => { })!;
        Assert.Equal((225, 300), (thumbnail.PixelWidth, thumbnail.PixelHeight)); // 600x800 を長辺300pxに
        Assert.True(thumbnail.IsFrozen);
    }

    [Fact]
    public async Task TryGet_メモリ上から追い出された画像も_ZIPを読み直さずに作り直せる()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenAsync(ctx, Pages(6));
        using var source = new PageThumbnailSource(session, new ThumbnailGenerator(), capacity: 2);

        async Task LoadAsync(int index)
        {
            var loaded = new TaskCompletionSource();
            if (source.TryGet(index, () => loaded.TrySetResult()) is null)
            {
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            }
        }

        foreach (var index in new[] { 0, 1, 2, 3 })
        {
            await LoadAsync(index);
        }

        Assert.Equal(2, source.CachedCount); // デコード済みの画像は2枚まで
        Assert.Equal(4, ctx.Reader.OpenedReadCalls);

        await LoadAsync(0); // 追い出されていた 0 を、もう一度表示する

        Assert.Equal(4, ctx.Reader.OpenedReadCalls); // ZIPは読んでいない（生成済みのJPEGから作り直した）
        Assert.Equal(4, source.GeneratedCount);
    }

    [Fact]
    public async Task TryGet_壊れた画像は_失敗として通知する()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenAsync(
            ctx, ("1.jpg", TestImages.Jpeg(50, 50)), ("2.jpg", TestImages.Corrupt()), ("3.jpg", TestImages.Jpeg(50, 50)));
        using var source = new PageThumbnailSource(session, new ThumbnailGenerator());
        var failed = new TaskCompletionSource();

        Assert.Null(source.TryGet(1, () => { }, () => failed.TrySetResult()));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(source.IsFailed(1));
        Assert.False(source.IsFailed(0));
        Assert.Equal(0, source.GeneratedCount);
    }

    [Fact]
    public async Task アーカイブを閉じたあとの要求は_失敗になり_例外で落ちない()
    {
        using var ctx = new LibraryTestContext();
        var session = await OpenAsync(ctx, Pages(3));
        using var source = new PageThumbnailSource(session, new ThumbnailGenerator());
        session.Dispose();
        var failed = new TaskCompletionSource();

        Assert.Null(source.TryGet(0, () => { }, () => failed.TrySetResult()));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(source.IsFailed(0));
    }

    [Fact]
    public async Task ViewerSession_ReadImage_同期で読み出し_範囲外と閉じたあとは例外にする()
    {
        using var ctx = new LibraryTestContext();
        var session = await OpenAsync(ctx, ("1.jpg", TestImages.Jpeg(30, 20)), ("2.jpg", TestImages.Jpeg(40, 20)));

        Assert.Equal((40, 20), TestImages.SizeOf(session.ReadImage(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadImage(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadImage(-1));

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.ReadImage(0));
    }
}
