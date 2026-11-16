using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

internal sealed class MainTestHarness : IDisposable
{
    private readonly LibraryTestContext _ctx;

    public MainTestHarness(LibraryTestContext ctx, ArchiveService? archives = null, SettingsService? settings = null)
    {
        _ctx = ctx;
        Notifications = new NotificationService();
        Dialogs = new ScriptedDialogService();
        Library = new LibraryViewModel(
            ctx.Library, ctx.Scan, ctx.Thumbnails, ctx.ImageLoader, Dialogs, ctx.Archives, NullLogger<LibraryViewModel>.Instance, settings);
        Viewer = new ViewerViewModel(ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance);
        Toasts = new ToastHostViewModel(Notifications);
        Main = new MainViewModel(Library, Viewer, Toasts, Notifications, Dialogs, archives ?? ctx.Archives, NullLogger<MainViewModel>.Instance, settings);
    }

    public NotificationService Notifications { get; }

    /// <summary>確認ダイアログの代わり。呼ばれた内容を記録し、あらかじめ決めた選択を返す。</summary>
    public ScriptedDialogService Dialogs { get; }

    public LibraryViewModel Library { get; }

    public ViewerViewModel Viewer { get; }

    public ToastHostViewModel Toasts { get; }

    public MainViewModel Main { get; }

    /// <summary>バックグラウンドのスキャンとサムネイル作成が終わり、その状態がしばらく続くまで待つ。</summary>
    public async Task WaitForIdleAsync()
    {
        var idleSince = DateTime.MaxValue;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if (Library.IsScanning || Library.IsGeneratingThumbnails)
            {
                idleSince = DateTime.MaxValue;
            }
            else if (idleSince == DateTime.MaxValue)
            {
                idleSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "バックグラウンド処理が終わりませんでした。");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public void Dispose()
    {
        // 操作のあとに始まったサムネイルの作成などが、一時DBを使ったまま後始末へ進まないよう、終わるのを待つ。
        // 待ちきれなかった場合も、後始末は続ける（テスト自体の結果は、各テストの検証で決まる）
        try
        {
            Task.Run(WaitForIdleAsync).Wait(TimeSpan.FromSeconds(25));
        }
        catch (AggregateException)
        {
            // テストのキャンセルなど
        }

        Viewer.Dispose();
        Library.Dispose();
        Toasts.Dispose();
        _ = _ctx;
    }

    internal sealed class ScriptedDialogService : IDialogService
    {
        /// <summary>変更確認ダイアログで返す選択。</summary>
        public LeaveChoice Choice { get; set; } = LeaveChoice.Cancel;

        public List<(string ArchiveName, int Count, string? UpdateUnavailableReason)> LeaveConfirmations { get; } = [];

        public string? PickFolder() => null;

        public bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths) => false;

        public LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, string? updateUnavailableReason)
        {
            LeaveConfirmations.Add((archiveName, pendingDeleteCount, updateUnavailableReason));
            return Choice;
        }

        // ---- ライブラリ画面の操作（名前変更・移動・削除・管理フォルダの解除） ----

        /// <summary>名前変更の入力として返す文字列。null ならキャンセル。</summary>
        public string? RenameInput { get; set; }

        /// <summary>名前変更ダイアログに表示される、入力の問題の説明（直近の入力のもの）。</summary>
        public string? LastRenameError { get; private set; }

        public List<string> RenamePrompts { get; } = [];

        /// <summary>移動先として返すフォルダ。null ならキャンセル。</summary>
        public string? MoveDestination { get; set; }

        public bool DeleteConfirmed { get; set; }

        public bool PermanentDeleteConfirmed { get; set; }

        public bool RemoveFolderConfirmed { get; set; }

        public List<string> DeleteConfirmations { get; } = [];

        public List<(string FileName, string Reason)> PermanentDeleteConfirmations { get; } = [];

        public List<(string Path, int Count)> RemoveFolderConfirmations { get; } = [];

        public string? PromptRename(string currentFileName, Func<string, string?> validate)
        {
            RenamePrompts.Add(currentFileName);
            if (RenameInput is not null)
            {
                LastRenameError = validate(RenameInput);
            }

            return RenameInput;
        }

        public string? PickMoveDestination(string initialDirectory) => MoveDestination;

        public bool ConfirmDeleteArchive(string fileName)
        {
            DeleteConfirmations.Add(fileName);
            return DeleteConfirmed;
        }

        public bool ConfirmPermanentDelete(string fileName, string reason)
        {
            PermanentDeleteConfirmations.Add((fileName, reason));
            return PermanentDeleteConfirmed;
        }

        public bool ConfirmRemoveLibraryFolder(string folderPath, int archiveCount)
        {
            RemoveFolderConfirmations.Add((folderPath, archiveCount));
            return RemoveFolderConfirmed;
        }

        public bool ConvertConfirmed { get; set; }

        public List<(string FileName, string NewFileName, string? KeepOriginalReason)> ConvertConfirmations { get; } = [];

        public bool ConfirmConvertToZip(string fileName, string newFileName, string? keepOriginalReason)
        {
            ConvertConfirmations.Add((fileName, newFileName, keepOriginalReason));
            return ConvertConfirmed;
        }

        public void ShowSettings()
        {
        }
    }
}

public class MainViewModelTests
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

    /// <summary>登録・スキャン・サムネイル作成まで済んだ、3頁のアーカイブがある状態を作り、一覧の先頭の行を返す。</summary>
    private static async Task<ArchiveListItem> PrepareAsync(LibraryTestContext ctx, MainTestHarness harness)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)), ("3.jpg", TestImages.Jpeg(120, 80)));
        await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        return Assert.Single(harness.Library.Archives);
    }

    [Fact]
    public async Task 一覧で開く操作をすると_ビューアに切り替わり_ウィンドウのタイトルにアーカイブ名が出る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        Assert.True(harness.Main.IsLibraryVisible);
        Assert.Equal("35Viewer", harness.Main.Title);

        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);

        Assert.False(harness.Main.IsLibraryVisible);
        Assert.True(harness.Viewer.IsOpen);
        Assert.Equal("book.zip - 35Viewer", harness.Main.Title);
        Assert.Equal("1 / 3", harness.Viewer.PositionText);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 開けなかった場合は_ライブラリ画面に留まり_理由が通知される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        File.Delete(ctx.PathOf("book.zip"));
        var shown = new List<string>();
        harness.Notifications.Notified += (_, n) => shown.Add(n.Message);
        ctx.Notifications.Items.Clear();

        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => ctx.Notifications.Items.Count > 0);
        await Task.Delay(200, Ct);

        Assert.True(harness.Main.IsLibraryVisible);
        Assert.False(harness.Viewer.IsOpen);
        Assert.Equal("35Viewer", harness.Main.Title);
        Assert.Contains("再スキャン", ctx.Notifications.Items[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ビューアで戻る操作をすると_ライブラリ画面に戻り_閲覧位置が保存される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        harness.Viewer.NextCommand.Execute(null);
        harness.Viewer.NextCommand.Execute(null);

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);

        Assert.True(harness.Main.IsLibraryVisible);
        Assert.False(harness.Main.IsViewerVisible);
        Assert.Equal("35Viewer", harness.Main.Title);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE last_view_index = 2"));

        // 同じアーカイブを開き直すと、前回の位置から始まる
        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        Assert.Equal("3 / 3", harness.Viewer.PositionText);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task ビューアを開いている間は_別の開く操作を受け付けない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        var opens = ctx.Reader.OpenCalls;

        harness.Library.OpenArchiveCommand.Execute(item);
        await Task.Delay(300, Ct);

        Assert.Equal(opens, ctx.Reader.OpenCalls);
        Assert.True(harness.Main.IsViewerVisible);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task OpenArchiveCommand_行を渡すと開く要求を出し_nullなら何もしない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        var item = await PrepareAsync(ctx, harness);
        var requested = new List<long>();
        harness.Library.OpenRequested += (_, id) => requested.Add(id);

        harness.Library.OpenArchiveCommand.Execute(null);
        Assert.Empty(requested);

        harness.Library.OpenArchiveCommand.Execute(item);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        Assert.Equal([item.Id], requested);
        await harness.Viewer.CloseAsync();
    }
}

public class ToastHostViewModelTests
{
    [Fact]
    public async Task 通知は表示され_一定時間で消え_表示数には上限がある()
    {
        var notifications = new NotificationService();
        using var toasts = new ToastHostViewModel(notifications, TimeSpan.FromSeconds(1));

        notifications.ShowSuccess("A");
        notifications.ShowWarning("B");
        Assert.Equal(["A", "B"], toasts.Items.Select(t => t.Message));
        Assert.Equal([NotificationLevel.Success, NotificationLevel.Warning], toasts.Items.Select(t => t.Level));
        Assert.Equal(["✓", "⚠"], toasts.Items.Select(t => t.Symbol));

        // 表示時間は短すぎると、負荷の高い CI では最初の確認より先に消えてしまう。消えるのは、期限つきで待つ
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (toasts.Items.Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "通知が一定時間で消えませんでした。");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 8; i++)
        {
            notifications.ShowInfo($"通知{i}");
        }

        Assert.Equal(5, toasts.Items.Count);
        Assert.Equal("通知7", toasts.Items[^1].Message); // 古いものから消える
    }
}
