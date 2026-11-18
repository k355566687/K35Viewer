using K35Viewer.Infrastructure.Shortcuts;
using K35Viewer.Services;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>ショートカットの代わり。作った・削除した記録を持つ（実際のファイルは作らない）。</summary>
internal sealed class FakeShortcutService : IShortcutService
{
    public ShortcutAvailability Availability { get; set; } = ShortcutAvailability.Available;

    public string? ExecutablePath { get; set; } = @"C:\Apps\35Viewer\K35Viewer.exe";

    public Dictionary<ShortcutPlace, ShortcutState> States { get; } = [];

    public string? RemoveError { get; set; }

    public List<ShortcutRequest> CreateCalls { get; } = [];

    public List<ShortcutPlace> RemoveCalls { get; } = [];

    public string PathOf(ShortcutPlace place) => $@"C:\Shortcuts\{place}\35Viewer.lnk";

    public ShortcutState GetState(ShortcutPlace place) => States.GetValueOrDefault(place, ShortcutState.Missing);

    public Task<ShortcutOutcome> CreateAsync(ShortcutRequest request)
    {
        CreateCalls.Add(request);
        var created = new List<ShortcutPlace>();
        if (request.StartMenu)
        {
            States[ShortcutPlace.StartMenu] = ShortcutState.Current;
            created.Add(ShortcutPlace.StartMenu);
        }

        if (request.Desktop)
        {
            States[ShortcutPlace.Desktop] = ShortcutState.Current;
            created.Add(ShortcutPlace.Desktop);
        }

        return Task.FromResult(new ShortcutOutcome(created, []));
    }

    public string? Remove(ShortcutPlace place)
    {
        RemoveCalls.Add(place);
        if (RemoveError is null)
        {
            _ = States.Remove(place);
        }

        return RemoveError;
    }
}

/// <summary>ショートカットの確認のダイアログの代わり。</summary>
internal sealed class FakeShortcutDialogs : IShortcutDialogs
{
    /// <summary>はじめての確認への答え。null なら「今はしない」。</summary>
    public ShortcutRequest? FirstRunAnswer { get; set; }

    public bool TemporaryAnswer { get; set; } = true;

    public int FirstRunPrompts { get; private set; }

    public List<string> TemporaryPrompts { get; } = [];

    public ShortcutRequest? PromptFirstRun()
    {
        FirstRunPrompts++;
        return FirstRunAnswer;
    }

    public bool ConfirmTemporaryLocation(string executablePath)
    {
        TemporaryPrompts.Add(executablePath);
        return TemporaryAnswer;
    }
}

/// <summary>
/// ショートカットの作成と削除（GitHub の Issue #23）。実際の .lnk を、一時フォルダに作って確かめる
/// （スタートメニューやデスクトップの実際のフォルダには、触れない）。
/// </summary>
public sealed class ShortcutServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "K35ViewerShortcutTests", Guid.NewGuid().ToString("N"));

    public ShortcutServiceTests()
    {
        _ = Directory.CreateDirectory(_root);
    }

    private string StartMenu => Path.Combine(_root, "StartMenu");

    private string Desktop => Path.Combine(_root, "Desktop");

    /// <summary>「ふだん使う場所」にある実行ファイル（一時フォルダの外とみなす）。</summary>
    private string App => Path.Combine(_root, "Apps", "K35Viewer.exe");

    private ShortcutService Create(string? executable = null, string? temporaryDirectory = null)
    {
        var exe = executable ?? App;
        _ = Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        if (!File.Exists(exe))
        {
            File.WriteAllText(exe, "dummy");
        }

        return new ShortcutService(
            new ShortcutLocations(StartMenu, Desktop),
            exe,
            temporaryDirectory ?? Path.Combine(_root, "NotUsedTemp"),
            NullLogger<ShortcutService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は、無視する
        }
    }

    [Fact]
    public async Task 作ると_スタートメニューとデスクトップに_リンク先が実行ファイルのショートカットができる()
    {
        var service = Create();

        var outcome = await service.CreateAsync(new ShortcutRequest(true, true));

        Assert.Equal([ShortcutPlace.StartMenu, ShortcutPlace.Desktop], outcome.Created);
        Assert.Empty(outcome.Failures);
        Assert.Equal(Path.Combine(StartMenu, "35Viewer.lnk"), service.PathOf(ShortcutPlace.StartMenu));
        Assert.True(File.Exists(service.PathOf(ShortcutPlace.StartMenu)));
        Assert.True(File.Exists(service.PathOf(ShortcutPlace.Desktop)));
        Assert.Equal(App, ShellLink.ReadTarget(service.PathOf(ShortcutPlace.StartMenu)));
        Assert.Equal(ShortcutState.Current, service.GetState(ShortcutPlace.StartMenu));
        Assert.Equal(ShortcutState.Current, service.GetState(ShortcutPlace.Desktop));
    }

    [Fact]
    public async Task 日本語や空白を含む場所の実行ファイルでも_リンク先を正しく記録する()
    {
        var exe = Path.Combine(_root, "アプリ フォルダ", "K35Viewer.exe");
        var service = Create(exe);

        _ = await service.CreateAsync(new ShortcutRequest(true, false));

        Assert.Equal(exe, ShellLink.ReadTarget(service.PathOf(ShortcutPlace.StartMenu)));
        Assert.Equal(ShortcutState.Current, service.GetState(ShortcutPlace.StartMenu));
    }

    [Fact]
    public async Task 選んだ場所にだけ作る()
    {
        var service = Create();

        var outcome = await service.CreateAsync(new ShortcutRequest(false, true));

        Assert.Equal([ShortcutPlace.Desktop], outcome.Created);
        Assert.Equal(ShortcutState.Missing, service.GetState(ShortcutPlace.StartMenu));
        Assert.False(Directory.Exists(StartMenu)); // 作らない場所のフォルダにも、触れない
        Assert.Equal(ShortcutState.Current, service.GetState(ShortcutPlace.Desktop));
    }

    [Fact]
    public async Task リンク先が今の場所と違うショートカットは_古い状態になり_作り直すと直る()
    {
        var oldApp = Path.Combine(_root, "OldFolder", "K35Viewer.exe");
        var old = Create(oldApp);
        _ = await old.CreateAsync(new ShortcutRequest(true, false)); // 以前の場所で作ったもの

        var service = Create(); // フォルダを移した今のアプリ
        Assert.Equal(ShortcutState.Stale, service.GetState(ShortcutPlace.StartMenu));

        _ = await service.CreateAsync(new ShortcutRequest(true, false));

        Assert.Equal(ShortcutState.Current, service.GetState(ShortcutPlace.StartMenu));
        Assert.Equal(App, ShellLink.ReadTarget(service.PathOf(ShortcutPlace.StartMenu)));
    }

    [Fact]
    public async Task 削除すると_ショートカットだけが消え_なかったときは何もしない()
    {
        var service = Create();
        _ = await service.CreateAsync(new ShortcutRequest(true, true));

        Assert.Null(service.Remove(ShortcutPlace.Desktop));
        Assert.Null(service.Remove(ShortcutPlace.Desktop)); // もうない

        Assert.False(File.Exists(service.PathOf(ShortcutPlace.Desktop)));
        Assert.True(File.Exists(service.PathOf(ShortcutPlace.StartMenu)));
        Assert.True(File.Exists(App)); // アプリ本体には触れない
    }

    [Fact]
    public async Task 作れない場所があっても_ほかの場所には作り_失敗の理由を返す()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "file");
        var service = new ShortcutService(
            new ShortcutLocations(StartMenu, Path.Combine(blocker, "Desktop")), // ファイルの下には、フォルダを作れない
            Create().ExecutablePath,
            Path.Combine(_root, "NotUsedTemp"),
            NullLogger<ShortcutService>.Instance);

        var outcome = await service.CreateAsync(new ShortcutRequest(true, true));

        Assert.Equal([ShortcutPlace.StartMenu], outcome.Created);
        var failure = Assert.Single(outcome.Failures);
        Assert.Equal(ShortcutPlace.Desktop, failure.Place);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Fact]
    public void 実行ファイルの名前が違う場合は_作れない()
    {
        var service = new ShortcutService(
            new ShortcutLocations(StartMenu, Desktop),
            @"C:\Program Files\dotnet\dotnet.exe",
            Path.Combine(_root, "NotUsedTemp"),
            NullLogger<ShortcutService>.Instance);

        Assert.Equal(ShortcutAvailability.UnsuitableExecutable, service.Availability);
        Assert.Null(service.ExecutablePath);
    }

    [Fact]
    public async Task 実行ファイルが分からない場合は_作ろうとしても何も作らず_失敗として返す()
    {
        var service = new ShortcutService(
            new ShortcutLocations(StartMenu, Desktop),
            null,
            Path.Combine(_root, "NotUsedTemp"),
            NullLogger<ShortcutService>.Instance);

        var outcome = await service.CreateAsync(new ShortcutRequest(true, true));

        Assert.Empty(outcome.Created);
        Assert.Equal(2, outcome.Failures.Count);
        Assert.False(Directory.Exists(StartMenu));
        Assert.False(Directory.Exists(Desktop));
    }

    [Fact]
    public void 一時的な場所にある実行ファイルは_作る前に確認が要る()
    {
        var temporary = Path.Combine(_root, "Temp1_book.zip");
        var inside = Path.Combine(temporary, "K35Viewer.exe");

        Assert.Equal(ShortcutAvailability.TemporaryLocation, Create(inside, temporary).Availability);
        Assert.Equal(ShortcutAvailability.Available, Create(App, temporary).Availability);
        // 名前が似ているだけの場所は、一時的な場所とはみなさない
        Assert.Equal(ShortcutAvailability.Available, Create(Path.Combine(_root, "Temp1_book.zip2", "K35Viewer.exe"), temporary).Availability);
    }
}

/// <summary>はじめて起動したときの、ショートカットの確認（GitHub の Issue #23）。</summary>
public sealed class ShortcutFirstRunTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Context = new LibraryTestContext();
            Settings = Context.CreateSettings();
            Service = new FakeShortcutService();
            Dialogs = new FakeShortcutDialogs();
            Runner = new ShortcutFirstRun(Settings, Service, Dialogs, Context.Notifications, NullLogger<ShortcutFirstRun>.Instance);
        }

        public LibraryTestContext Context { get; }

        public SettingsService Settings { get; }

        public FakeShortcutService Service { get; }

        public FakeShortcutDialogs Dialogs { get; }

        public ShortcutFirstRun Runner { get; }

        public void Dispose() => Context.Dispose();
    }

    [Fact]
    public async Task はじめての起動では_確認して_選ばれたものを作り_聞いたことを記録する()
    {
        using var rig = new Rig();
        rig.Dialogs.FirstRunAnswer = new ShortcutRequest(true, true);

        await rig.Runner.RunAsync();

        Assert.Equal(1, rig.Dialogs.FirstRunPrompts);
        Assert.Equal([new ShortcutRequest(true, true)], rig.Service.CreateCalls);
        Assert.True(rig.Settings.Current.ShortcutPromptShown);
        Assert.True(rig.Context.CreateSettings().Current.ShortcutPromptShown); // 保存されている
        Assert.Contains(rig.Context.Notifications.Items, n => n.Message.Contains("スタートメニュー、デスクトップにショートカットを作りました", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 今はしないを選んでも_聞いたことになり_二度目は聞かない()
    {
        using var rig = new Rig();
        rig.Dialogs.FirstRunAnswer = null;

        await rig.Runner.RunAsync();
        await rig.Runner.RunAsync();

        Assert.Equal(1, rig.Dialogs.FirstRunPrompts);
        Assert.Empty(rig.Service.CreateCalls);
        Assert.True(rig.Settings.Current.ShortcutPromptShown);
    }

    [Fact]
    public async Task すでに聞いていれば_何も聞かない()
    {
        using var rig = new Rig();
        _ = rig.Settings.Update(s => s with { ShortcutPromptShown = true });

        await rig.Runner.RunAsync();

        Assert.Equal(0, rig.Dialogs.FirstRunPrompts);
        Assert.Empty(rig.Service.CreateCalls);
    }

    [Theory]
    [InlineData(ShortcutAvailability.TemporaryLocation)]
    [InlineData(ShortcutAvailability.UnsuitableExecutable)]
    public async Task ショートカットを作れない場所では_聞かず_聞いたことにもしない(ShortcutAvailability availability)
    {
        using var rig = new Rig();
        rig.Service.Availability = availability;

        await rig.Runner.RunAsync();

        Assert.Equal(0, rig.Dialogs.FirstRunPrompts);
        Assert.False(rig.Settings.Current.ShortcutPromptShown); // 正しい場所から起動したときに、聞く
    }

    [Fact]
    public async Task スタートメニューに今のアプリのショートカットがすでにあれば_聞かずに聞いたことにする()
    {
        using var rig = new Rig();
        rig.Service.States[ShortcutPlace.StartMenu] = ShortcutState.Current;

        await rig.Runner.RunAsync();

        Assert.Equal(0, rig.Dialogs.FirstRunPrompts);
        Assert.True(rig.Settings.Current.ShortcutPromptShown);
    }

    [Fact]
    public async Task 古い版の設定のファイルには項目がなく_聞いていない扱いになる()
    {
        using var rig = new Rig();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(rig.Context.SettingsFile)!);
        File.WriteAllText(rig.Context.SettingsFile, """{ "Theme": "Dark", "ConfirmBeforeLeave": true }""");

        var settings = rig.Context.CreateSettings();

        Assert.False(settings.Current.ShortcutPromptShown);
    }
}

/// <summary>設定画面の「ショートカット」（GitHub の Issue #23）。</summary>
[Collection(WpfViewsGroup.Name)]
public sealed class ShortcutSettingsTests
{
    private sealed class NoCacheMaintenance : IThumbnailCacheMaintenance
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public string? ClearBlockedReason => null;

        public Task<long> GetCacheSizeAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<bool> ClearCacheAsync() => Task.FromResult(true);
    }

    private static (LibraryTestContext Context, SettingsViewModel ViewModel, FakeShortcutService Service, FakeShortcutDialogs Dialogs) Create()
    {
        var ctx = new LibraryTestContext();
        var service = new FakeShortcutService();
        var dialogs = new FakeShortcutDialogs();
        var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCacheMaintenance(), shortcuts: service, shortcutDialogs: dialogs);
        return (ctx, viewModel, service, dialogs);
    }

    [Fact]
    public async Task 状態を表示し_作ってなければ作る_作ってあれば作り直すと案内する()
    {
        var (ctx, viewModel, service, _) = Create();
        using var _1 = ctx;
        service.States[ShortcutPlace.Desktop] = ShortcutState.Stale;

        await viewModel.InitializeAsync();

        Assert.Equal("作っていません", viewModel.StartMenuShortcutText);
        Assert.Equal("作る", viewModel.CreateStartMenuLabel);
        Assert.Contains("リンク先が今の場所と違います", viewModel.DesktopShortcutText, StringComparison.Ordinal);
        Assert.Equal("作り直す", viewModel.CreateDesktopLabel);
        Assert.Contains("作り直す", viewModel.ShortcutNote, StringComparison.Ordinal);
        Assert.False(viewModel.RemoveStartMenuShortcutCommand.CanExecute(null));
        Assert.True(viewModel.RemoveDesktopShortcutCommand.CanExecute(null));
    }

    [Fact]
    public async Task 作るボタンで作り_状態の表示が変わり_通知される()
    {
        var (ctx, viewModel, service, _) = Create();
        using var _1 = ctx;
        await viewModel.InitializeAsync();

        await viewModel.CreateStartMenuShortcutCommand.ExecuteAsync(null);

        Assert.Equal([new ShortcutRequest(true, false)], service.CreateCalls);
        Assert.Equal("作ってあります", viewModel.StartMenuShortcutText);
        Assert.Equal("作り直す", viewModel.CreateStartMenuLabel);
        Assert.True(viewModel.RemoveStartMenuShortcutCommand.CanExecute(null));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success);
    }

    [Fact]
    public async Task 削除すると_ショートカットだけを消し_失敗したら理由を知らせる()
    {
        var (ctx, viewModel, service, _) = Create();
        using var _1 = ctx;
        service.States[ShortcutPlace.StartMenu] = ShortcutState.Current;
        await viewModel.InitializeAsync();

        await viewModel.RemoveStartMenuShortcutCommand.ExecuteAsync(null);

        Assert.Equal([ShortcutPlace.StartMenu], service.RemoveCalls);
        Assert.Equal("作っていません", viewModel.StartMenuShortcutText);

        service.States[ShortcutPlace.Desktop] = ShortcutState.Current;
        service.RemoveError = "使用中です";
        await viewModel.RefreshShortcutsAsync();
        await viewModel.RemoveDesktopShortcutCommand.ExecuteAsync(null);

        Assert.Contains(ctx.Notifications.Items, n =>
            n.Level == NotificationLevel.Error && n.Message.Contains("使用中です", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 一時的な場所では_確認して_断られたら作らない()
    {
        var (ctx, viewModel, service, dialogs) = Create();
        using var _1 = ctx;
        service.Availability = ShortcutAvailability.TemporaryLocation;
        dialogs.TemporaryAnswer = false;
        await viewModel.InitializeAsync();

        await viewModel.CreateDesktopShortcutCommand.ExecuteAsync(null);

        Assert.Equal([service.ExecutablePath!], dialogs.TemporaryPrompts);
        Assert.Empty(service.CreateCalls);
        Assert.Contains("一時的な場所", viewModel.ShortcutNote, StringComparison.Ordinal);

        dialogs.TemporaryAnswer = true;
        await viewModel.CreateDesktopShortcutCommand.ExecuteAsync(null);

        Assert.Single(service.CreateCalls);
    }

    [Fact]
    public async Task 実行ファイルが違う場合は_作るボタンを押せない()
    {
        var (ctx, viewModel, service, _) = Create();
        using var _1 = ctx;
        service.Availability = ShortcutAvailability.UnsuitableExecutable;

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CreateStartMenuShortcutCommand.CanExecute(null));
        Assert.False(viewModel.CreateDesktopShortcutCommand.CanExecute(null));
        Assert.Contains("K35Viewer.exe", viewModel.ShortcutNote, StringComparison.Ordinal);
    }
}
