using K35Viewer.Domain;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

public class ViewerServiceTests
{
    // ZIPに入れる順序は、自然順（1, 2, 3, 10, 12）とあえて異なる並びにする
    private static readonly int[] BookPageNumbers = [12, 3, 1, 10, 2];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>p1〜p12 のページを持つZIPを登録し、アーカイブIDを返す。</summary>
    private static async Task<long> RegisterBookAsync(LibraryTestContext ctx, string name = "book.zip")
    {
        var pages = BookPageNumbers
            .Select(n => ($"p{n}.jpg", TestImages.Jpeg(20 + n, 10 + n)))
            .ToArray();
        ctx.CreateZip(name, [.. pages, ("readme.txt", [1, 2, 3])]);
        await ctx.RegisterAndScanAsync();
        return await ctx.ScalarAsync($"SELECT id FROM archives WHERE file_name = '{name}'");
    }

    [Fact]
    public async Task OpenAsync_画像を自然順に並べ_先頭から開く()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);
        ctx.Notifications.Items.Clear(); // 登録時のスキャンの通知を除く

        var result = await ctx.Viewer.OpenAsync(id, Ct);

        Assert.True(result.Succeeded);
        using var session = result.Session!;
        Assert.Equal(id, session.ArchiveId);
        Assert.Equal("book.zip", session.ArchiveName);
        Assert.Equal(ctx.PathOf("book.zip"), session.ArchivePath);
        Assert.Equal(["p1.jpg", "p2.jpg", "p3.jpg", "p10.jpg", "p12.jpg"], session.Entries.Select(e => e.FullName));
        Assert.Equal(5, session.Count);
        Assert.Equal(0, session.CurrentIndex);
        Assert.Equal("p1.jpg", session.CurrentEntry.FullName);
        Assert.Empty(ctx.Notifications.Items);
    }

    [Fact]
    public async Task OpenAsync_前回の閲覧位置が有効ならそこから開き_無効なら先頭から開く()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);

        await ctx.ExecuteAsync($"UPDATE archives SET last_view_entry = 'p10.jpg', last_view_index = 3 WHERE id = {id}");
        using (var restored = (await ctx.Viewer.OpenAsync(id, Ct)).Session!)
        {
            Assert.Equal(3, restored.CurrentIndex);
            Assert.Equal("p10.jpg", restored.CurrentEntry.FullName);
        }

        // 画像エントリが存在しなければ、位置の番号が範囲内でも先頭から開く
        await ctx.ExecuteAsync($"UPDATE archives SET last_view_entry = 'deleted.jpg', last_view_index = 2 WHERE id = {id}");
        using (var invalid = (await ctx.Viewer.OpenAsync(id, Ct)).Session!)
        {
            Assert.Equal(0, invalid.CurrentIndex);
        }
    }

    [Fact]
    public async Task SaveLastViewAsync_位置を記録し_次に開くと復元される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);
        using (var first = (await ctx.Viewer.OpenAsync(id, Ct)).Session!)
        {
            first.MoveTo(2);
            await ctx.Viewer.SaveLastViewAsync(first, Ct);
        }

        Assert.Equal(1, await ctx.ScalarAsync(
            $"SELECT COUNT(*) FROM archives WHERE id = {id} AND last_view_entry = 'p3.jpg' AND last_view_index = 2 AND last_viewed_at IS NOT NULL"));
        using var second = (await ctx.Viewer.OpenAsync(id, Ct)).Session!;
        Assert.Equal(2, second.CurrentIndex);
    }

    [Fact]
    public async Task OpenAsync_存在しないアーカイブや不在のアーカイブは_開かずに通知する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);

        var unknown = await ctx.Viewer.OpenAsync(99999, Ct);
        Assert.False(unknown.Succeeded);
        Assert.Contains("再スキャン", ctx.Notifications.Items[^1].Message, StringComparison.Ordinal);

        await ctx.ExecuteAsync($"UPDATE archives SET availability_status = 'MISSING' WHERE id = {id}");
        Assert.False((await ctx.Viewer.OpenAsync(id, Ct)).Succeeded);
        Assert.Equal(NotificationLevel.Warning, ctx.Notifications.Items[^1].Level);
    }

    [Fact]
    public async Task OpenAsync_ファイルが消えていれば_再スキャンを促して開かない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);
        File.Delete(ctx.PathOf("book.zip"));

        var result = await ctx.Viewer.OpenAsync(id, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("book.zip", result.Message, StringComparison.Ordinal);
        Assert.Contains("再スキャン", result.Message, StringComparison.Ordinal);
        Assert.Equal(NotificationLevel.Warning, ctx.Notifications.Items[^1].Level);
        Assert.Equal(0, ctx.Reader.OpenCalls); // ZIPを開こうともしない
    }

    [Fact]
    public async Task OpenAsync_壊れたZIP_画像のないZIP_暗号化ZIPは_理由を通知して開かない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("broken.zip", size: 100);
        ctx.CreateZip("docs.zip", ("a.txt", [1]));
        TestZips.CreateEncryptedLooking(ctx.PathOf("secret.zip"), ("1.jpg", TestImages.Jpeg(10, 10)));
        await ctx.RegisterAndScanAsync();
        long IdOf(string name) => ctx.ScalarAsync($"SELECT id FROM archives WHERE file_name = '{name}'").GetAwaiter().GetResult();

        var broken = await ctx.Viewer.OpenAsync(IdOf("broken.zip"), Ct);
        var empty = await ctx.Viewer.OpenAsync(IdOf("docs.zip"), Ct);
        var secret = await ctx.Viewer.OpenAsync(IdOf("secret.zip"), Ct);

        Assert.All(new[] { broken, empty, secret }, r => Assert.False(r.Succeeded));
        Assert.Contains("破損", broken.Message, StringComparison.Ordinal);
        Assert.Contains("画像", empty.Message, StringComparison.Ordinal);
        Assert.Contains("暗号化", secret.Message, StringComparison.Ordinal);
        Assert.Equal(
            [NotificationLevel.Error, NotificationLevel.Warning, NotificationLevel.Warning],
            ctx.Notifications.Items.TakeLast(3).Select(n => n.Level));
    }

    [Fact]
    public async Task ViewerSession_Dispose後は_アーカイブのファイルを手放す()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);
        var session = (await ctx.Viewer.OpenAsync(id, Ct)).Session!;
        Assert.NotEmpty(await session.ReadImageAsync(0, Ct));

        session.Dispose();
        session.Dispose(); // 二重に閉じても問題ない

        File.Delete(ctx.PathOf("book.zip"));
        Assert.False(File.Exists(ctx.PathOf("book.zip")));
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => session.ReadImageAsync(0, Ct));
    }

    [Fact]
    public async Task ViewerSession_位置の移動は範囲内だけで_端では止まる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);
        using var session = (await ctx.Viewer.OpenAsync(id, Ct)).Session!;

        Assert.False(session.CanMovePrevious);
        Assert.False(session.MovePrevious()); // 先頭から戻らない
        Assert.True(session.MoveNext());
        Assert.Equal(1, session.CurrentIndex);
        Assert.True(session.MoveLast());
        Assert.Equal(4, session.CurrentIndex);
        Assert.False(session.CanMoveNext);
        Assert.False(session.MoveNext()); // 最後から進まない(先頭へ戻らない)
        Assert.Equal(4, session.CurrentIndex);
        Assert.False(session.MoveLast()); // すでに最後
        Assert.True(session.MoveFirst());
        Assert.False(session.MoveFirst());
        Assert.False(session.MoveTo(-1));
        Assert.False(session.MoveTo(5));
        Assert.Equal(0, session.CurrentIndex);
        Assert.True(session.MoveTo(3));
        Assert.True(session.MovePrevious());
        Assert.Equal(2, session.CurrentIndex);
    }

    [Fact]
    public async Task ViewerSession_ReadImageAsync_指定した頁の内容を返し_範囲外は例外にする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var id = await RegisterBookAsync(ctx);
        using var session = (await ctx.Viewer.OpenAsync(id, Ct)).Session!;

        var data = await session.ReadImageAsync(3, Ct); // p10.jpg

        Assert.Equal((30, 20), TestImages.SizeOf(data));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadImageAsync(5, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadImageAsync(-1, Ct));
    }
}

public class PageImageLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ViewerSession> OpenBookAsync(LibraryTestContext ctx, int pages)
    {
        await ctx.InitializeAsync();
        var entries = Enumerable.Range(1, pages)
            .Select(n => ($"{n:D3}.jpg", TestImages.Jpeg(100 + n, 50 + n)))
            .ToArray();
        ctx.CreateZip("book.zip", entries);
        await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        return (await ctx.Viewer.OpenAsync(id, Ct)).Session!;
    }

    [Fact]
    public async Task GetAsync_指定した頁を読み込んでデコードする()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenBookAsync(ctx, 5);
        using var loader = new PageImageLoader(session, new ViewerImageDecoder());

        var page = await loader.GetAsync(2);

        Assert.Null(page.Error);
        Assert.Equal(2, page.Index);
        Assert.Equal((103, 53), (page.Image!.PixelWidth, page.Image.PixelHeight));
        Assert.True(page.Image.IsFrozen); // 背景で作った画像を画面で使える
    }

    [Fact]
    public async Task GetAsync_同じ頁を同時に何度要求しても_読み込みは1回だけ()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenBookAsync(ctx, 5);
        using var loader = new PageImageLoader(session, new ViewerImageDecoder());

        var pages = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => loader.GetAsync(1)));
        _ = await loader.GetAsync(1);

        Assert.All(pages, p => Assert.Same(pages[0], p));
        Assert.Equal(1, ctx.Reader.OpenedReadCalls);
    }

    [Fact]
    public async Task Prefetch_現在の頁の前後だけを読み込み_それより遠い頁は破棄する()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenBookAsync(ctx, 20);
        using var loader = new PageImageLoader(session, new ViewerImageDecoder());

        loader.Prefetch(0);
        Assert.Equal(3, loader.CachedCount); // 0, 1, 2（先頭なので、前はない）
        _ = await Task.WhenAll(Enumerable.Range(0, 3).Select(loader.GetAsync)); // 読み込みが終わるのを待つ
        Assert.Equal(3, ctx.Reader.OpenedReadCalls);

        loader.Prefetch(10);
        Assert.Equal(4, loader.CachedCount); // 9, 10, 11, 12（0〜2は破棄）
        _ = await Task.WhenAll(Enumerable.Range(9, 4).Select(loader.GetAsync));
        Assert.Equal(7, ctx.Reader.OpenedReadCalls); // 0,1,2 と 9,10,11,12。13頁以降などは読んでいない

        loader.Prefetch(19);
        Assert.Equal(2, loader.CachedCount); // 18, 19（最後なので、後ろはない）
    }

    [Fact]
    public async Task Prefetch_戻る方向は直前の頁を保持し_戻っても読み直さない()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenBookAsync(ctx, 10);
        using var loader = new PageImageLoader(session, new ViewerImageDecoder());
        loader.Prefetch(5);
        _ = await Task.WhenAll(Enumerable.Range(4, 4).Select(loader.GetAsync));
        var reads = ctx.Reader.OpenedReadCalls;

        _ = await loader.GetAsync(4); // 1頁戻る

        Assert.Equal(reads, ctx.Reader.OpenedReadCalls);
    }

    [Fact]
    public async Task GetAsync_壊れた画像は_エラーとして返し_何度も読み直さない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(50, 50)), ("2.jpg", TestImages.Corrupt()), ("3.jpg", TestImages.Jpeg(60, 60)));
        await ctx.RegisterAndScanAsync();
        using var session = (await ctx.Viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct)).Session!;
        using var loader = new PageImageLoader(session, new ViewerImageDecoder());

        var broken = await loader.GetAsync(1);
        _ = await loader.GetAsync(1);
        var next = await loader.GetAsync(2);

        Assert.Null(broken.Image);
        Assert.False(string.IsNullOrEmpty(broken.Error));
        Assert.NotNull(next.Image); // 壊れた頁があっても、次の頁は読める
        Assert.Equal(2, ctx.Reader.OpenedReadCalls);
    }

    [Fact]
    public async Task Prefetch_遠くへ飛んだとき_追い出した頁の読み込みは中止され_読み込み機構は使い続けられる()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenBookAsync(ctx, 200);
        using var loader = new PageImageLoader(session, new ViewerImageDecoder());

        // 先頭付近の読み込みを始めた直後に、末尾へ飛ぶ（先頭付近は追い出される）
        loader.Prefetch(0);
        var abandoned = loader.GetAsync(0);
        loader.Prefetch(199);

        // 追い出された頁は、完了していれば結果、途中だったら中止（どちらでもよい。例外で他へ影響しないこと）
        var outcome = await Record.ExceptionAsync(() => abandoned);
        Assert.True(outcome is null or OperationCanceledException, outcome?.ToString());

        // 飛び先の頁は読み込め、その後も通常どおり使える
        var last = await loader.GetAsync(199);
        Assert.NotNull(last.Image);
        loader.Prefetch(100);
        Assert.NotNull((await loader.GetAsync(100)).Image);
        Assert.InRange(loader.CachedCount, 1, 4);
    }

    [Fact]
    public async Task Dispose後は要求を受け付けない()
    {
        using var ctx = new LibraryTestContext();
        using var session = await OpenBookAsync(ctx, 3);
        var loader = new PageImageLoader(session, new ViewerImageDecoder());
        loader.Dispose();
        loader.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = loader.GetAsync(0); // Taskを返す前に、同期的に例外になる
        });
        loader.Prefetch(1); // 例外にならない
        Assert.Equal(0, loader.CachedCount);
    }
}

public class ViewerImageDecoderTests
{
    [Fact]
    public void Decode_通常の画像は元の大きさのまま読み込む()
    {
        var decoder = new ViewerImageDecoder();

        var jpeg = decoder.Decode(TestImages.Jpeg(1200, 800));
        var png = decoder.Decode(TestImages.Png(300, 500));

        Assert.Equal((1200, 800), (jpeg.PixelWidth, jpeg.PixelHeight));
        Assert.Equal((300, 500), (png.PixelWidth, png.PixelHeight));
        Assert.True(jpeg.IsFrozen);
    }

    [Theory]
    [InlineData(2000, 1000)] // 横長
    [InlineData(1000, 2000)] // 縦長
    public void Decode_画素数の上限を超える画像は_縦横比を保って縮小して読み込む(int width, int height)
    {
        var decoder = new ViewerImageDecoder(maxPixels: 500_000);

        var image = decoder.Decode(TestImages.Png(width, height));

        Assert.True((long)image.PixelWidth * image.PixelHeight <= 500_000, $"{image.PixelWidth}x{image.PixelHeight}");
        Assert.InRange((double)image.PixelWidth / image.PixelHeight, ((double)width / height) - 0.02, ((double)width / height) + 0.02);
    }

    [Fact]
    public void Decode_上限ちょうどまでの画像は縮小しない()
    {
        var decoder = new ViewerImageDecoder(maxPixels: 200_000);

        var image = decoder.Decode(TestImages.Png(500, 400)); // ちょうど20万画素

        Assert.Equal((500, 400), (image.PixelWidth, image.PixelHeight));
    }

    [Fact]
    public void Decode_壊れた画像と空のデータは_ImageDecodeExceptionになる()
    {
        var decoder = new ViewerImageDecoder();

        Assert.Throws<ImageDecodeException>(() => decoder.Decode(TestImages.Corrupt()));
        Assert.Throws<ImageDecodeException>(() => decoder.Decode([]));
        Assert.Throws<ImageDecodeException>(() => decoder.Decode(new byte[100]));
    }
}
