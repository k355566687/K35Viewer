using K35Viewer.Domain;
using K35Viewer.Services;
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
    }

    private static LibraryViewModel CreateViewModel(LibraryTestContext ctx, FakeDialogService? dialogs = null) =>
        new(ctx.Library, ctx.Scan, dialogs ?? new FakeDialogService(null, confirmMerge: true), NullLogger<LibraryViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>テストの後始末(一時フォルダの削除)の前に、バックグラウンドのスキャンが終わるのを待つ。</summary>
    private static Task WaitForIdleAsync(LibraryViewModel viewModel) => WaitUntilAsync(() => !viewModel.IsScanning);

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
