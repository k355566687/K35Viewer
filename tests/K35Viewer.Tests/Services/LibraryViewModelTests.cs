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

    private static LibraryViewModel CreateViewModel(LibraryTestContext ctx, FakeDialogService dialogs) =>
        new(ctx.Library, ctx.Scan, dialogs, NullLogger<LibraryViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task AddFolderCommand_フォルダを登録して初回スキャンを開始し_件数つきで一覧に反映する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("a.zip");
        ctx.CreateFile(@"sub\b.cbz");
        using var viewModel = CreateViewModel(ctx, new FakeDialogService(ctx.Workspace, confirmMerge: true));
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasFolders);

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasFolders);
        Assert.Single(viewModel.Folders);
        await WaitUntilAsync(() => !viewModel.IsScanning && viewModel.Folders[0].Description.StartsWith('2'));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
    }

    [Fact]
    public async Task AddFolderCommand_フォルダ選択をキャンセルしたら何もしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = CreateViewModel(ctx, new FakeDialogService(null, confirmMerge: true));

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Folders);
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
