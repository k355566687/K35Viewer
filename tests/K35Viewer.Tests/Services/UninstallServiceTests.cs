using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Uninstall;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>アンインストールの確認のダイアログの代わり。</summary>
internal sealed class FakeUninstallDialogs : IUninstallDialogs
{
    /// <summary>答え。null なら、キャンセル。</summary>
    public UninstallChoice? Answer { get; set; }

    public List<(UninstallPlan Plan, string DefaultFolder)> Prompts { get; } = [];

    public UninstallChoice? Prompt(UninstallPlan plan, string defaultBackupFolder, Func<string, string?> validateBackupFolder)
    {
        Prompts.Add((plan, defaultBackupFolder));
        return Answer;
    }
}

/// <summary>アンインストールの代わり（実際には、何も消さない）。</summary>
internal sealed class FakeUninstallService : IUninstallService
{
    public string DefaultBackupFolder { get; set; } = @"C:\Users\someone\Downloads";

    public UninstallPlan Plan { get; set; } =
        new(new UninstallTargets(@"C:\Apps\35Viewer", @"C:\Data\K35Viewer", [], []), [], 150_000_000, 441, 1_000_000);

    public UninstallStartResult StartResult { get; set; } = new(true);

    public int PlanCalls { get; private set; }

    public List<(UninstallPlan Plan, string? BackupFolder)> StartCalls { get; } = [];

    public Task<UninstallPlan> PlanAsync(CancellationToken ct = default)
    {
        PlanCalls++;
        return Task.FromResult(Plan);
    }

    public string? ValidateBackupFolder(UninstallPlan plan, string? folder) =>
        UninstallPlanner.ValidateBackupFolder(plan.Targets, folder);

    public Task<UninstallStartResult> StartAsync(UninstallPlan plan, string? backupFolder, CancellationToken ct = default)
    {
        StartCalls.Add((plan, backupFolder));
        return Task.FromResult(StartResult);
    }
}

/// <summary>控えの起動の代わり（実際には、プロセスを起動せず、アプリも終了しない）。</summary>
internal sealed class FakeUninstallLauncher : IUninstallLauncher
{
    public bool Succeeds { get; set; } = true;

    public List<UninstallTargets> Started { get; } = [];

    public int Exits { get; private set; }

    public bool TryStart(UninstallTargets targets, out string? reason)
    {
        Started.Add(targets);
        reason = Succeeds ? null : "起動できませんでした（テスト）";
        return Succeeds;
    }

    public void ExitApplication() => Exits++;
}

/// <summary>アンインストールの始め方: 確認、バックアップ、控えの起動（GitHub の Issue #24）。</summary>
public sealed class UninstallServiceTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Context = new LibraryTestContext();
            Sandbox = new UninstallSandbox();
            Shortcuts = new FakeShortcutService();
            Launcher = new FakeUninstallLauncher();
            Paths = new AppPaths(Context.CacheDirectory);
            Service = new UninstallService(
                Sandbox.Planner(),
                Shortcuts,
                Context.Library,
                Context.CreateBackupService(),
                Launcher,
                Paths,
                Sandbox.ExecutablePath,
                Sandbox.PathOf("Downloads"),
                NullLogger<UninstallService>.Instance);
        }

        public LibraryTestContext Context { get; }

        public UninstallSandbox Sandbox { get; }

        public FakeShortcutService Shortcuts { get; }

        public FakeUninstallLauncher Launcher { get; }

        public AppPaths Paths { get; }

        public UninstallService Service { get; }

        public void Dispose()
        {
            Sandbox.Dispose();
            Context.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 確認は_登録された管理フォルダと_今のショートカットだけを使う()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        rig.Context.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(10, 10)));
        _ = await rig.Context.RegisterAndScanAsync();
        rig.Shortcuts.States[ShortcutPlace.StartMenu] = ShortcutState.Current;
        rig.Shortcuts.States[ShortcutPlace.Desktop] = ShortcutState.Stale; // 別の場所のアプリのもの。さわらない

        var plan = await rig.Service.PlanAsync(Ct);

        Assert.True(plan.CanRun, string.Join(" / ", plan.Blockers));
        Assert.Equal([rig.Shortcuts.PathOf(ShortcutPlace.StartMenu)], plan.Targets.ShortcutFiles);
        Assert.Equal([rig.Context.Workspace], plan.Targets.LibraryFolders);
        Assert.Equal(rig.Paths.Root, plan.Targets.DataDirectory);
        Assert.Equal(rig.Sandbox.ProgramDirectory, plan.Targets.ProgramDirectory);
    }

    [Fact]
    public async Task 管理フォルダが_プログラムのフォルダの中にあれば_確認の結果は実行できない()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        var books = Path.Combine(rig.Sandbox.ProgramDirectory, "books");
        _ = Directory.CreateDirectory(books);
        _ = await rig.Context.Library.AddFolderAsync(books, confirmMerge: true, Ct);

        var plan = await rig.Service.PlanAsync(Ct);

        Assert.False(plan.CanRun);
    }

    [Fact]
    public async Task バックアップを選ぶと_保存してから_控えを起動してアプリを終了する()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        var plan = await rig.Service.PlanAsync(Ct);
        var folder = rig.Sandbox.PathOf("Downloads");

        var result = await rig.Service.StartAsync(plan, folder, Ct);

        Assert.True(result.Started);
        Assert.NotNull(result.BackupFile);
        Assert.True(File.Exists(result.BackupFile));
        Assert.Equal(folder, Path.GetDirectoryName(result.BackupFile));
        Assert.EndsWith(".zip", result.BackupFile, StringComparison.Ordinal);
        Assert.Single(rig.Launcher.Started);
        Assert.Equal(rig.Paths.Root, rig.Launcher.Started[0].DataDirectory);
        Assert.Equal(1, rig.Launcher.Exits);
    }

    [Fact]
    public async Task バックアップを選ばなければ_バックアップを作らずに_控えを起動する()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        var plan = await rig.Service.PlanAsync(Ct);

        var result = await rig.Service.StartAsync(plan, null, Ct);

        Assert.True(result.Started);
        Assert.Null(result.BackupFile);
        Assert.Single(rig.Launcher.Started);
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.BackupsDirectory)) && Directory.EnumerateFiles(rig.Paths.BackupsDirectory).Any());
    }

    [Fact]
    public async Task バックアップの保存先が_消す場所の中なら_何もせずに断る()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        var plan = await rig.Service.PlanAsync(Ct);

        var result = await rig.Service.StartAsync(plan, Path.Combine(rig.Sandbox.ProgramDirectory, "licenses"), Ct);

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
        Assert.Empty(rig.Launcher.Started);
        Assert.Equal(0, rig.Launcher.Exits);
    }

    [Fact]
    public async Task バックアップを保存できなければ_控えを起動せず_何も消さない()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        var plan = await rig.Service.PlanAsync(Ct);
        var blocker = rig.Sandbox.PathOf("blocker");
        File.WriteAllText(blocker, "file");

        var result = await rig.Service.StartAsync(plan, Path.Combine(blocker, "Downloads"), Ct); // ファイルの下には、フォルダを作れない

        Assert.False(result.Started);
        Assert.Contains("何も削除していません", result.Error, StringComparison.Ordinal);
        Assert.Empty(rig.Launcher.Started);
        Assert.Equal(0, rig.Launcher.Exits);
    }

    [Fact]
    public async Task 始める直前の確認で問題があれば_バックアップも控えの起動もしない()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        var plan = await rig.Service.PlanAsync(Ct);
        // ダイアログを見ている間に、管理フォルダが、プログラムのフォルダの中に登録された
        var books = Path.Combine(rig.Sandbox.ProgramDirectory, "books");
        _ = Directory.CreateDirectory(books);
        _ = await rig.Context.Library.AddFolderAsync(books, confirmMerge: true, Ct);

        var result = await rig.Service.StartAsync(plan, rig.Sandbox.PathOf("Downloads"), Ct);

        Assert.False(result.Started);
        Assert.Empty(rig.Launcher.Started);
        Assert.False(Directory.Exists(rig.Sandbox.PathOf("Downloads")));
    }

    [Fact]
    public async Task 控えを起動できなければ_アプリは終了せず_理由を返す()
    {
        using var rig = new Rig();
        await rig.Context.InitializeAsync();
        rig.Launcher.Succeeds = false;
        var plan = await rig.Service.PlanAsync(Ct);

        var result = await rig.Service.StartAsync(plan, null, Ct);

        Assert.False(result.Started);
        Assert.Contains("起動できませんでした", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, rig.Launcher.Exits);
    }
}

/// <summary>設定画面の「アンインストール」（GitHub の Issue #24）。</summary>
[Collection(WpfViewsGroup.Name)]
public sealed class UninstallSettingsTests
{
    private sealed class SettableCache : IThumbnailCacheMaintenance
    {
        private string? _reason;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string? ClearBlockedReason
        {
            get => _reason;
            set
            {
                _reason = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ClearBlockedReason)));
            }
        }

        public Task<long> GetCacheSizeAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<bool> ClearCacheAsync() => Task.FromResult(true);
    }

    private static (LibraryTestContext Context, SettingsViewModel ViewModel, FakeUninstallService Service, FakeUninstallDialogs Dialogs, SettableCache Cache) Create()
    {
        var ctx = new LibraryTestContext();
        var service = new FakeUninstallService();
        var dialogs = new FakeUninstallDialogs();
        var cache = new SettableCache();
        var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), cache, uninstall: service, uninstallDialogs: dialogs);
        return (ctx, viewModel, service, dialogs, cache);
    }

    [Fact]
    public async Task 問題がなければ_押せて_案内は出ない()
    {
        var (ctx, viewModel, _, _, _) = Create();
        using var _1 = ctx;

        await viewModel.InitializeAsync();

        Assert.True(viewModel.UninstallCommand.CanExecute(null));
        Assert.Equal(string.Empty, viewModel.UninstallNote);
        Assert.Contains("アーカイブには、さわりません", viewModel.UninstallToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 安全のため実行できない場合は_押せず_理由を表示する()
    {
        var (ctx, viewModel, service, _, _) = Create();
        using var _1 = ctx;
        service.Plan = service.Plan with { Blockers = ["管理フォルダ（D:\\Books）が、プログラムのフォルダの中にあります"] };

        await viewModel.InitializeAsync();

        Assert.False(viewModel.UninstallCommand.CanExecute(null));
        Assert.Contains("D:\\Books", viewModel.UninstallNote, StringComparison.Ordinal);
        Assert.Contains("安全のため", viewModel.UninstallToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task スキャンやサムネイルの作成の間は_押せず_理由を示す()
    {
        var (ctx, viewModel, _, _, cache) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();

        cache.ClearBlockedReason = "サムネイルを作成中です";

        Assert.False(viewModel.UninstallCommand.CanExecute(null));
        Assert.Equal("サムネイルを作成中です", viewModel.UninstallToolTip);

        cache.ClearBlockedReason = null;

        Assert.True(viewModel.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task 確認でキャンセルすれば_何も始めない()
    {
        var (ctx, viewModel, service, dialogs, _) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();
        dialogs.Answer = null;

        await viewModel.UninstallCommand.ExecuteAsync(null);

        _ = Assert.Single(dialogs.Prompts);
        Assert.Equal(service.DefaultBackupFolder, dialogs.Prompts[0].DefaultFolder);
        Assert.Empty(service.StartCalls);
        Assert.True(viewModel.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task 確認で進むと_選んだ保存先で始める()
    {
        var (ctx, viewModel, service, dialogs, _) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();
        dialogs.Answer = new UninstallChoice(@"D:\Backups");

        await viewModel.UninstallCommand.ExecuteAsync(null);

        var call = Assert.Single(service.StartCalls);
        Assert.Equal(@"D:\Backups", call.BackupFolder);
        Assert.DoesNotContain(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error);
        Assert.False(viewModel.UninstallCommand.CanExecute(null)); // 始めたら、アプリは終了する。それまで、押せない
    }

    [Fact]
    public async Task バックアップなしを選ぶと_保存先なしで始める()
    {
        var (ctx, viewModel, service, dialogs, _) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();
        dialogs.Answer = new UninstallChoice(null);

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(service.StartCalls).BackupFolder);
    }

    [Fact]
    public async Task 始められなかったら_理由を通知し_もう一度押せる()
    {
        var (ctx, viewModel, service, dialogs, _) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();
        dialogs.Answer = new UninstallChoice(@"D:\Backups");
        service.StartResult = new UninstallStartResult(false, "バックアップを保存できませんでした。何も削除していません");

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Contains(ctx.Notifications.Items, n =>
            n.Level == NotificationLevel.Error && n.Message.Contains("何も削除していません", StringComparison.Ordinal));
        Assert.True(viewModel.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task ダイアログの前の確認で実行できなくなっていたら_ダイアログを出さずに知らせる()
    {
        var (ctx, viewModel, service, dialogs, _) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();
        service.Plan = service.Plan with { Blockers = ["管理フォルダ（D:\\Books）が、プログラムのフォルダの中にあります"] };

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Prompts);
        Assert.Empty(service.StartCalls);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error && n.Message.Contains("D:\\Books", StringComparison.Ordinal));
        Assert.False(viewModel.UninstallCommand.CanExecute(null));
    }
}
