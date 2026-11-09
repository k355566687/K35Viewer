using System.Windows.Media.Imaging;
using K35Viewer.Infrastructure.Thumbnails;

namespace K35Viewer.Tests.Infrastructure;

public class ThumbnailImageLoaderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "loader-" + Guid.NewGuid().ToString("N"));
    private readonly ThumbnailCacheStore _store;

    public ThumbnailImageLoaderTests() => _store = new ThumbnailCacheStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Save(long archiveId, int width = 300, int height = 200) =>
        _store.Save(archiveId, TestImages.Jpeg(width, height));

    /// <summary>読み込み完了を待って画像を返す(すでにメモリ上にあればすぐ返す)。</summary>
    private async Task<System.Windows.Media.ImageSource> LoadAsync(ThumbnailImageLoader loader, string path)
    {
        var loaded = new TaskCompletionSource();
        var image = loader.TryGet(path, () => loaded.TrySetResult());
        if (image is not null)
        {
            return image;
        }

        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        return loader.TryGet(path, () => { }) ?? throw new InvalidOperationException("読み込み完了の通知後に画像がありません");
    }

    [Fact]
    public async Task TryGet_最初はnullを返し_背景で読み込んで通知し_次回から画像を返す()
    {
        using var loader = new ThumbnailImageLoader(_store);
        var path = Save(1, 300, 200);
        var loaded = new TaskCompletionSource();

        var first = loader.TryGet(path, () => loaded.TrySetResult());
        Assert.Null(first);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var image = loader.TryGet(path, () => { });
        var bitmap = Assert.IsType<BitmapImage>(image);
        Assert.Equal((300, 200), (bitmap.PixelWidth, bitmap.PixelHeight));
        Assert.True(bitmap.IsFrozen); // 背景スレッドで作った画像をUIスレッドで使えるよう凍結している
        Assert.Same(image, loader.TryGet(path, () => { })); // メモリ上のものをそのまま返す
    }

    [Fact]
    public async Task TryGet_同じ画像を複数が要求しても_読み込みは1回で_全員に通知する()
    {
        using var loader = new ThumbnailImageLoader(_store);
        var path = Save(1);
        var notified = 0;
        var all = new TaskCompletionSource();

        for (var i = 0; i < 3; i++)
        {
            loader.TryGet(path, () =>
            {
                if (Interlocked.Increment(ref notified) == 3)
                {
                    all.TrySetResult();
                }
            });
        }

        await all.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, loader.CachedCount);
    }

    [Fact]
    public async Task メモリ上の保持枚数には上限があり_最近使っていないものから破棄する()
    {
        using var loader = new ThumbnailImageLoader(_store, capacity: 2);
        var p1 = Save(1);
        var p2 = Save(2);
        var p3 = Save(3);

        await LoadAsync(loader, p1);
        await LoadAsync(loader, p2);
        Assert.NotNull(loader.TryGet(p1, () => { })); // p1 を使った(p2 が最も古くなる)
        await LoadAsync(loader, p3);

        Assert.Equal(2, loader.CachedCount);
        Assert.NotNull(loader.TryGet(p1, () => { }));
        Assert.NotNull(loader.TryGet(p3, () => { }));

        // 破棄されたので、改めて読み込みが始まる。テスト終了時の後始末(フォルダ削除)と重ならないよう、完了を待つ
        var reloaded = new TaskCompletionSource();
        Assert.Null(loader.TryGet(p2, () => reloaded.TrySetResult()));
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task 大量に要求しても_保持枚数は上限を超えず_すべて通知される()
    {
        using var loader = new ThumbnailImageLoader(_store, capacity: 10);
        var paths = Enumerable.Range(1, 60).Select(i => Save(i, 40, 40)).ToList();
        var remaining = paths.Count;
        var all = new TaskCompletionSource();

        foreach (var path in paths)
        {
            loader.TryGet(path, () =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    all.TrySetResult();
                }
            });
        }

        await all.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.InRange(loader.CachedCount, 1, 10);
    }

    [Fact]
    public async Task 読み込めないファイルは通知せず_無効化するまで再試行しない()
    {
        using var loader = new ThumbnailImageLoader(_store);
        var path = ThumbnailCacheStore.GetRelativePath(99);
        var notified = 0;

        Assert.Null(loader.TryGet(path, () => Interlocked.Increment(ref notified)));
        await Task.Delay(300, Ct);
        Assert.Equal(0, notified);
        Assert.Equal(0, loader.CachedCount);

        // 後からファイルができても、無効化するまでは読み直さない
        _store.Save(99, TestImages.Jpeg(100, 100));
        Assert.Null(loader.TryGet(path, () => Interlocked.Increment(ref notified)));
        await Task.Delay(200, Ct);
        Assert.Equal(0, notified);

        loader.Invalidate(path);
        var image = await LoadAsync(loader, path);
        Assert.Equal(100, ((BitmapImage)image).PixelWidth);
    }

    [Fact]
    public async Task Invalidate_再生成されて中身が変わったファイルを読み直す()
    {
        using var loader = new ThumbnailImageLoader(_store);
        var path = Save(1, 300, 200);
        var before = (BitmapImage)await LoadAsync(loader, path);
        Assert.Equal(300, before.PixelWidth);

        _store.Save(1, TestImages.Jpeg(120, 160)); // 同じパスに別の内容(再生成)
        Assert.Same(before, loader.TryGet(path, () => { })); // 無効化するまでは古いまま

        loader.Invalidate(path);
        var after = (BitmapImage)await LoadAsync(loader, path);

        Assert.Equal((120, 160), (after.PixelWidth, after.PixelHeight));
    }
}
