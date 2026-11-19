using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;

namespace K35Viewer.Tests.Services;

/// <summary>サムネイルの編集（GitHub の Issue #15）。表紙の画像を選び、切り抜いて、サムネイルにする。</summary>
public class CoverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>1.jpg（横長 400x200）、2.jpg（縦長 200x400）、3.png（左半分が透明・右半分が赤の 200x100）の入った book.zip。</summary>
    private static async Task<long> CreateBookAsync(LibraryTestContext ctx)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("1.jpg", TestImages.Jpeg(400, 200)),
            ("2.jpg", TestImages.Jpeg(200, 400)),
            ("3.png", TestImages.PngHalfTransparent(200, 100)));
        _ = await ctx.RegisterAndScanAsync();
        _ = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        return await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'book.zip'");
    }

    private static Task<string> SourceEntryAsync(LibraryTestContext ctx, long id) =>
        ctx.StringAsync($"SELECT source_entry FROM thumbnail_cache WHERE archive_id = {id}");

    private static async Task<(int Width, int Height)> ThumbnailSizeAsync(LibraryTestContext ctx, long id) =>
        TestImages.SizeOf(ctx.Cache.Read(await ctx.StringAsync($"SELECT relative_path FROM thumbnail_cache WHERE archive_id = {id}")));

    private static async Task<bool> HasCustomCoverAsync(LibraryTestContext ctx)
    {
        var result = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(SearchText: "book"), Ct);
        return Assert.Single(result.Items).HasCustomCover;
    }

    // ---- 範囲（CoverCrop） ----

    [Theory]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(0.25, 0.1, 0.5, 0.8, true)]
    [InlineData(0.5, 0, 0.6, 1, false)] // 右へはみ出す
    [InlineData(0, 0, 0, 1, false)] // 幅が 0
    [InlineData(-0.1, 0, 0.5, 0.5, false)]
    [InlineData(1, 0, 0.5, 0.5, false)]
    [InlineData(double.NaN, 0, 0.5, 0.5, false)]
    public void 切り抜きの範囲は_画像の中に収まるときだけ正しい(double x, double y, double width, double height, bool expected) =>
        Assert.Equal(expected, new CoverCrop(x, y, width, height).IsValid);

    [Fact]
    public void 切り抜きの範囲は_画素に直すと_画像の外に出ず_1画素以上になる()
    {
        Assert.Equal((100, 50, 200, 100), new CoverCrop(0.25, 0.25, 0.5, 0.5).ToPixels(400, 200));
        Assert.Equal((399, 0, 1, 1), new CoverCrop(0.9999, 0, 0.0001, 0.0001).ToPixels(400, 200));
    }

    // ---- 切り抜きの枠（CoverCropEditor） ----

    [Fact]
    public void 枠は_はじめは画像の全体で_カードの縦横比のときは収まるいちばん大きな枠を真ん中に置く()
    {
        var free = new CoverCropEditor(1000, 500);
        Assert.Equal((0.0, 0.0, 1000.0, 500.0), free.Rect);

        var wide = new CoverCropEditor(1000, 500, lockAspect: true);
        Assert.Equal(500, wide.Height, 6);
        Assert.Equal(500 * CoverCropEditor.AspectRatio, wide.Width, 6);
        Assert.Equal((1000 - wide.Width) / 2, wide.X, 6);
        Assert.Equal(0, wide.Y, 6);

        var tall = new CoverCropEditor(300, 1000, lockAspect: true);
        Assert.Equal(300, tall.Width, 6);
        Assert.Equal(300 / CoverCropEditor.AspectRatio, tall.Height, 6);
        Assert.Equal((1000 - tall.Height) / 2, tall.Y, 6);
    }

    [Fact]
    public void 縦横比が自由なら_辺のつまみで横だけ_縦だけを変えられ_反対の辺は動かない()
    {
        // 見開き（横長）の左半分だけを選ぶ
        var editor = new CoverCropEditor(1000, 500);
        editor.Resize(CropHandle.Right, -500, 0);
        Assert.Equal((0.0, 0.0, 500.0, 500.0), editor.Rect);

        editor.Resize(CropHandle.Top, 0, 100);
        Assert.Equal((0.0, 100.0, 500.0, 400.0), editor.Rect);

        editor.Resize(CropHandle.BottomLeft, 50, -50);
        Assert.Equal((50.0, 100.0, 450.0, 350.0), editor.Rect);

        var crop = editor.ToCrop();
        Assert.Equal((0.05, 0.2, 0.45, 0.7), (Math.Round(crop.X, 6), Math.Round(crop.Y, 6), Math.Round(crop.Width, 6), Math.Round(crop.Height, 6)));
    }

    [Fact]
    public void 縦横比が自由でも_画像の外へは広げず_下限より小さくしない()
    {
        var editor = new CoverCropEditor(1000, 500);
        editor.Resize(CropHandle.Left, -300, 0); // すでに左端
        Assert.Equal(0, editor.X, 6);
        Assert.Equal(1000, editor.Width, 6);

        editor.Resize(CropHandle.Right, -5000, 0);
        Assert.Equal(editor.MinWidth, editor.Width, 6);
        editor.Resize(CropHandle.Bottom, 0, -5000);
        Assert.Equal(editor.MinHeight, editor.Height, 6);
    }

    [Fact]
    public void 枠を動かしても_画像の外には出ない()
    {
        var editor = new CoverCropEditor(1000, 500);
        editor.Scale(0.5);
        editor.Move(-5000, 30);
        Assert.Equal(0, editor.X, 6);
        editor.Move(5000, 5000);
        Assert.Equal(1000 - editor.Width, editor.X, 6);
        Assert.Equal(500 - editor.Height, editor.Y, 6);
    }

    [Fact]
    public void カードの縦横比のときは_角でも辺でも_比が保たれ_画像の外へ広がらない()
    {
        var editor = new CoverCropEditor(1000, 1000, lockAspect: true);
        editor.Scale(0.5);
        var right = editor.X + editor.Width;
        var bottom = editor.Y + editor.Height;

        editor.Resize(CropHandle.TopLeft, 40, 0); // 左上を右へ（縮める）。右下は動かない
        Assert.Equal(right, editor.X + editor.Width, 6);
        Assert.Equal(bottom, editor.Y + editor.Height, 6);
        Assert.Equal(CoverCropEditor.AspectRatio, editor.Width / editor.Height, 6);

        editor.Resize(CropHandle.Right, 100, 0);
        Assert.Equal(CoverCropEditor.AspectRatio, editor.Width / editor.Height, 6);

        editor.Resize(CropHandle.Bottom, 0, 5000);
        Assert.True(editor.Y + editor.Height <= 1000 + 1e-6);
        Assert.Equal(CoverCropEditor.AspectRatio, editor.Width / editor.Height, 6);

        editor.Resize(CropHandle.BottomRight, 5000, 5000);
        Assert.True(editor.X + editor.Width <= 1000 + 1e-6);
        Assert.Equal(CoverCropEditor.AspectRatio, editor.Width / editor.Height, 6);
    }

    [Fact]
    public void カードの縦横比に切り替えると_今の枠の中心を保って_比を合わせる()
    {
        var editor = new CoverCropEditor(1000, 500);
        editor.Resize(CropHandle.Right, -500, 0); // 左半分（500x500）
        editor.LockAspect = true;
        Assert.Equal(CoverCropEditor.AspectRatio, editor.Width / editor.Height, 6);
        Assert.Equal(250, editor.X + (editor.Width / 2), 6); // 中心は、左半分の真ん中
        Assert.True(editor.Height <= 500 + 1e-6);

        editor.LockAspect = false;
        editor.Resize(CropHandle.Right, 100, 0); // 自由に戻すと、横だけ変えられる
        Assert.NotEqual(CoverCropEditor.AspectRatio, editor.Width / editor.Height, 3);
    }

    [Fact]
    public void 枠の範囲は_割合に直すと正しく_前の範囲から始められる()
    {
        var editor = new CoverCropEditor(800, 600);
        editor.Resize(CropHandle.BottomRight, -400, -100);
        var crop = editor.ToCrop();
        Assert.True(crop.IsValid);
        Assert.Equal(0, crop.X, 6);
        Assert.Equal(0, crop.Y, 6);
        Assert.Equal(0.5, crop.Width, 6);
        Assert.Equal(500.0 / 600, crop.Height, 6);

        var again = new CoverCropEditor(800, 600, crop);
        Assert.Equal(editor.X, again.X, 6);
        Assert.Equal(editor.Y, again.Y, 6);
        Assert.Equal(editor.Width, again.Width, 6);
        Assert.Equal(editor.Height, again.Height, 6);
    }

    [Fact]
    public void ドラッグを始めた枠に戻せる()
    {
        var editor = new CoverCropEditor(1000, 500);
        var start = editor.Rect;
        editor.Resize(CropHandle.Right, -800, 0);
        editor.Restore(start);
        Assert.Equal(start, editor.Rect);
    }

    // ---- 切り抜きの画面（CoverCropDialog） ----

    [Fact]
    public void 切り抜きの画面は_はじめは画像の全体を返し_カードの縦横比にすると枠がその比になり_キーで動かせる()
    {
        CoverCrop? first = null;
        CoverCrop? moved = null;
        var lockedRatio = 0.0;
        var previewShown = false;
        WpfTestHelper.RunOnStaThread(() =>
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(TestImages.Jpeg(800, 400));
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            // はじめの枠（画像の全体）を、そのまま返す
            var dialog = new K35Viewer.Views.CoverCropDialog(image, "2.jpg");
            _ = dialog.Dispatcher.BeginInvoke(
                () =>
                {
                    previewShown = ((System.Windows.Controls.Image)dialog.FindName("Preview")).Source is not null;
                    ClickOk(dialog);
                },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _ = dialog.ShowDialog();
            first = dialog.Crop;

            // カードの縦横比にして、右へ動かしてから返す
            var again = new K35Viewer.Views.CoverCropDialog(image, "2.jpg");
            _ = again.Dispatcher.BeginInvoke(
                () =>
                {
                    ((System.Windows.Controls.CheckBox)again.FindName("LockAspectBox")).IsChecked = true;
                    var frame = (System.Windows.Shapes.Rectangle)again.FindName("Frame");
                    lockedRatio = frame.Width / frame.Height;
                    var surface = (System.Windows.Controls.Canvas)again.FindName("Surface");
                    var source = System.Windows.PresentationSource.FromVisual(surface)!;
                    for (var i = 0; i < 100; i++)
                    {
                        surface.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                            System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Right)
                        {
                            RoutedEvent = System.Windows.UIElement.KeyDownEvent,
                        });
                    }

                    ClickOk(again);
                },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _ = again.ShowDialog();
            moved = again.Crop;

            static void ClickOk(System.Windows.Window window) =>
                WpfTestHelper.Descendants<System.Windows.Controls.Button>(window).Single(b => Equals(b.Content, "表紙にする"))
                    .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });

        Assert.True(previewShown, "できあがりの見本が出ていません。");
        Assert.Equal(new CoverCrop(0, 0, 1, 1), first);
        Assert.Equal(CoverCropEditor.AspectRatio, lockedRatio, 3);
        Assert.NotNull(moved);
        Assert.Equal(1, moved.Height, 6);
        Assert.Equal(1 - moved.Width, moved.X, 6); // 右端まで動いた
    }

    [Fact]
    public void カードと縦横比が違う範囲は_その形のままサムネイルになる()
    {
        // 見開き（800x400）の左半分の、さらに上の 3/4 → 400x300 の範囲
        var thumbnail = new ThumbnailGenerator().Generate(TestImages.Jpeg(800, 400), new CoverCrop(0, 0, 0.5, 0.75));
        Assert.Equal((300, 225), (thumbnail.Width, thumbnail.Height));
    }

    // ---- サムネイルの作成（ThumbnailGenerator） ----

    [Fact]
    public void 切り抜きを指定すると_その範囲からサムネイルを作る()
    {
        var generator = new ThumbnailGenerator();
        var image = TestImages.PngHalfTransparent(400, 200); // 左半分は透明（白になる）、右半分は赤

        var right = generator.Generate(image, new CoverCrop(0.5, 0, 0.5, 1));
        Assert.Equal((200, 200), (right.Width, right.Height));
        var (red, green, _) = TestImages.PixelAt(right.Data, 10, 100);
        Assert.True(red > 200 && green < 60, "右半分（赤）から作られていない");

        var left = generator.Generate(image, new CoverCrop(0, 0, 0.5, 1));
        var (r, g, b) = TestImages.PixelAt(left.Data, 190, 100);
        Assert.True(r > 230 && g > 230 && b > 230, "左半分（白）から作られていない");
    }

    [Fact]
    public void 切り抜いた範囲は_長辺がサムネイルの大きさまで縮められ_拡大はしない()
    {
        var generator = new ThumbnailGenerator();
        var big = generator.Generate(TestImages.Jpeg(2000, 2000), new CoverCrop(0, 0, 0.5, 0.25));
        Assert.Equal((ThumbnailGenerator.LongSide, ThumbnailGenerator.LongSide / 2), (big.Width, big.Height));

        var small = generator.Generate(TestImages.Jpeg(200, 200), new CoverCrop(0.25, 0.25, 0.5, 0.5));
        Assert.Equal((100, 100), (small.Width, small.Height));
    }

    // ---- 表紙の記録と、作り直し（ThumbnailService） ----

    [Fact]
    public async Task 表紙を選ぶと_その画像からサムネイルを作り直し_記録が残る()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateBookAsync(ctx);
        Assert.Equal("1.jpg", await SourceEntryAsync(ctx, id));
        Assert.False(await HasCustomCoverAsync(ctx));

        var result = await ctx.Thumbnails.SetCoverAsync(id, new CoverSelection("2.jpg", null), Ct);

        Assert.NotNull(result);
        Assert.Equal("READY", result.ThumbnailStatus);
        Assert.True(result.HasCustomCover);
        Assert.Equal("2.jpg", await SourceEntryAsync(ctx, id));
        Assert.Equal((150, 300), await ThumbnailSizeAsync(ctx, id)); // 2.jpg（200x400）から作られた
        Assert.Equal("2.jpg", await ctx.StringAsync($"SELECT cover_entry FROM archives WHERE id = {id}"));
        Assert.True(await HasCustomCoverAsync(ctx));
    }

    [Fact]
    public async Task 切り抜きの範囲も記録され_キャッシュを削除して作り直しても_同じ表紙になる()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateBookAsync(ctx);
        var crop = new CoverCrop(0.5, 0, 0.5, 1);

        _ = await ctx.Thumbnails.SetCoverAsync(id, new CoverSelection("3.png", crop), Ct);
        Assert.Equal(0.5, double.Parse(await ctx.StringAsync($"SELECT cover_crop_x FROM archives WHERE id = {id}"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((100, 100), await ThumbnailSizeAsync(ctx, id)); // 3.png（200x100）の右半分

        Assert.True(await ctx.Thumbnails.ClearCacheAsync(Ct));
        _ = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal("3.png", await SourceEntryAsync(ctx, id));
        Assert.Equal((100, 100), await ThumbnailSizeAsync(ctx, id));
    }

    [Fact]
    public async Task 自動に戻すと_記録が消え_先頭の画像から作り直す()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateBookAsync(ctx);
        _ = await ctx.Thumbnails.SetCoverAsync(id, new CoverSelection("2.jpg", new CoverCrop(0, 0, 1, 0.5)), Ct);

        var result = await ctx.Thumbnails.SetCoverAsync(id, null, Ct);

        Assert.False(result!.HasCustomCover);
        Assert.Equal("1.jpg", await SourceEntryAsync(ctx, id));
        Assert.Equal(0, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {id} AND (cover_entry IS NOT NULL OR cover_crop_x IS NOT NULL)"));
        Assert.False(await HasCustomCoverAsync(ctx));
    }

    [Fact]
    public async Task 選んだ画像がアーカイブからなくなると_記録を消して_自動に戻す()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateBookAsync(ctx);
        _ = await ctx.Thumbnails.SetCoverAsync(id, new CoverSelection("2.jpg", null), Ct);

        // 2.jpg を除いて作り直した（削除予約の確定や、外部での変更と同じ）
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(400, 200)), ("3.png", TestImages.Png(50, 50)));
        File.SetLastWriteTimeUtc(ctx.PathOf("book.zip"), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _ = await ctx.Scan.ScanAllAsync(ScanType.Manual, Ct);
        _ = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal("1.jpg", await SourceEntryAsync(ctx, id));
        Assert.Equal(0, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE id = {id} AND cover_entry IS NOT NULL"));
    }

    [Fact]
    public async Task 選んだ画像が読めなければ_今回は自動で作り_記録は残す()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(400, 200)), ("2.jpg", TestImages.Corrupt()));
        _ = await ctx.RegisterAndScanAsync();
        _ = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var id = await ctx.ScalarAsync("SELECT id FROM archives");

        var result = await ctx.Thumbnails.SetCoverAsync(id, new CoverSelection("2.jpg", null), Ct);

        Assert.Equal("READY", result!.ThumbnailStatus);
        Assert.Equal("1.jpg", await SourceEntryAsync(ctx, id));
        Assert.Equal("2.jpg", await ctx.StringAsync($"SELECT cover_entry FROM archives WHERE id = {id}"));
    }

    [Fact]
    public async Task 正しくない切り抜きの範囲は_記録しない()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateBookAsync(ctx);
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => ctx.Thumbnails.SetCoverAsync(id, new CoverSelection("2.jpg", new CoverCrop(0.8, 0, 0.5, 1)), Ct));
        Assert.Equal(0, await ctx.ScalarAsync($"SELECT COUNT(*) FROM archives WHERE cover_entry IS NOT NULL"));
    }

    // ---- 画面の流れ（ビューアの右クリック → MainViewModel） ----

    private sealed class FakeCoverDialogs(CoverCrop? result) : ICoverDialogs
    {
        public List<(int Bytes, string Entry)> Calls { get; } = [];

        public CoverCrop? PickCrop(byte[] imageData, string entryName)
        {
            Calls.Add((imageData.Length, entryName));
            return result;
        }
    }

    private static async Task<(MainTestHarness Harness, List<Notification> Shown)> OpenViewerAsync(
        LibraryTestContext ctx, ICoverDialogs dialogs)
    {
        var harness = new MainTestHarness(ctx, coverDialogs: dialogs);
        _ = await CreateBookAsync(ctx);
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single());
        await WaitUntilAsync(() => harness.Main.IsViewerVisible && harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);
        return (harness, shown);
    }

    [Fact]
    public async Task ビューアで今の画像を表紙にすると_一覧の表紙が変わり_通知される()
    {
        using var ctx = new LibraryTestContext();
        var dialogs = new FakeCoverDialogs(null);
        var (harness, shown) = await OpenViewerAsync(ctx, dialogs);
        using var _ = harness;
        var id = harness.Viewer.Session!.ArchiveId;

        await harness.Main.ChangeCoverAsync(new CoverRequest(id, 1, "2.jpg", Crop: false));

        Assert.Empty(dialogs.Calls); // 切り抜かないときは、画面を出さない
        Assert.Equal("2.jpg", await SourceEntryAsync(ctx, id));
        await WaitUntilAsync(() => harness.Library.Archives.Single().HasCustomCover);
        var success = Assert.Single(shown);
        Assert.Equal(NotificationLevel.Success, success.Level);
        Assert.Contains("表紙を変えました", success.Message, StringComparison.Ordinal);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 切り抜くときは_画像を渡して範囲を選んでもらい_キャンセルなら何も変えない()
    {
        using var ctx = new LibraryTestContext();
        var cancel = new FakeCoverDialogs(null);
        var (harness, shown) = await OpenViewerAsync(ctx, cancel);
        using var _ = harness;
        var id = harness.Viewer.Session!.ArchiveId;

        await harness.Main.ChangeCoverAsync(new CoverRequest(id, 1, "2.jpg", Crop: true));

        var call = Assert.Single(cancel.Calls);
        Assert.Equal("2.jpg", call.Entry);
        Assert.True(call.Bytes > 0);
        Assert.Equal("1.jpg", await SourceEntryAsync(ctx, id));
        Assert.Empty(shown);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 切り抜いた範囲で_表紙を作り直す()
    {
        using var ctx = new LibraryTestContext();
        var dialogs = new FakeCoverDialogs(new CoverCrop(0, 0.25, 1, 0.5));
        var (harness, _) = await OpenViewerAsync(ctx, dialogs);
        using var __ = harness;
        var id = harness.Viewer.Session!.ArchiveId;

        await harness.Main.ChangeCoverAsync(new CoverRequest(id, 1, "2.jpg", Crop: true));

        Assert.Equal("2.jpg", await SourceEntryAsync(ctx, id));
        Assert.Equal((200, 200), await ThumbnailSizeAsync(ctx, id)); // 2.jpg（200x400）の、縦の真ん中の半分
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示では_右クリックした画像が対象になる()
    {
        using var ctx = new LibraryTestContext();
        var (harness, _) = await OpenViewerAsync(ctx, new FakeCoverDialogs(null));
        using var __ = harness;
        var requests = new List<CoverRequest>();
        harness.Viewer.CoverRequested += (_, r) => requests.Add(r);

        harness.Viewer.SetCoverCommand.Execute(null); // 1枚表示: 今の頁（先頭）
        harness.Viewer.ToggleViewModeCommand.Execute(null);
        harness.Viewer.OnPageRightClicked(2);
        harness.Viewer.CropCoverCommand.Execute(null);

        Assert.Equal(2, requests.Count);
        Assert.Equal(("1.jpg", false), (requests[0].EntryName, requests[0].Crop));
        Assert.Equal(("3.png", true, 2), (requests[1].EntryName, requests[1].Crop, requests[1].Index));
        await WaitUntilAsync(() => !harness.Main.IsTransitioning);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task ライブラリの右クリックで_表紙を自動に戻せる()
    {
        using var ctx = new LibraryTestContext();
        var (harness, shown) = await OpenViewerAsync(ctx, new FakeCoverDialogs(null));
        using var _ = harness;
        var id = harness.Viewer.Session!.ArchiveId;
        await harness.Main.ChangeCoverAsync(new CoverRequest(id, 1, "2.jpg", Crop: false));
        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => harness.Main.IsLibraryVisible && !harness.Viewer.IsOpen);
        await WaitUntilAsync(() => harness.Library.Archives.Single().HasCustomCover);
        harness.Library.SelectedArchive = harness.Library.Archives.Single();
        shown.Clear();

        Assert.True(harness.Library.ResetCoverSelectedCommand.CanExecute(null));
        await harness.Library.ResetCoverSelectedCommand.ExecuteAsync(null);

        Assert.Equal("1.jpg", await SourceEntryAsync(ctx, id));
        await WaitUntilAsync(() => !harness.Library.Archives.Single().HasCustomCover);
        Assert.False(harness.Library.ResetCoverSelectedCommand.CanExecute(null));
        Assert.Contains(shown, n => n.Level == NotificationLevel.Success && n.Message.Contains("自動に戻しました", StringComparison.Ordinal));
    }
}
