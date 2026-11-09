using System.Windows.Input;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>ビューアの一覧表示（サムネイルグリッド）、画像の選択、1枚表示との位置・選択の同期。</summary>
public class ViewerListModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ViewerViewModel CreateViewer(LibraryTestContext ctx) =>
        new(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>
    /// ページ i（1始まり）の幅が 100 + 10×i、高さが 60 のJPEGを持つZIPを登録して開き、1行に4枚並ぶ幅にする。
    /// 幅を見れば、どのページが1枚表示されているか分かる。
    /// </summary>
    private static async Task<ViewerViewModel> OpenAsync(LibraryTestContext ctx, int pages = 10)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            [.. Enumerable.Range(1, pages).Select(i => ($"{i:D3}.jpg", TestImages.Jpeg(100 + (10 * i), 60)))]);
        await ctx.RegisterAndScanAsync();
        var viewer = CreateViewer(ctx);
        Assert.True(await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct));
        viewer.UpdateListViewport((ViewerViewModel.PageCellWidth * 4) + 10);
        return viewer;
    }

    private static string Positions(ViewerViewModel viewer) => string.Join(",", viewer.SelectedIndices);

    [Fact]
    public async Task 一覧表示に切り替えると_閲覧していた画像が選択され_現在位置として強調される()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        for (var i = 0; i < 4; i++)
        {
            viewer.NextCommand.Execute(null); // 5枚目（位置4）を閲覧中
        }

        var scrollRows = new List<int>();
        viewer.ScrollToRowRequested += (_, row) => scrollRows.Add(row);
        Assert.True(viewer.IsSingleView);
        Assert.Equal("一覧表示（G）", viewer.ViewModeToggleText);

        viewer.ToggleViewModeCommand.Execute(null);

        Assert.True(viewer.IsListView);
        Assert.False(viewer.IsSingleView);
        Assert.Equal("1枚表示（G）", viewer.ViewModeToggleText);
        Assert.Equal(10, viewer.Pages.Count);
        Assert.Equal("4", Positions(viewer));
        Assert.Equal(1, viewer.SelectedCount);
        Assert.Equal("選択 1枚", viewer.SelectionText);
        Assert.True(viewer.Pages[4].IsSelected);
        Assert.True(viewer.Pages[4].IsCurrent);
        Assert.Equal(1, viewer.Pages.Count(p => p.IsSelected));
        Assert.Equal(1, viewer.Pages.Count(p => p.IsCurrent));
        Assert.Equal("5 / 10", viewer.PositionText);
        Assert.Equal([4, 4, 2], viewer.PageRows.Select(r => r.Items.Count)); // 1行4枚
        Assert.Contains(1, scrollRows); // 現在位置（位置4）は2行目。画面に表示するよう要求される
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧で選んだ画像から1枚表示に戻り_選択は保たれる()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        viewer.ToggleViewModeCommand.Execute(null);

        viewer.OnPageClicked(7, ModifierKeys.None, clickCount: 1); // 8枚目を選ぶ
        viewer.ToggleViewModeCommand.Execute(null);

        Assert.True(viewer.IsSingleView);
        Assert.Equal("8 / 10", viewer.PositionText);
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 180); // 8枚目の画像
        Assert.Equal("7", Positions(viewer)); // 選択はそのまま
        Assert.Empty(viewer.PageRows); // 1枚表示の間は、行を保持しない
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一枚表示で位置を動かしてから一覧に切り替えると_現在の画像が選択される()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.OnPageClicked(1, ModifierKeys.None, 1);
        viewer.OnPageClicked(3, ModifierKeys.Shift, 1);
        Assert.Equal("1,2,3", Positions(viewer));
        viewer.ToggleViewModeCommand.Execute(null); // 1枚表示（位置3）

        // 選択に含まれている位置へ戻ってから切り替えると、複数選択が保たれる
        viewer.PreviousCommand.Execute(null);
        viewer.ToggleViewModeCommand.Execute(null);
        Assert.Equal("1,2,3", Positions(viewer));
        Assert.True(viewer.Pages[2].IsCurrent);

        // 選択に含まれない位置で切り替えると、その画像だけが選択される
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.NextCommand.Execute(null);
        viewer.NextCommand.Execute(null); // 位置4
        viewer.ToggleViewModeCommand.Execute(null);
        Assert.Equal("4", Positions(viewer));
        Assert.Equal("5 / 10", viewer.PositionText);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task クリックの選択_通常は1枚_Ctrlは切替_Shiftは範囲_ダブルクリックは1枚表示()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        viewer.ToggleViewModeCommand.Execute(null);

        viewer.OnPageClicked(3, ModifierKeys.None, 1);
        Assert.Equal("3", Positions(viewer));
        Assert.Equal("4 / 10", viewer.PositionText); // クリックした画像が現在位置になる

        viewer.OnPageClicked(6, ModifierKeys.Control, 1);
        Assert.Equal("3,6", Positions(viewer));
        Assert.Equal("7 / 10", viewer.PositionText);

        viewer.OnPageClicked(3, ModifierKeys.Control, 1); // 選択済みをCtrl+クリックすると外れる
        Assert.Equal("6", Positions(viewer));

        viewer.OnPageClicked(2, ModifierKeys.None, 1); // 通常のクリックは、複数選択を置き換える
        Assert.Equal("2", Positions(viewer));

        viewer.OnPageClicked(5, ModifierKeys.Shift, 1); // 起点（2）から5まで
        Assert.Equal("2,3,4,5", Positions(viewer));
        viewer.OnPageClicked(0, ModifierKeys.Shift, 1); // 起点は変わらないので、2から0まで
        Assert.Equal("0,1,2", Positions(viewer));
        Assert.Equal("選択 3枚", viewer.SelectionText);

        viewer.OnPageClicked(8, ModifierKeys.None, 2); // ダブルクリック
        Assert.True(viewer.IsSingleView);
        Assert.Equal("9 / 10", viewer.PositionText);
        await WaitUntilAsync(() => viewer.CurrentImage?.PixelWidth == 190);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task クリックは_一覧表示でないときや範囲外の位置では無視する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);

        viewer.OnPageClicked(3, ModifierKeys.None, 1); // 1枚表示の間
        Assert.Equal("1 / 10", viewer.PositionText);
        Assert.Empty(viewer.SelectedIndices);

        viewer.ToggleViewModeCommand.Execute(null);
        viewer.OnPageClicked(-1, ModifierKeys.None, 1);
        viewer.OnPageClicked(10, ModifierKeys.None, 1);
        Assert.Equal("0", Positions(viewer)); // 切り替えで選ばれた現在位置のまま
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示のキー操作_矢印で移動し_Shiftで範囲選択_端で止まる()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        viewer.ToggleViewModeCommand.Execute(null);
        Assert.Equal("0", Positions(viewer));

        Assert.True(viewer.HandleKey(Key.Right, ModifierKeys.None));
        Assert.Equal(("2 / 10", "1"), (viewer.PositionText, Positions(viewer)));
        Assert.True(viewer.HandleKey(Key.Down, ModifierKeys.None)); // 1行（4枚）下へ
        Assert.Equal(("6 / 10", "5"), (viewer.PositionText, Positions(viewer)));
        Assert.True(viewer.HandleKey(Key.Left, ModifierKeys.None));
        Assert.True(viewer.HandleKey(Key.Up, ModifierKeys.None));
        Assert.Equal(("1 / 10", "0"), (viewer.PositionText, Positions(viewer)));

        Assert.True(viewer.HandleKey(Key.Up, ModifierKeys.None)); // 先頭の行では、これ以上上へ行けない
        Assert.Equal("1 / 10", viewer.PositionText);
        Assert.True(viewer.HandleKey(Key.Left, ModifierKeys.None));
        Assert.Equal("1 / 10", viewer.PositionText);

        Assert.True(viewer.HandleKey(Key.Right, ModifierKeys.Shift)); // 0〜1
        Assert.True(viewer.HandleKey(Key.Down, ModifierKeys.Shift)); // 0〜5
        Assert.Equal("0,1,2,3,4,5", Positions(viewer));
        Assert.True(viewer.HandleKey(Key.Left, ModifierKeys.Shift)); // 0〜4
        Assert.Equal("0,1,2,3,4", Positions(viewer));

        Assert.True(viewer.HandleKey(Key.End, ModifierKeys.None));
        Assert.Equal(("10 / 10", "9"), (viewer.PositionText, Positions(viewer)));
        Assert.True(viewer.HandleKey(Key.Right, ModifierKeys.None)); // 末尾から先へは進まない
        Assert.Equal("10 / 10", viewer.PositionText);
        Assert.True(viewer.HandleKey(Key.Home, ModifierKeys.None));
        Assert.Equal(("1 / 10", "0"), (viewer.PositionText, Positions(viewer)));
        Assert.True(viewer.HandleKey(Key.End, ModifierKeys.Shift));
        Assert.Equal(10, viewer.SelectedCount);

        viewer.HandleKey(Key.Home, ModifierKeys.None);
        Assert.True(viewer.HandleKey(Key.PageDown, ModifierKeys.None)); // 3行（12枚）下だが、末尾で止まる
        Assert.Equal("10 / 10", viewer.PositionText);
        Assert.True(viewer.HandleKey(Key.PageUp, ModifierKeys.None)); // 3行（12枚）上だが、先頭で止まる
        Assert.Equal("1 / 10", viewer.PositionText);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示のキー操作_Spaceで選択を切り替え_Ctrl_Aで全選択_Enterで1枚表示_Gで戻る()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.HandleKey(Key.Right, ModifierKeys.None);
        viewer.HandleKey(Key.Right, ModifierKeys.None); // 位置2

        Assert.True(viewer.HandleKey(Key.Space, ModifierKeys.None)); // 選択を外す
        Assert.Empty(viewer.SelectedIndices);
        Assert.Equal("3 / 10", viewer.PositionText); // 1枚表示のSpaceと違い、頁は進まない
        Assert.True(viewer.HandleKey(Key.Space, ModifierKeys.None));
        Assert.Equal("2", Positions(viewer));

        Assert.True(viewer.HandleKey(Key.A, ModifierKeys.Control));
        Assert.Equal(10, viewer.SelectedCount);
        Assert.Equal("選択 10枚", viewer.SelectionText);

        Assert.True(viewer.HandleKey(Key.Enter, ModifierKeys.None)); // 現在位置を1枚表示
        Assert.True(viewer.IsSingleView);
        Assert.Equal("3 / 10", viewer.PositionText);
        Assert.Equal(10, viewer.SelectedCount); // 選択は保たれる

        Assert.True(viewer.HandleKey(Key.G, ModifierKeys.None));
        Assert.True(viewer.IsListView);
        Assert.True(viewer.HandleKey(Key.G, ModifierKeys.None));
        Assert.True(viewer.IsSingleView);
        Assert.False(viewer.HandleKey(Key.G, ModifierKeys.Control)); // Ctrl+G は処理しない
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 表示方式ごとにキーの意味が異なる_1枚表示の拡大キーは一覧では処理しない()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);

        Assert.True(viewer.HandleKey(Key.Space, ModifierKeys.None)); // 1枚表示: 次の頁
        Assert.Equal("2 / 10", viewer.PositionText);
        Assert.Empty(viewer.SelectedIndices);

        viewer.HandleKey(Key.G, ModifierKeys.None);
        Assert.False(viewer.HandleKey(Key.OemPlus, ModifierKeys.None));
        Assert.False(viewer.HandleKey(Key.D0, ModifierKeys.None));
        Assert.False(viewer.HandleKey(Key.D1, ModifierKeys.None));
        Assert.Equal(ZoomMode.Fit, viewer.ZoomMode); // 一覧表示では、拡大率の操作は効かない
        Assert.True(viewer.HandleKey(Key.F11, ModifierKeys.None)); // 全画面は、どちらの表示方式でも使える
        Assert.True(viewer.IsFullScreen);
        var closeRequests = 0;
        viewer.CloseRequested += (_, _) => closeRequests++;
        Assert.True(viewer.HandleKey(Key.Escape, ModifierKeys.None));
        Assert.Equal(1, closeRequests);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示で位置を動かすと_画像は読み込まずに_閲覧位置だけが保存される()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, pages: 12);
        await Task.Delay(500, Ct); // 最初の頁と先読みが済むのを待つ
        var readsBefore = ctx.Reader.OpenedReadCalls;
        viewer.ToggleViewModeCommand.Execute(null);

        viewer.HandleKey(Key.End, ModifierKeys.None);
        viewer.HandleKey(Key.Up, ModifierKeys.None);
        viewer.HandleKey(Key.Left, ModifierKeys.None);

        Assert.Equal("7 / 12", viewer.PositionText);
        Assert.Equal(readsBefore, ctx.Reader.OpenedReadCalls); // 動かしただけでは、頁の画像を読み込まない（サムネイルも未要求）
        await WaitUntilAsync(
            () => ctx.ScalarAsync("SELECT COALESCE(MAX(last_view_index), -1) FROM archives").GetAwaiter().GetResult() == 6,
            seconds: 20);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 閉じると一覧表示と選択の状態が片付き_開き直すと1枚表示から始まる()
    {
        using var ctx = new LibraryTestContext();
        var viewer = await OpenAsync(ctx);
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.SelectAll();
        Assert.Equal(10, viewer.SelectedCount);

        await viewer.CloseAsync();

        Assert.False(viewer.IsListView);
        Assert.Empty(viewer.Pages);
        Assert.Empty(viewer.PageRows);
        Assert.Empty(viewer.SelectedIndices);
        Assert.Equal(0, viewer.SelectedCount);
        Assert.Equal(string.Empty, viewer.SelectionText);

        Assert.True(await viewer.OpenAsync(id, Ct));
        Assert.True(viewer.IsSingleView);
        Assert.Empty(viewer.SelectedIndices); // 選択は、開き直しても引き継がない
        Assert.Equal(10, viewer.Pages.Count);
        await viewer.CloseAsync();
        viewer.Dispose();
    }

    [Fact]
    public async Task 表示領域の幅に合わせて_1行に並べる枚数と行のまとまりが決まる()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, pages: 11);
        viewer.ToggleViewModeCommand.Execute(null);
        Assert.Equal(4, viewer.ColumnCount);
        Assert.Equal([4, 4, 3], viewer.PageRows.Select(r => r.Items.Count));

        viewer.UpdateListViewport(ViewerViewModel.PageCellWidth * 3);
        Assert.Equal([3, 3, 3, 2], viewer.PageRows.Select(r => r.Items.Count));
        Assert.Equal(
            Enumerable.Range(0, 11),
            viewer.PageRows.SelectMany(r => r.Items).Select(p => p.Index)); // 並び順が保たれる

        viewer.UpdateListViewport(10); // 狭くても最低1枚
        Assert.Equal(1, viewer.ColumnCount);
        Assert.Equal(11, viewer.PageRows.Count);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧のカードは_画面に出て初めてサムネイルを生成し_生成後に変更を通知する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, pages: 6);
        viewer.ToggleViewModeCommand.Execute(null);
        var item = viewer.Pages[3];
        var changed = new TaskCompletionSource();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageItem.Thumbnail))
            {
                changed.TrySetResult();
            }
        };
        Assert.Equal("読み込み中…", item.PlaceholderText);
        Assert.Equal("004.jpg", item.DisplayName);
        Assert.Equal("4", item.PageNumberText);

        Assert.Null(item.Thumbnail); // 最初の参照（画面に出た）で生成が始まる
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var image = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(item.Thumbnail);
        Assert.Equal((140, 60), (image.PixelWidth, image.PixelHeight));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧のカード_壊れた画像は_読み込めない表示になる_フォルダ内の名前はファイル名だけを表示する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("ch1/p1.jpg", TestImages.Jpeg(50, 50)), ("ch1/p2.jpg", TestImages.Corrupt()));
        await ctx.RegisterAndScanAsync();
        using var viewer = CreateViewer(ctx);
        await viewer.OpenAsync(await ctx.ScalarAsync("SELECT id FROM archives"), Ct);
        viewer.ToggleViewModeCommand.Execute(null);
        var broken = viewer.Pages[1];
        var changed = new TaskCompletionSource();
        broken.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageItem.PlaceholderText))
            {
                changed.TrySetResult();
            }
        };

        Assert.Null(broken.Thumbnail);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("読み込めません", broken.PlaceholderText);
        Assert.Equal("p2.jpg", broken.DisplayName);
        Assert.Equal("ch1/p2.jpg", broken.EntryName);
        Assert.Null(broken.Thumbnail);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 選択の操作は_範囲外を無視し_選択した位置を昇順で返す()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx);
        viewer.ToggleViewModeCommand.Execute(null);

        viewer.SelectOnly(99);
        viewer.ToggleSelection(-1);
        viewer.SelectRange(0, 99);
        Assert.Equal("0", Positions(viewer));

        viewer.ToggleSelection(7);
        viewer.ToggleSelection(2);
        viewer.ToggleSelection(5);
        Assert.Equal([0, 2, 5, 7], viewer.SelectedIndices);

        viewer.ClearSelection();
        Assert.Empty(viewer.SelectedIndices);
        Assert.Equal(string.Empty, viewer.SelectionText);
        Assert.All(viewer.Pages, p => Assert.False(p.IsSelected));
        await viewer.CloseAsync();
    }
}
