using K35Viewer.Domain;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

public class LibraryViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeDialogService(string? pickedFolder, bool confirmMerge) : IDialogService
    {
        public int ConfirmMergeCalls { get; private set; }

        public string? PickFolder() => pickedFolder;

        public bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths)
        {
            ConfirmMergeCalls++;
            return confirmMerge;
        }

        public LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, string? updateUnavailableReason) => LeaveChoice.Cancel;

        public string? PromptRename(string currentFileName, Func<string, string?> validate) => null;

        public string? PickMoveDestination(string initialDirectory) => null;

        public bool ConfirmDeleteArchive(string fileName) => false;

        public bool ConfirmPermanentDelete(string fileName, string reason) => false;

        public bool ConfirmRemoveLibraryFolder(string folderPath, int archiveCount) => false;
    }

    private static LibraryViewModel CreateViewModel(LibraryTestContext ctx, FakeDialogService? dialogs = null) =>
        new(
            ctx.Library,
            ctx.Scan,
            ctx.Thumbnails,
            ctx.ImageLoader,
            dialogs ?? new FakeDialogService(null, confirmMerge: true),
            ctx.Archives,
            NullLogger<LibraryViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>
    /// テストの後始末(一時フォルダの削除)の前に、バックグラウンドのスキャンとサムネイル作成が終わるのを待つ。
    /// スキャンの終了直後にサムネイル作成が始まるため、「どちらも動いていない」状態が一定時間続くことを確認する。
    /// </summary>
    private static async Task WaitForIdleAsync(LibraryViewModel viewModel)
    {
        var idleSince = DateTime.MaxValue;
        await WaitUntilAsync(() =>
        {
            if (viewModel.IsScanning || viewModel.IsGeneratingThumbnails)
            {
                idleSince = DateTime.MaxValue;
                return false;
            }

            if (idleSince == DateTime.MaxValue)
            {
                idleSince = DateTime.UtcNow;
            }

            return DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(250);
        });
    }

    /// <summary>管理フォルダを登録してスキャン済みの状態を作る。</summary>
    private static async Task<long> RegisterAndScanAsync(LibraryTestContext ctx, string? relative = null)
    {
        var result = await ctx.Library.AddFolderAsync(relative is null ? ctx.Workspace : ctx.PathOf(relative), false, Ct);
        await ctx.Scan.ScanAsync(result.LibraryFolderId!.Value, ScanType.Initial, Ct);
        return result.LibraryFolderId.Value;
    }

    private static FolderTreeItem? FindByHeader(IEnumerable<FolderTreeItem> items, string header)
    {
        foreach (var item in items)
        {
            if (item.Header == header)
            {
                return item;
            }

            if (FindByHeader(item.Children, header) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public async Task InitializeAsync_登録済みフォルダのツリーと一覧を読み込む()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        ctx.CreateFile(@"sub\b.zip");
        ctx.CreateFile(@"sub\c.cbz");
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasFolders);
        Assert.Equal(2, viewModel.FolderRoots.Count); // 「すべて」と登録フォルダ
        Assert.Equal("すべて", viewModel.FolderRoots[0].Header);
        Assert.True(viewModel.FolderRoots[0].IsSelected);
        Assert.Equal(ctx.Workspace, viewModel.FolderRoots[1].Header);
        Assert.True(viewModel.FolderRoots[1].IsExpanded);
        Assert.Equal(3, viewModel.Archives.Count);
        Assert.Equal(3, viewModel.TotalCount);
        Assert.Equal(string.Empty, viewModel.EmptyMessage);
        await WaitForIdleAsync(viewModel);
    }

    [Fact]
    public async Task InitializeAsync_管理フォルダがなければ案内を表示し_スキャンも再スキャンもしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = CreateViewModel(ctx);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasFolders);
        Assert.Contains("管理フォルダがありません", viewModel.EmptyMessage, StringComparison.Ordinal);
        Assert.False(viewModel.RescanCommand.CanExecute(null));
        Assert.False(viewModel.IsScanning);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs"));
        Assert.Empty(ctx.Notifications.Items);
    }

    [Fact]
    public async Task InitializeAsync_起動時チェックで外部の追加を取り込み_一覧に反映する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        await RegisterAndScanAsync(ctx);
        ctx.CreateFile("added-later.zip"); // アプリ外でのファイル追加
        ctx.Notifications.Items.Clear();
        using var viewModel = CreateViewModel(ctx);

        await viewModel.InitializeAsync();
        Assert.Single(viewModel.Archives); // 表示は待たされず、先に既存の内容が出る

        await WaitUntilAsync(() => !viewModel.IsScanning && viewModel.Archives.Count == 2);
        await WaitForIdleAsync(viewModel);
        Assert.Equal("ライブラリの更新を確認しています", ctx.Notifications.Items[0].Message);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'SCAN_STARTUP'"));
    }

    [Fact]
    public async Task OnFolderSelected_選択したフォルダと配下だけに絞り込む()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("top.zip");
        ctx.CreateFile(@"sub\s1.zip");
        ctx.CreateFile(@"sub\deep\d1.zip");
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitForIdleAsync(viewModel);

        viewModel.OnFolderSelected(FindByHeader(viewModel.FolderRoots, "sub")!);
        await WaitUntilAsync(() => viewModel.Archives.Count == 2);
        Assert.Equal(["d1.zip", "s1.zip"], viewModel.Archives.Select(a => a.FileName).ToArray());

        viewModel.OnFolderSelected(FindByHeader(viewModel.FolderRoots, "deep")!);
        await WaitUntilAsync(() => viewModel.Archives.Count == 1);

        viewModel.OnFolderSelected(viewModel.FolderRoots[0]); // すべて
        await WaitUntilAsync(() => viewModel.Archives.Count == 3);
    }

    [Fact]
    public async Task SearchText_入力するとファイル名で絞り込み_消すと戻る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("Alpha.zip");
        ctx.CreateFile("beta.zip");
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitForIdleAsync(viewModel);

        viewModel.SearchText = "ALP";
        await WaitUntilAsync(() => viewModel.Archives.Count == 1);
        Assert.Equal("Alpha.zip", viewModel.Archives[0].FileName);

        viewModel.SearchText = "zzz";
        await WaitUntilAsync(() => viewModel.Archives.Count == 0);
        Assert.Equal("該当するアーカイブがありません", viewModel.EmptyMessage);

        viewModel.SearchText = string.Empty;
        await WaitUntilAsync(() => viewModel.Archives.Count == 2);
        Assert.Equal(string.Empty, viewModel.EmptyMessage);
    }

    [Fact]
    public async Task 並べ替え項目と降順を切り替えると一覧の順序が変わる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("small.zip", size: 1);
        ctx.CreateFile("medium.zip", size: 50);
        ctx.CreateFile("large.zip", size: 900);
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitForIdleAsync(viewModel);
        Assert.Equal(["large.zip", "medium.zip", "small.zip"], viewModel.Archives.Select(a => a.FileName).ToArray());

        viewModel.SelectedSortOption = viewModel.SortOptions.Single(o => o.Key == ArchiveSortKey.FileSize);
        await WaitUntilAsync(() => viewModel.Archives[0].FileName == "small.zip");

        viewModel.IsDescending = true;
        await WaitUntilAsync(() => viewModel.Archives[0].FileName == "large.zip");
        Assert.Equal(["large.zip", "medium.zip", "small.zip"], viewModel.Archives.Select(a => a.FileName).ToArray());
    }

    [Fact]
    public async Task RescanCommand_外部の変更を取り込む()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitForIdleAsync(viewModel);
        Assert.True(viewModel.RescanCommand.CanExecute(null));
        ctx.CreateFile("b.zip");

        await viewModel.RescanCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => viewModel.Archives.Count == 2);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'SCAN_MANUAL'"));
        Assert.True(viewModel.RescanCommand.CanExecute(null));
        await WaitForIdleAsync(viewModel);
    }

    [Fact]
    public async Task InitializeAsync_未作成のサムネイルを背景で作り_一覧に反映する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 4; i++)
        {
            ctx.CreateZip($"book{i}.zip", ("1.jpg", TestImages.Jpeg(600, 400)), ("2.jpg", TestImages.Jpeg(60, 40)));
        }

        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);

        await viewModel.InitializeAsync();
        Assert.All(viewModel.Archives, item => Assert.Equal("作成待ち", item.PlaceholderText)); // 作成前は枠だけ

        await WaitUntilAsync(() => viewModel.Archives.All(item => item.ThumbnailPath is not null));
        await WaitForIdleAsync(viewModel);

        Assert.Equal(4, viewModel.Archives.Count);
        Assert.All(viewModel.Archives, item =>
        {
            Assert.Equal(string.Empty, item.PlaceholderText);
            Assert.Equal("2", item.ImageCountText);
        });
        Assert.Equal(string.Empty, viewModel.ThumbnailProgressText);
        Assert.False(viewModel.IsGeneratingThumbnails);
    }

    [Fact]
    public async Task 作成完了の通知を受けると_表示中の行を取得し直さずにその場で更新する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync(); // 管理フォルダがないので、スキャンも作成も始まらない
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(600, 400)), ("2.jpg", TestImages.Jpeg(60, 40)));
        ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        await RegisterAndScanAsync(ctx);
        await viewModel.ReloadAsync();
        var itemsBefore = viewModel.Archives.ToList();
        Assert.Equal(2, itemsBefore.Count);
        Assert.All(itemsBefore, item => Assert.Null(item.ThumbnailPath));
        var changedProperties = new List<string?>();
        itemsBefore[0].PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct); // ViewModelを介さずに作成する

        Assert.Equal(itemsBefore, viewModel.Archives); // 一覧は取得し直されていない
        Assert.All(itemsBefore, item => Assert.NotNull(item.ThumbnailPath));
        Assert.Equal("2", itemsBefore[0].ImageCountText);
        Assert.Equal("1", itemsBefore[1].ImageCountText);
        Assert.Equal(string.Empty, itemsBefore[0].PlaceholderText);
        Assert.Contains(nameof(ArchiveListItem.Thumbnail), changedProperties);
        Assert.Contains(nameof(ArchiveListItem.ImageCountText), changedProperties);
    }

    [Fact]
    public async Task 一覧に表示されていないアーカイブの作成完了通知は_無視する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        ctx.CreateZip("shown.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        ctx.CreateZip("hidden.zip", ("1.jpg", TestImages.Jpeg(100, 100)));
        await RegisterAndScanAsync(ctx);
        viewModel.SearchText = "shown";
        await viewModel.ReloadAsync();
        Assert.Single(viewModel.Archives);

        var exception = await Record.ExceptionAsync(() => ctx.Thumbnails.ProcessPendingAsync(ct: Ct));

        Assert.Null(exception);
        Assert.NotNull(viewModel.Archives[0].ThumbnailPath);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
    }

    [Fact]
    public async Task ArchiveListItem_Thumbnail_画面に出て初めて読み込み_読み込めたら変更を通知する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(600, 400)));
        await RegisterAndScanAsync(ctx);
        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var summary = (await ctx.Library.QueryArchivesAsync(new ArchiveQuery(), Ct)).Items.Single();
        var item = new ArchiveListItem(summary, ctx.ImageLoader);
        var changed = new TaskCompletionSource();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchiveListItem.Thumbnail))
            {
                changed.TrySetResult();
            }
        };
        Assert.Equal(0, ctx.ImageLoader.CachedCount); // 画面に出る(バインドされる)までは読み込まれていない

        Assert.Null(item.Thumbnail); // 最初の参照で背景読み込みが始まる
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var image = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(item.Thumbnail);
        Assert.Equal((300, 200), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(1, ctx.ImageLoader.CachedCount);
    }

    [Fact]
    public async Task 壊れたアーカイブや画像のないアーカイブは_一覧上で識別できる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("broken.zip", size: 50);
        ctx.CreateZip("docs.zip", ("a.txt", [1]));
        ctx.CreateZip("good.zip", ("1.jpg", TestImages.Jpeg(100, 100)), ("2.jpg", TestImages.Jpeg(100, 100)));
        TestZips.CreateEncryptedLooking(ctx.PathOf("secret.zip"), ("1.jpg", TestImages.Jpeg(100, 100)));
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Archives.All(item => item.PlaceholderText != "作成待ち"));
        await WaitForIdleAsync(viewModel);

        var byName = viewModel.Archives.ToDictionary(item => item.FileName);
        Assert.Equal("破損", byName["broken.zip"].ImageCountText);
        Assert.Contains("破損", byName["broken.zip"].PlaceholderText, StringComparison.Ordinal);
        Assert.Equal("0", byName["docs.zip"].ImageCountText);
        Assert.Equal("画像なし", byName["docs.zip"].PlaceholderText);
        Assert.Equal("非対応", byName["secret.zip"].ImageCountText);
        Assert.Contains("暗号化", byName["secret.zip"].PlaceholderText, StringComparison.Ordinal);
        Assert.Equal("2", byName["good.zip"].ImageCountText);
        Assert.Equal(string.Empty, byName["good.zip"].PlaceholderText);
    }

    [Fact]
    public async Task サムネイル表示では_表示領域の幅に合わせて1行に並べる枚数が決まる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 0; i < 7; i++)
        {
            ctx.CreateFile($"book{i}.zip", size: 1);
        }

        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitForIdleAsync(viewModel);
        Assert.Empty(viewModel.ThumbnailRows); // 詳細表示の間は行を作らない
        Assert.True(viewModel.IsDetailView);

        viewModel.UpdateViewportWidth((LibraryViewModel.ThumbnailCellWidth * 3) + 10);
        viewModel.IsThumbnailView = true;

        Assert.False(viewModel.IsDetailView);
        Assert.Equal(3, viewModel.ColumnCount);
        Assert.Equal([3, 3, 1], viewModel.ThumbnailRows.Select(r => r.Items.Count));
        Assert.Equal(
            viewModel.Archives.Select(a => a.Id),
            viewModel.ThumbnailRows.SelectMany(r => r.Items).Select(a => a.Id)); // 並び順が保たれる

        viewModel.UpdateViewportWidth(LibraryViewModel.ThumbnailCellWidth * 4);
        Assert.Equal([4, 3], viewModel.ThumbnailRows.Select(r => r.Items.Count));

        viewModel.UpdateViewportWidth(10); // 狭くても最低1枚
        Assert.Equal(1, viewModel.ColumnCount);
        Assert.Equal(7, viewModel.ThumbnailRows.Count);

        viewModel.IsDetailView = true; // 詳細表示に戻すと破棄される
        Assert.False(viewModel.IsThumbnailView);
        Assert.Empty(viewModel.ThumbnailRows);
    }

    [Fact]
    public async Task サムネイル表示でも_検索と並べ替えが行に反映される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("apple.zip", size: 5);
        ctx.CreateFile("apricot.zip", size: 50);
        ctx.CreateFile("banana.zip", size: 500);
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitForIdleAsync(viewModel);
        viewModel.UpdateViewportWidth(LibraryViewModel.ThumbnailCellWidth * 2);
        viewModel.IsThumbnailView = true;

        viewModel.SearchText = "ap";
        await WaitUntilAsync(() => viewModel.Archives.Count == 2);
        Assert.Equal([2], viewModel.ThumbnailRows.Select(r => r.Items.Count));

        viewModel.SelectedSortOption = viewModel.SortOptions.Single(o => o.Key == ArchiveSortKey.FileSize);
        viewModel.IsDescending = true;
        await WaitUntilAsync(() => viewModel.ThumbnailRows.Count == 1 && viewModel.ThumbnailRows[0].Items[0].FileName == "apricot.zip");
    }

    [Fact]
    public async Task 再スキャンで内容が変わったアーカイブは_サムネイルが作り直されて表示が更新される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(600, 400)));
        await RegisterAndScanAsync(ctx);
        using var viewModel = CreateViewModel(ctx);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Archives.All(item => item.ThumbnailPath is not null));
        await WaitForIdleAsync(viewModel);
        Assert.Equal("1", viewModel.Archives[0].ImageCountText);

        ctx.CreateZip("book.zip", ("1.jpg", TestImages.Jpeg(600, 400)), ("2.jpg", TestImages.Jpeg(60, 40)), ("3.jpg", TestImages.Jpeg(60, 40)));
        await viewModel.RescanCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Archives.Count == 1 && viewModel.Archives[0].ImageCountText == "3");
        await WaitForIdleAsync(viewModel);

        Assert.Equal("3", viewModel.Archives[0].ImageCountText);
        Assert.NotNull(viewModel.Archives[0].ThumbnailPath);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
    }

    [Fact]
    public async Task AddFolderCommand_フォルダを登録して初回スキャンを開始し_ツリーと一覧に反映する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        ctx.CreateFile(@"sub\b.cbz");
        using var viewModel = CreateViewModel(ctx, new FakeDialogService(ctx.Workspace, confirmMerge: true));
        await viewModel.InitializeAsync();
        Assert.False(viewModel.HasFolders);

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasFolders);
        Assert.Equal(2, viewModel.FolderRoots.Count);
        await WaitUntilAsync(() => !viewModel.IsScanning && viewModel.Archives.Count == 2);
        await WaitForIdleAsync(viewModel);
        Assert.Equal("2 件", viewModel.CountText);
    }

    [Fact]
    public async Task AddFolderCommand_フォルダ選択をキャンセルしたら何もしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = CreateViewModel(ctx, new FakeDialogService(null, confirmMerge: true));
        await viewModel.InitializeAsync();

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasFolders);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Empty(ctx.Notifications.Items);
    }

    [Fact]
    public async Task AddFolderCommand_統合の確認で拒否したら登録を変更しない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"parent\child\a.zip");
        await ctx.Library.AddFolderAsync(ctx.PathOf(@"parent\child"), false, Ct);
        var dialogs = new FakeDialogService(ctx.PathOf("parent"), confirmMerge: false);
        using var viewModel = CreateViewModel(ctx, dialogs);

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmMergeCalls);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM scan_runs"));
    }
}
