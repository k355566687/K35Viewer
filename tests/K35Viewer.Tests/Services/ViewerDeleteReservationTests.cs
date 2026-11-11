using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using K35Viewer.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>削除予約（UC-08、UC-09、UC-10）と、アーカイブを離れるときの確認（DLG-01）。ZIP本体は変更しない。</summary>
[Collection(WpfViewsGroup.Name)]
public class ViewerDeleteReservationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ViewerViewModel CreateViewer(LibraryTestContext ctx) =>
        new(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>ページ i（1始まり）の幅が 100 + 10×i のJPEGを持つZIPを作って登録する。</summary>
    private static async Task<long> CreateArchiveAsync(LibraryTestContext ctx, int pages)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            [.. Enumerable.Range(1, pages).Select(i => ($"{i:D3}.jpg", TestImages.Jpeg(100 + (10 * i), 60)))]);
        await ctx.RegisterAndScanAsync();
        return await ctx.ScalarAsync("SELECT id FROM archives");
    }

    private static async Task<ViewerViewModel> OpenAsync(LibraryTestContext ctx, int pages)
    {
        var id = await CreateArchiveAsync(ctx, pages);
        var viewer = CreateViewer(ctx);
        Assert.True(await viewer.OpenAsync(id, Ct));
        viewer.UpdateListViewport((ViewerViewModel.PageCellWidth * 4) + 10);
        return viewer;
    }

    /// <summary>ZIP本体の内容と更新日時の組。変更されていないことの確認に使う。</summary>
    private static (string Hash, DateTime Written) Fingerprint(string path) =>
        (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path));

    private static string Positions(IEnumerable<int> indices) => string.Join(",", indices);

    // ---- ViewerSession ----

    [Fact]
    public async Task セッションは_削除予約した画像を前後移動の対象から除く()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateArchiveAsync(ctx, 6);
        var result = await ctx.Viewer.OpenAsync(id, Ct);
        using var session = result.Session!;

        Assert.Equal(2, session.SetPendingDelete([1, 2], true));
        Assert.Equal(0, session.SetPendingDelete([1], true)); // すでに予約済みなら変わらない
        Assert.Equal(0, session.SetPendingDelete([-1, 99], true)); // 範囲外は無視する
        Assert.Equal(2, session.PendingDeleteCount);

        Assert.True(session.MoveNext());
        Assert.Equal(3, session.CurrentIndex); // 1, 2 を飛ばす
        Assert.True(session.MovePrevious());
        Assert.Equal(0, session.CurrentIndex);
        Assert.False(session.CanMovePrevious);

        session.SetPendingDelete([5], true);
        Assert.True(session.MoveLast());
        Assert.Equal(4, session.CurrentIndex); // 末尾は予約済みなので、その前
        Assert.True(session.MoveFirst());
        Assert.Equal(0, session.CurrentIndex);
        Assert.Equal(["002.jpg", "003.jpg", "006.jpg"], session.PendingDeleteEntries);
        Assert.Equal([1, 2, 5], session.PendingDeleteIndices);
    }

    [Fact]
    public async Task セッションは_削除予約を解除でき_直接の位置指定は予約済みの画像にも移動できる()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateArchiveAsync(ctx, 4);
        var result = await ctx.Viewer.OpenAsync(id, Ct);
        using var session = result.Session!;
        session.SetPendingDelete([1, 2, 3], true);
        Assert.False(session.CanMoveNext); // 後ろはすべて予約済み

        Assert.True(session.MoveTo(2)); // 一覧からの位置指定は可能
        Assert.True(session.IsPendingDelete(2));
        Assert.Equal(1, session.SetPendingDelete([2], false));
        Assert.False(session.IsPendingDelete(2));
        Assert.True(session.MovePrevious());
        Assert.Equal(0, session.CurrentIndex);
        Assert.True(session.MoveNext());
        Assert.Equal(2, session.CurrentIndex);

        session.ClearPendingDelete();
        Assert.Equal(0, session.PendingDeleteCount);
        Assert.Empty(session.PendingDeleteEntries);
    }

    // ---- 1枚表示（UC-08） ----

    [Fact]
    public async Task 一枚表示でDeleteを押すと_現在の画像を削除予約し_次の未削除の画像を表示する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 4);
        var before = Fingerprint(ctx.PathOf("book.zip"));
        await WaitUntilAsync(() => viewer.CurrentImage is not null);

        Assert.True(viewer.HandleKey(Key.Delete, ModifierKeys.None));

        Assert.Equal(1, viewer.PendingDeleteCount);
        Assert.Equal("削除予定 1枚", viewer.PendingDeleteText);
        Assert.True(viewer.Pages[0].IsPendingDelete);
        Assert.Equal("2 / 4", viewer.PositionText);
        Assert.False(viewer.IsCurrentPendingDelete);
        await WaitUntilAsync(() => viewer.CurrentImage is { PixelWidth: 120 });

        // 前へ戻っても、削除予約した画像は飛ばす
        Assert.False(viewer.PreviousCommand.CanExecute(null));
        Assert.Equal(before, Fingerprint(ctx.PathOf("book.zip"))); // ZIP本体は変更されない
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一枚表示で最後の画像を削除予約すると_前の未削除の画像を表示する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 3);
        viewer.LastCommand.Execute(null);
        await WaitUntilAsync(() => viewer.PositionText == "3 / 3");

        viewer.ToggleDeleteCommand.Execute(null);

        Assert.Equal("2 / 3", viewer.PositionText);
        Assert.True(viewer.Pages[2].IsPendingDelete);
        Assert.False(viewer.NextCommand.CanExecute(null));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一枚表示ですべての画像を削除予約すると_最後に残した画像を削除予定として表示する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 2);

        viewer.ToggleDeleteCommand.Execute(null); // 1枚目 → 2枚目へ
        Assert.Equal("2 / 2", viewer.PositionText);
        viewer.ToggleDeleteCommand.Execute(null); // 2枚目。移動先がない

        Assert.Equal("2 / 2", viewer.PositionText);
        Assert.True(viewer.IsCurrentPendingDelete);
        Assert.Equal("削除予定 2枚", viewer.PendingDeleteText);
        Assert.False(viewer.NextCommand.CanExecute(null));
        Assert.False(viewer.PreviousCommand.CanExecute(null));

        // もう一度Deleteを押すと解除される（トグル）
        viewer.ToggleDeleteCommand.Execute(null);
        Assert.False(viewer.IsCurrentPendingDelete);
        Assert.Equal("削除予定 1枚", viewer.PendingDeleteText);
        Assert.False(viewer.PreviousCommand.CanExecute(null)); // 1枚目はまだ予約済み
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 削除予約した画像は_先頭と末尾への移動や前後移動でも飛ばされる()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 5);
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.SelectRange(0, 1);
        viewer.HandleKey(Key.Delete, ModifierKeys.None); // 位置0, 1を予約
        viewer.OnPageClicked(4, ModifierKeys.None, 1); // 末尾を現在位置にする
        viewer.ToggleViewModeCommand.Execute(null);
        Assert.Equal("5 / 5", viewer.PositionText);

        viewer.FirstCommand.Execute(null);
        await WaitUntilAsync(() => viewer.PositionText == "3 / 5");

        viewer.LastCommand.Execute(null);
        await WaitUntilAsync(() => viewer.PositionText == "5 / 5");
        viewer.PreviousCommand.Execute(null);
        Assert.Equal("4 / 5", viewer.PositionText);
        viewer.PreviousCommand.Execute(null);
        Assert.Equal("3 / 5", viewer.PositionText);
        Assert.False(viewer.PreviousCommand.CanExecute(null)); // 1, 2枚目は予約済み
        await viewer.CloseAsync();
    }

    // ---- 一覧表示（UC-09、UC-10） ----

    [Fact]
    public async Task 一覧表示でDeleteを押すと_選択した複数の画像をまとめて削除予約する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 8);
        var before = Fingerprint(ctx.PathOf("book.zip"));
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.OnPageClicked(1, ModifierKeys.None, 1);
        viewer.OnPageClicked(3, ModifierKeys.Shift, 1);
        viewer.OnPageClicked(6, ModifierKeys.Control, 1);
        Assert.Equal("1,2,3,6", Positions(viewer.SelectedIndices));

        Assert.True(viewer.HandleKey(Key.Delete, ModifierKeys.None));

        Assert.Equal(4, viewer.PendingDeleteCount);
        Assert.Equal("削除予定 4枚", viewer.PendingDeleteText);
        Assert.Equal("1,2,3,6", Positions(viewer.Pages.Where(p => p.IsPendingDelete).Select(p => p.Index)));
        Assert.Equal("選択中、削除予定", viewer.Pages[2].StatusText);
        Assert.Equal("", viewer.Pages[0].StatusText);
        Assert.Equal("1,2,3,6", Positions(viewer.SelectedIndices)); // 選択は保たれる
        Assert.Equal("7 / 8", viewer.PositionText); // 一覧では現在位置は動かない
        Assert.Equal(before, Fingerprint(ctx.PathOf("book.zip")));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 一覧表示でDeleteを押すと_選択がすべて予約済みなら解除し_一部だけなら残りも予約する()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 6);
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.OnPageClicked(1, ModifierKeys.None, 1);
        viewer.OnPageClicked(2, ModifierKeys.Shift, 1);
        viewer.HandleKey(Key.Delete, ModifierKeys.None);
        Assert.Equal(2, viewer.PendingDeleteCount);

        // 予約済みの画像だけを選んで押すと解除（UC-10）
        viewer.HandleKey(Key.Delete, ModifierKeys.None);
        Assert.Equal(0, viewer.PendingDeleteCount);
        Assert.Equal(string.Empty, viewer.PendingDeleteText);
        Assert.DoesNotContain(viewer.Pages, p => p.IsPendingDelete);

        // 予約済みと未予約が混ざっていれば、全部を予約する
        viewer.SelectOnly(1);
        viewer.HandleKey(Key.Delete, ModifierKeys.None);
        viewer.SelectRange(1, 3);
        viewer.HandleKey(Key.Delete, ModifierKeys.None);
        Assert.Equal("1,2,3", Positions(viewer.Pages.Where(p => p.IsPendingDelete).Select(p => p.Index)));
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 右クリックメニューの操作で_削除予約と解除ができる_すでに選択中なら複数選択を保つ()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 6);
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.OnPageClicked(0, ModifierKeys.None, 1);
        viewer.OnPageClicked(2, ModifierKeys.Shift, 1); // 0〜2 を選択

        viewer.OnPageRightClicked(1); // 選択中の画像を右クリック → 選択は保つ
        Assert.Equal("0,1,2", Positions(viewer.SelectedIndices));
        viewer.ReserveDeleteCommand.Execute(null);
        Assert.Equal(3, viewer.PendingDeleteCount);

        viewer.OnPageRightClicked(4); // 選択外の画像を右クリック → その1枚だけを選択
        Assert.Equal("4", Positions(viewer.SelectedIndices));
        viewer.ReleaseDeleteCommand.Execute(null); // 予約されていない画像の解除は何も変えない
        Assert.Equal(3, viewer.PendingDeleteCount);

        viewer.OnPageRightClicked(2);
        viewer.ReleaseDeleteCommand.Execute(null);
        Assert.Equal(2, viewer.PendingDeleteCount);
        Assert.False(viewer.Pages[2].IsPendingDelete);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 削除予約は_表示モードを切り替えても共有され_一覧から予約済みの画像を1枚表示して解除できる()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 5);
        viewer.ToggleViewModeCommand.Execute(null);
        viewer.OnPageClicked(2, ModifierKeys.None, 1);
        viewer.HandleKey(Key.Delete, ModifierKeys.None);

        viewer.ToggleViewModeCommand.Execute(null); // 予約済みの3枚目を1枚表示へ
        await WaitUntilAsync(() => viewer.CurrentImage is { PixelWidth: 130 });

        Assert.Equal("3 / 5", viewer.PositionText);
        Assert.True(viewer.IsCurrentPendingDelete);
        Assert.Equal("削除予定 1枚", viewer.PendingDeleteText); // 件数は共通

        viewer.HandleKey(Key.Delete, ModifierKeys.None); // 1枚表示のDeleteで解除（その場に留まる）
        Assert.Equal("3 / 5", viewer.PositionText);
        Assert.False(viewer.IsCurrentPendingDelete);
        Assert.Equal(0, viewer.PendingDeleteCount);
        Assert.False(viewer.Pages[2].IsPendingDelete);
        await viewer.CloseAsync();
    }

    [Fact]
    public async Task 閉じると削除予約は破棄され_開き直しても残らない()
    {
        using var ctx = new LibraryTestContext();
        var id = await CreateArchiveAsync(ctx, 3);
        var viewer = CreateViewer(ctx);
        Assert.True(await viewer.OpenAsync(id, Ct));
        viewer.ToggleDeleteCommand.Execute(null);
        Assert.Equal(1, viewer.PendingDeleteCount);

        await viewer.CloseAsync();

        Assert.Equal(0, viewer.PendingDeleteCount);
        Assert.Equal(string.Empty, viewer.PendingDeleteText);
        Assert.True(await viewer.OpenAsync(id, Ct));
        Assert.Equal(0, viewer.PendingDeleteCount);
        Assert.DoesNotContain(viewer.Pages, p => p.IsPendingDelete);
        await viewer.CloseAsync();
        viewer.Dispose();
    }

    [Fact]
    public async Task 開いていないときのDeleteは_何もしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewer = CreateViewer(ctx);

        Assert.False(viewer.HandleKey(Key.Delete, ModifierKeys.None));
        viewer.ToggleDeleteCommand.Execute(null);
        viewer.ReserveDeleteCommand.Execute(null);
        viewer.ReleaseDeleteCommand.Execute(null);

        Assert.Equal(0, viewer.PendingDeleteCount);
    }

    // ---- 離れるときの確認（DLG-01、UC-11） ----

    private static async Task<MainTestHarness> OpenInMainAsync(LibraryTestContext ctx)
    {
        var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)), ("3.jpg", TestImages.Jpeg(120, 80)));
        await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives[0]);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        return harness;
    }

    [Fact]
    public async Task 削除予約がなければ_確認せずにライブラリへ戻る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);

        Assert.Empty(harness.Dialogs.LeaveConfirmations);
        Assert.True(harness.Main.IsLibraryVisible);
    }

    [Fact]
    public async Task 削除予約があるときの戻る操作は_確認しキャンセルならビューアに留まる_ZIPは変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        var before = Fingerprint(ctx.PathOf("book.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Cancel;

        harness.Viewer.RequestCloseCommand.Execute(null);
        await Task.Delay(200, Ct);

        var confirmation = Assert.Single(harness.Dialogs.LeaveConfirmations);
        Assert.Equal(("book.zip", 2, (string?)null), confirmation); // 更新できる状態（理由なし）で確認する
        Assert.True(harness.Main.IsViewerVisible);
        Assert.True(harness.Viewer.IsOpen);
        Assert.Equal(2, harness.Viewer.PendingDeleteCount); // 予約は保持される
        Assert.Equal(before, Fingerprint(ctx.PathOf("book.zip")));
    }

    [Fact]
    public async Task 削除予約があるときに_変更を破棄すると_ライブラリへ戻り_予約は消えZIPは変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        var before = Fingerprint(ctx.PathOf("book.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Discard;

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);

        Assert.Single(harness.Dialogs.LeaveConfirmations);
        Assert.True(harness.Main.IsLibraryVisible);
        Assert.Equal(0, harness.Viewer.PendingDeleteCount);
        Assert.Equal(before, Fingerprint(ctx.PathOf("book.zip")));
        Assert.Equal(3, await ctx.ScalarAsync("SELECT image_count FROM archives")); // DBの画像枚数も変わらない

        // 開き直すと、予約は残っていない
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives[0]);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        Assert.Equal(0, harness.Viewer.PendingDeleteCount);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task アプリを終了するときも_削除予約があれば確認し_キャンセルなら終了しない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        Assert.Equal(LeaveDecision.Proceed, harness.Main.DecideLeave());
        Assert.Empty(harness.Dialogs.LeaveConfirmations);
        harness.Viewer.ToggleDeleteCommand.Execute(null);

        harness.Dialogs.Choice = LeaveChoice.Cancel;
        Assert.Equal(LeaveDecision.Stay, harness.Main.DecideLeave());
        harness.Dialogs.Choice = LeaveChoice.Discard;
        Assert.Equal(LeaveDecision.Proceed, harness.Main.DecideLeave());

        Assert.Equal(2, harness.Dialogs.LeaveConfirmations.Count);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task メインウィンドウを閉じようとすると_削除予約があれば確認し_キャンセルなら閉じない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        bool stillOpenAfterCancel = false, closedAfterDiscard = false, deleteHandled = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new MainWindow(harness.Main));
            window.Show();
            WpfTestHelper.Pump();
            var source = PresentationSource.FromVisual(window)!;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(args);
            deleteHandled = args.Handled; // ビューアの表示中は、Delete がビューアに渡る

            harness.Dialogs.Choice = LeaveChoice.Cancel;
            window.Close();
            stillOpenAfterCancel = window.IsVisible;

            harness.Dialogs.Choice = LeaveChoice.Discard;
            window.Close();
            closedAfterDiscard = !window.IsVisible;
        });

        Assert.True(deleteHandled);
        Assert.True(stillOpenAfterCancel);
        Assert.True(closedAfterDiscard);
        await harness.Viewer.CloseAsync();
    }

    // ---- WPFの画面 ----

    [Fact]
    public async Task 一覧表示では_削除予約した画像に削除予定と表示され_件数も表示される()
    {
        using var ctx = new LibraryTestContext();
        using var viewer = await OpenAsync(ctx, 4);
        var reservedLabelsVisible = new List<int>();
        var countTextVisible = string.Empty;
        var singleBadgeVisible = false;
        var contextMenuItems = new List<string>();

        WpfTestHelper.RunOnStaThread(() =>
        {
            var view = new ViewerView { DataContext = viewer };
            var window = WpfTestHelper.WithAppTheme(new Window { Width = 1280, Height = 800, Content = view });
            window.Show();
            WpfTestHelper.Pump();
            viewer.ToggleViewModeCommand.Execute(null);
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();
            viewer.OnPageClicked(1, ModifierKeys.None, 1);
            viewer.HandleKey(Key.Delete, ModifierKeys.None);
            WpfTestHelper.Pump();

            var cells = WpfTestHelper.Descendants<Border>(view).Where(b => b.DataContext is PageItem && b.Width == 164).ToList();
            foreach (var cell in cells)
            {
                var label = WpfTestHelper.Descendants<TextBlock>(cell).First(t => t.Text == "削除予定");
                if (label.IsVisible)
                {
                    reservedLabelsVisible.Add(((PageItem)cell.DataContext).Index);
                }
            }

            countTextVisible = string.Join(
                "|",
                WpfTestHelper.Descendants<TextBlock>(view).Where(t => t.Text == "削除予定 1枚" && t.IsVisible).Select(t => t.Text));
            contextMenuItems.AddRange(cells[0].ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header));

            // 予約済みの画像を1枚表示すると、削除予定の帯が出る
            viewer.OnPageClicked(1, ModifierKeys.None, 2);
            WpfTestHelper.Pump();
            WpfTestHelper.Pump();
            singleBadgeVisible = WpfTestHelper.Descendants<TextBlock>(view).Any(t => t.Text.StartsWith("削除予定（", StringComparison.Ordinal) && t.IsVisible);
            window.Close();
        });

        Assert.Equal([1], reservedLabelsVisible);
        Assert.Equal("削除予定 1枚", countTextVisible);
        Assert.Equal(["削除予約", "削除予約を解除"], contextMenuItems);
        Assert.True(singleBadgeVisible, "予約済みの画像を1枚表示したときに、削除予定の表示が出ていません。");
        await viewer.CloseAsync();
    }

    [Fact]
    public void 変更確認ダイアログは_キャンセルが既定で_更新できない間は更新するを選べない()
    {
        bool updateEnabledWhenUnavailable = true, updateEnabledWhenAvailable = false, cancelIsDefault = false;
        string message = string.Empty;
        LeaveChoice choiceWhenClosed = LeaveChoice.Update;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var unavailable = new ArchiveChangeDialog("ABC.zip", 5, "アーカイブが外部で変更されたため、更新できません");
            updateEnabledWhenUnavailable = ((Button)unavailable.FindName("UpdateButton")).IsEnabled;
            cancelIsDefault = ((Button)unavailable.FindName("CancelButton")).IsDefault;
            message = ((TextBlock)unavailable.FindName("MessageText")).Text;
            choiceWhenClosed = unavailable.Choice;

            var available = new ArchiveChangeDialog("ABC.zip", 5, null);
            updateEnabledWhenAvailable = ((Button)available.FindName("UpdateButton")).IsEnabled;
        });

        Assert.False(updateEnabledWhenUnavailable);
        Assert.True(updateEnabledWhenAvailable);
        Assert.True(cancelIsDefault);
        Assert.Contains("ABC.zip で5枚の画像が削除対象になっています。", message, StringComparison.Ordinal);
        Assert.Equal(LeaveChoice.Cancel, choiceWhenClosed); // 何も選ばずに閉じた場合はキャンセル
    }
}
