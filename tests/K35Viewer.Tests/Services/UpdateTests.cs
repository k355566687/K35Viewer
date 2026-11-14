using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Updates;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>アプリの更新（要件定義 24.6）。リリースの取得、確認、ダウンロードとハッシュの確認、フォルダの入れ替え。</summary>
public class UpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DownloadBase = "https://github.com/k355566687/K35Viewer/releases/download";

    /// <summary>リリース1つ分の JSON。配布物とハッシュの名前は、タグから決まる。</summary>
    private static string Release(
        string tag, bool draft = false, bool prerelease = false, bool package = true, bool hash = true,
        string downloadHost = "github.com", string pageHost = "github.com")
    {
        var name = $"K35Viewer-{tag}-win-x64.zip";
        var assets = new List<string>();
        if (package)
        {
            assets.Add($$"""{"name":"{{name}}","size":1234,"browser_download_url":"https://{{downloadHost}}/k355566687/K35Viewer/releases/download/{{tag}}/{{name}}"}""");
        }

        if (hash)
        {
            assets.Add($$"""{"name":"{{name}}.sha256","size":100,"browser_download_url":"https://{{downloadHost}}/k355566687/K35Viewer/releases/download/{{tag}}/{{name}}.sha256"}""");
        }

        return $$"""
            {"tag_name":"{{tag}}","draft":{{draft.ToString().ToLowerInvariant()}},"prerelease":{{prerelease.ToString().ToLowerInvariant()}},
             "html_url":"https://{{pageHost}}/k355566687/K35Viewer/releases/tag/{{tag}}","assets":[{{string.Join(",", assets)}}]}
            """;
    }

    private static string Releases(params string[] releases) => "[" + string.Join(",", releases) + "]";

    /// <summary>配布物の ZIP（本物と同じく、フォルダ K35Viewer-win-x64 の中に一式が入っている）。</summary>
    private static byte[] PackageZip(params (string Name, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return memory.ToArray();
    }

    private static byte[] DefaultPackage() =>
        PackageZip(("K35Viewer-win-x64/K35Viewer.exe", "new exe"), ("K35Viewer-win-x64/K35Viewer.dll", "new dll"));

    private static string HashOf(byte[] data, string name = "K35Viewer-v0.3.0-win-x64.zip") =>
        $"{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}  {name}\n";

    private sealed class Setup : IDisposable
    {
        public Setup(LibraryTestContext ctx, string releasesJson, byte[]? package = null, bool writableAppDirectory = true)
        {
            Ctx = ctx;
            var data = package ?? DefaultPackage();
            Handler = new FakeGitHubHandler { ReleasesJson = releasesJson, Package = data, HashText = HashOf(data) };
            Launcher = new FakeUpdateLauncher();
            AppDirectory = Path.Combine(ctx.Workspace, "app");
            if (writableAppDirectory)
            {
                _ = Directory.CreateDirectory(AppDirectory);
            }

            Service = ctx.CreateUpdateService(Handler, Launcher, new Version(0, 2, 0), AppDirectory);
        }

        public LibraryTestContext Ctx { get; }

        public FakeGitHubHandler Handler { get; }

        public FakeUpdateLauncher Launcher { get; }

        public string AppDirectory { get; }

        public UpdateService Service { get; }

        public AppPaths Paths => new(Ctx.CacheDirectory);

        public void Dispose()
        {
        }
    }

    // ---- リリースの取得 ----

    [Fact]
    public async Task リリースの一覧は_下書きを除き_試用版を含め_新しい順に返す()
    {
        var handler = new FakeGitHubHandler
        {
            ReleasesJson = Releases(
                Release("v0.2.1"),
                Release("v0.3.0", prerelease: true),
                Release("v0.9.9", draft: true),
                Release("v0.2.5", package: false, hash: false),
                Release("nightly")), // 版を読めないタグは、含めない
        };
        using var http = new HttpClient(handler);

        var releases = await new GitHubReleaseClient(http).GetReleasesAsync(Ct);

        Assert.Equal(["v0.3.0", "v0.2.5", "v0.2.1"], releases.Select(r => r.TagName));
        Assert.True(releases[0].IsPrerelease);
        Assert.True(releases[0].IsInstallable);
        Assert.Equal(1234, releases[0].PackageSize);
        Assert.False(releases[1].IsInstallable); // 配布物がない
        Assert.Equal("v0.3.0", releases[0].DisplayVersion);
        Assert.Equal("api.github.com", handler.Requests[0].Host);
    }

    [Fact]
    public async Task github_com以外のURLの配布物は_ないものとして扱う()
    {
        var handler = new FakeGitHubHandler
        {
            ReleasesJson = Releases(Release("v0.3.0", downloadHost: "evil.example.com"), Release("v0.2.9", pageHost: "evil.example.com")),
        };
        using var http = new HttpClient(handler);

        var releases = await new GitHubReleaseClient(http).GetReleasesAsync(Ct);

        var only = Assert.Single(releases); // ページの URL が github.com でないリリースは、含めない
        Assert.Equal("v0.3.0", only.TagName);
        Assert.Null(only.PackageUrl);
        Assert.Null(only.HashUrl);
    }

    [Theory]
    [InlineData("v0.2.1", true)]
    [InlineData("0.2.1", true)]
    [InlineData("v1.10.0", true)]
    [InlineData("v0.2", false)]
    [InlineData("v0.2.1-beta", false)]
    [InlineData("v0.2.x", false)]
    [InlineData("release", false)]
    [InlineData("", false)]
    public void タグから版を読み取れるのは_3つの数字の形だけ(string tag, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.TryParseVersion(tag, out _));
    }

    // ---- 更新の確認 ----

    [Fact]
    public async Task 今の版より新しい版があれば_最新のものを知らせる_試用版も対象にする()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.2.1"), Release("v0.3.0", prerelease: true)));

        var result = await setup.Service.CheckAsync(Ct);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("v0.3.0", result.Release!.TagName);
    }

    [Theory]
    [InlineData("[]")]
    public async Task 同じ版や古い版しかなければ_最新の版と知らせる(string json)
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, json);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await setup.Service.CheckAsync(Ct)).Status);

        setup.Handler.ReleasesJson = Releases(Release("v0.2.0"), Release("v0.1.0"));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await setup.Service.CheckAsync(Ct)).Status);
    }

    [Fact]
    public async Task 確認できなかった場合は_理由を返し_例外は投げない()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, "[]");

        setup.Handler.ReleasesStatus = HttpStatusCode.InternalServerError;
        var serverError = await setup.Service.CheckAsync(Ct);
        setup.Handler.ReleasesStatus = HttpStatusCode.Forbidden;
        var rateLimited = await setup.Service.CheckAsync(Ct);
        setup.Handler.ReleasesStatus = null;
        setup.Handler.Failure = new HttpRequestException("接続できない");
        var offline = await setup.Service.CheckAsync(Ct);
        setup.Handler.Failure = null;
        setup.Handler.ReleasesJson = "これはJSONではない";
        var broken = await setup.Service.CheckAsync(Ct);

        Assert.All([serverError, rateLimited, offline, broken], r => Assert.Equal(UpdateCheckStatus.Failed, r.Status));
        Assert.Contains("500", serverError.Message, StringComparison.Ordinal);
        Assert.Contains("多すぎ", rateLimited.Message, StringComparison.Ordinal);
        Assert.Contains("インターネット", offline.Message, StringComparison.Ordinal);
        Assert.Contains("読めません", broken.Message, StringComparison.Ordinal);
    }

    // ---- 更新の準備 ----

    private static async Task<ReleaseInfo> LatestAsync(Setup setup) =>
        (await setup.Service.CheckAsync(Ct)).Release!;

    [Fact]
    public async Task 更新の準備ができると_今のデータをバックアップし_展開した新しい版を起動する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        var release = await LatestAsync(setup);
        var phases = new List<UpdatePhase>();

        await setup.Service.InstallAsync(release, new SyncProgress<UpdateProgress>(p => phases.Add(p.Phase)), Ct);

        var (executable, arguments) = Assert.Single(setup.Launcher.Launches);
        Assert.Equal(Path.Combine(setup.Paths.UpdateStagingDirectory, "app", "K35Viewer.exe"), executable);
        Assert.Equal("new exe", File.ReadAllText(executable));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(setup.Paths.UpdateStagingDirectory, "app", "K35Viewer.dll")));
        Assert.Contains("--apply-update", arguments, StringComparison.Ordinal);
        Assert.Contains($"\"{setup.AppDirectory}\"", arguments, StringComparison.Ordinal);
        Assert.Equal(Environment.ProcessId, RestartArguments.ParseWaitProcessId(arguments.Split(' ')));
        Assert.Single(ctx.CreateBackupService().List(), b => b.Kind == BackupKind.Auto);
        Assert.Empty(Directory.EnumerateFileSystemEntries(setup.AppDirectory)); // アプリのフォルダには、触れない
        Assert.Equal(
            [UpdatePhase.Downloading, UpdatePhase.Verifying, UpdatePhase.Extracting, UpdatePhase.BackingUp, UpdatePhase.Starting],
            phases.Distinct());
    }

    [Fact]
    public async Task ハッシュが一致しなければ_更新を中止し_バックアップも起動もしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Handler.HashText = new string('0', 64) + "  K35Viewer-v0.3.0-win-x64.zip\n";
        var release = await LatestAsync(setup);

        var error = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.Contains("一致しません", error.Message, StringComparison.Ordinal);
        Assert.True(error.OfferReleasePage);
        Assert.Empty(setup.Launcher.Launches);
        Assert.Empty(ctx.CreateBackupService().List());
        Assert.False(Directory.Exists(setup.Paths.UpdateStagingDirectory)); // ダウンロードしたファイルを残さない
    }

    [Theory]
    [InlineData("")]
    [InlineData("これはハッシュではない")]
    [InlineData("zzzz")]
    public async Task ハッシュのファイルを読めなければ_更新を中止する(string hashText)
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Handler.HashText = hashText;
        var release = await LatestAsync(setup);

        _ = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.Empty(setup.Launcher.Launches);
    }

    [Fact]
    public async Task 大文字のハッシュでも_確かめられる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Handler.HashText = setup.Handler.HashText.ToUpperInvariant();
        var release = await LatestAsync(setup);

        await setup.Service.InstallAsync(release, ct: Ct);

        _ = Assert.Single(setup.Launcher.Launches);
    }

    [Fact]
    public async Task 配布物かハッシュがない版は_通信せずに断り_リリースのページを案内する()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0", hash: false)));
        var release = await LatestAsync(setup);
        var requests = setup.Handler.Requests.Count;

        var error = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.False(release.IsInstallable);
        Assert.True(error.OfferReleasePage);
        Assert.Equal(requests, setup.Handler.Requests.Count);
    }

    [Fact]
    public async Task アプリのフォルダに書き込めなければ_通信せずに断り_リリースのページを案内する()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")), writableAppDirectory: false);
        var release = await LatestAsync(setup);
        var requests = setup.Handler.Requests.Count;

        Assert.False(setup.Service.CanWriteToAppDirectory());
        var error = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.Contains("書き込めない", error.Message, StringComparison.Ordinal);
        Assert.True(error.OfferReleasePage);
        Assert.Equal(requests, setup.Handler.Requests.Count);
    }

    [Fact]
    public async Task 配布物の中に不正な名前があれば_何も展開せずに断る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var evil = PackageZip(
            ("K35Viewer-win-x64/K35Viewer.exe", "new exe"), ("K35Viewer-win-x64/../../evil.txt", "悪い"));
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")), evil);
        var release = await LatestAsync(setup);

        var error = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.Contains("不正", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ctx.CacheDirectory, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(ctx.CacheDirectory, "update-staging", "evil.txt")));
        Assert.Empty(setup.Launcher.Launches);
        Assert.Empty(ctx.CreateBackupService().List()); // バックアップもしない
    }

    [Fact]
    public async Task 配布物に実行ファイルがなければ_断る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")), PackageZip(("K35Viewer-win-x64/readme.txt", "x")));
        var release = await LatestAsync(setup);

        var error = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.Contains("実行ファイル", error.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Launcher.Launches);
    }

    [Fact]
    public async Task 新しい版を起動できなければ_断る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Launcher.Result = false;
        var release = await LatestAsync(setup);

        var error = await Assert.ThrowsAsync<UpdateException>(() => setup.Service.InstallAsync(release, ct: Ct));

        Assert.Contains("起動できません", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(setup.Paths.UpdateStagingDirectory));
    }

    [Fact]
    public async Task ダウンロードの途中で取り消すと_ダウンロードしたファイルを残さない()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Handler.HangOnPackage = true;
        var release = await LatestAsync(setup);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.InstallAsync(release, ct: cts.Token));

        Assert.False(Directory.Exists(setup.Paths.UpdateStagingDirectory));
        Assert.Empty(setup.Launcher.Launches);
    }

    // ---- アプリのフォルダの入れ替え ----

    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void 入れ替えは_新しい一式を重ね_使っていない古いファイルはそのまま残す()
    {
        using var ctx = new LibraryTestContext();
        var source = Path.Combine(ctx.Workspace, "new");
        var target = Path.Combine(ctx.Workspace, "app");
        var rollback = Path.Combine(ctx.Workspace, "rollback");
        _ = Write(target, "K35Viewer.exe", "old exe");
        _ = Write(target, "a.dll", "old a");
        _ = Write(target, "extra.txt", "ユーザーのファイル");
        _ = Write(source, "K35Viewer.exe", "new exe");
        _ = Write(source, "a.dll", "new a");
        _ = Write(source, "b.dll", "new b");
        _ = Write(source, Path.Combine("sub", "c.dll"), "new c");

        var result = UpdateApplier.Apply(source, target, rollback, "0.3.0");

        Assert.True(result.Succeeded);
        Assert.Contains("v0.3.0", result.Message, StringComparison.Ordinal);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "K35Viewer.exe")));
        Assert.Equal("new a", File.ReadAllText(Path.Combine(target, "a.dll")));
        Assert.Equal("new b", File.ReadAllText(Path.Combine(target, "b.dll")));
        Assert.Equal("new c", File.ReadAllText(Path.Combine(target, "sub", "c.dll")));
        Assert.Equal("ユーザーのファイル", File.ReadAllText(Path.Combine(target, "extra.txt")));
        Assert.False(Directory.Exists(rollback));
    }

    [Fact]
    public void 入れ替えの途中で失敗したら_元の版のファイルに戻し_新しく置いたファイルは消す()
    {
        using var ctx = new LibraryTestContext();
        var source = Path.Combine(ctx.Workspace, "new");
        var target = Path.Combine(ctx.Workspace, "app");
        var rollback = Path.Combine(ctx.Workspace, "rollback");
        _ = Write(target, "a.dll", "old a");
        var locked = Write(target, "z.dll", "old z"); // 使用中で、動かせない
        _ = Write(source, "a.dll", "new a");
        _ = Write(source, "b.dll", "new b");
        _ = Write(source, "z.dll", "new z");

        UpdateResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = UpdateApplier.Apply(source, target, rollback, "0.3.0");
        }

        Assert.False(result.Succeeded);
        Assert.Contains("元の版", result.Message, StringComparison.Ordinal);
        Assert.Equal("old a", File.ReadAllText(Path.Combine(target, "a.dll")));
        Assert.Equal("old z", File.ReadAllText(locked));
        Assert.False(File.Exists(Path.Combine(target, "b.dll"))); // 新しく増えるはずだったファイルも、残さない
        Assert.False(Directory.Exists(rollback));
    }

    [Fact]
    public void 入れ替えの専用の起動の引数を作り_読み取れる()
    {
        var arguments = UpdateApplier.ArgumentsFor(@"C:\Apps\35 Viewer\", 4321);

        Assert.Equal("--apply-update \"C:\\Apps\\35 Viewer\" --wait-for-exit 4321", arguments);
        Assert.Equal(@"C:\Apps\35 Viewer", UpdateApplier.ParseApplyTarget(["--apply-update", @"C:\Apps\35 Viewer", "--wait-for-exit", "4321"]));
        Assert.Null(UpdateApplier.ParseApplyTarget(["--wait-for-exit", "1"]));
        Assert.Null(UpdateApplier.ParseApplyTarget(["--apply-update"]));
        Assert.Null(UpdateApplier.ParseApplyTarget(["--apply-update", " "]));
    }

    [Fact]
    public void 入れ替えの結果は_書いて_1回だけ読める_壊れていれば読まない()
    {
        using var ctx = new LibraryTestContext();
        var paths = new AppPaths(ctx.CacheDirectory);
        _ = Directory.CreateDirectory(paths.Root);
        Assert.Null(UpdateApplier.TakeResult(paths));

        UpdateApplier.WriteResult(paths, new UpdateResult(true, "更新しました", "0.3.0"));
        var result = UpdateApplier.TakeResult(paths);

        Assert.Equal(new UpdateResult(true, "更新しました", "0.3.0"), result);
        Assert.Null(UpdateApplier.TakeResult(paths));

        File.WriteAllText(paths.UpdateResultFile, "これはJSONではない");
        Assert.Null(UpdateApplier.TakeResult(paths));
    }

    // ---- 設定画面 ----

    [Fact]
    public async Task 設定画面で確認すると_新しい版を表示し_変更点を開け_更新できる状態になる()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service);
        Assert.False(viewModel.InstallUpdateCommand.CanExecute(null));
        Assert.False(viewModel.OpenReleaseNotesCommand.CanExecute(null));

        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Equal("0.2.0", viewModel.CurrentVersionText);
        Assert.Contains("v0.3.0", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.InstallUpdateCommand.CanExecute(null));
        viewModel.OpenReleaseNotesCommand.Execute(null);
        Assert.Equal([new Uri("https://github.com/k355566687/K35Viewer/releases/tag/v0.3.0")], ctx.Explorer.OpenedWebPages);
    }

    [Fact]
    public async Task 設定画面で確認して_最新だった場合と_確認できなかった場合の表示()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.2.0")));
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service);

        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);
        Assert.Equal("最新の版です", viewModel.UpdateStatusText);
        Assert.False(viewModel.InstallUpdateCommand.CanExecute(null));

        setup.Handler.Failure = new HttpRequestException("接続できない");
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);
        Assert.Contains("インターネット", viewModel.UpdateStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task アプリの中で更新できない版は_変更点を見るよう案内し_更新するは押せない()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0", package: false, hash: false)));
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service);

        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Contains("変更点を見る", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.InstallUpdateCommand.CanExecute(null));
        Assert.True(viewModel.OpenReleaseNotesCommand.CanExecute(null));
    }

    [Fact]
    public async Task 設定画面で更新すると_確認して_準備し_新しい版を起動する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        var dialogs = new FakeUpdateDialogs { Confirm = true };
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service, updateDialogs: dialogs);
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(["v0.3.0"], dialogs.Confirmations);
        _ = Assert.Single(setup.Launcher.Launches);
        Assert.False(viewModel.IsUpdateBusy);
    }

    [Fact]
    public async Task 設定画面の確認でキャンセルすると_何もダウンロードしない()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        var dialogs = new FakeUpdateDialogs { Confirm = false };
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service, updateDialogs: dialogs);
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);
        var requests = setup.Handler.Requests.Count;

        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(requests, setup.Handler.Requests.Count);
        Assert.Empty(setup.Launcher.Launches);
    }

    [Fact]
    public async Task 設定画面で更新できなかった場合は_理由を表示し_通知する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Handler.HashText = new string('0', 64) + "  x.zip\n";
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service);
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Contains("一致しません", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.Contains("手で入れ替え", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error);
        Assert.Empty(setup.Launcher.Launches);
    }

    [Fact]
    public async Task 設定画面でダウンロードを中止できる()
    {
        using var ctx = new LibraryTestContext();
        using var setup = new Setup(ctx, Releases(Release("v0.3.0")));
        setup.Handler.HangOnPackage = true;
        using var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: setup.Service);
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        var install = viewModel.InstallUpdateCommand.ExecuteAsync(null);
        while (!viewModel.IsUpdateBusy || setup.Handler.Requests.Count < 3)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(viewModel.CancelUpdateCommand.CanExecute(null));
        viewModel.CancelUpdateCommand.Execute(null);
        await install;

        Assert.Contains("中止", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.IsUpdateBusy);
        Assert.Empty(setup.Launcher.Launches);
    }

    /// <summary>進行状況を、呼び出したスレッドでそのまま受け取る（<see cref="Progress{T}"/> は、別のスレッドで呼ぶことがある）。</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>キャッシュの管理の代わり（このテストでは使わない）。</summary>
    private sealed class NoCache : IThumbnailCacheMaintenance
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
}
