using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using K35Viewer.Services;
using K35Viewer.ViewModels;
using K35Viewer.Views;

namespace K35Viewer.Tests.Services;

/// <summary>
/// アプリの更新の間の表示（画面全体を覆う「更新しています」と、操作の禁止）。画面一覧 8.3、内部アーキテクチャ設計書 36章。
/// 実際の WPF のウィンドウを使うため、ほかの画面のテストと同時には動かさない。
/// </summary>
[Collection(WpfViewsGroup.Name)]
public class UpdateOverlayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Tag = "v0.3.0";

    private static string ReleasesJson() =>
        $$"""
        [{"tag_name":"{{Tag}}","draft":false,"prerelease":true,
          "html_url":"https://github.com/k355566687/K35Viewer/releases/tag/{{Tag}}",
          "assets":[
            {"name":"K35Viewer-{{Tag}}-win-x64.zip","size":1,"browser_download_url":"https://github.com/k355566687/K35Viewer/releases/download/{{Tag}}/K35Viewer-{{Tag}}-win-x64.zip"},
            {"name":"K35Viewer-{{Tag}}-win-x64.zip.sha256","size":1,"browser_download_url":"https://github.com/k355566687/K35Viewer/releases/download/{{Tag}}/K35Viewer-{{Tag}}-win-x64.zip.sha256"}]}]
        """;

    private static byte[] PackageZip()
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "K35Viewer-win-x64/K35Viewer.exe", "K35Viewer-win-x64/K35Viewer.dll" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write("new"u8);
            }
        }

        return memory.ToArray();
    }

    private sealed class Setup
    {
        public Setup(LibraryTestContext ctx)
        {
            var package = PackageZip();
            Handler = new FakeGitHubHandler
            {
                ReleasesJson = ReleasesJson(),
                Package = package,
                HashText = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)).ToLowerInvariant() + "  x.zip\n",
            };
            Launcher = new FakeUpdateLauncher();
            var appDirectory = Path.Combine(ctx.Workspace, "app");
            _ = Directory.CreateDirectory(appDirectory);
            Service = ctx.CreateUpdateService(Handler, Launcher, new Version(0, 2, 0), appDirectory);
            ViewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCache(), updates: Service);
        }

        public FakeGitHubHandler Handler { get; }

        public FakeUpdateLauncher Launcher { get; }

        public UpdateService Service { get; }

        public SettingsViewModel ViewModel { get; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    // ---- 状態の流れ（ViewModel） ----

    [Fact]
    public async Task 更新の準備の間は_覆う表示を出して設定を操作できなくし_中止すると元に戻る()
    {
        using var ctx = new LibraryTestContext();
        var setup = new Setup(ctx);
        using var viewModel = setup.ViewModel;
        setup.Handler.HangOnPackage = true;
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsInstallingUpdate);
        Assert.True(viewModel.IsSettingsEnabled);

        var install = viewModel.InstallUpdateCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsInstallingUpdate);

        Assert.False(viewModel.IsSettingsEnabled);
        Assert.False(string.IsNullOrEmpty(viewModel.UpdateProgressText));
        Assert.True(viewModel.CancelUpdateCommand.CanExecute(null));
        Assert.False(viewModel.CheckForUpdateCommand.CanExecute(null)); // 同時に、確認も更新もできない
        viewModel.CancelUpdateCommand.Execute(null);
        await install;

        Assert.False(viewModel.IsInstallingUpdate);
        Assert.True(viewModel.IsSettingsEnabled);
        Assert.Contains("中止", viewModel.UpdateStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 更新の準備に失敗したら_覆う表示は消える()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var setup = new Setup(ctx);
        using var viewModel = setup.ViewModel;
        setup.Handler.HashText = new string('0', 64) + "  x.zip\n"; // ハッシュが合わない
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsInstallingUpdate);
        Assert.True(viewModel.IsSettingsEnabled);
        Assert.Contains("一致しません", viewModel.UpdateStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 新しい版を起動したあとは_アプリが終わるまで覆う表示を残し_もう中止できない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var setup = new Setup(ctx);
        using var viewModel = setup.ViewModel;
        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        await viewModel.InstallUpdateCommand.ExecuteAsync(null);

        _ = Assert.Single(setup.Launcher.Launches);
        Assert.True(viewModel.IsInstallingUpdate); // 終了するまで、操作できないまま
        Assert.False(viewModel.IsSettingsEnabled);
        Assert.Contains("起動", viewModel.UpdateProgressText, StringComparison.Ordinal);
        Assert.False(viewModel.CancelUpdateCommand.CanExecute(null));
    }

    // ---- 画面（WPF） ----

    private static Grid Overlay(Window window) =>
        WpfTestHelper.Descendants<Grid>(window).First(g => AutomationProperties.GetName(g) == "更新中の表示");

    [Fact]
    public void 更新の間は_設定画面を覆う表示が出て_項目は操作できず_閉じることもできない()
    {
        using var ctx = new LibraryTestContext();
        var setup = new Setup(ctx);
        using var viewModel = setup.ViewModel;
        var overlayBefore = true;
        var overlayDuring = false;
        var progressTextShown = string.Empty;
        var closeButtonEnabledDuring = true;
        var contentEnabledDuring = true;
        var stillOpenAfterCloseRequest = false;
        var openedAfterFinish = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new SettingsWindow(viewModel));
            window.Show();
            WpfTestHelper.Pump();
            overlayBefore = Overlay(window).IsVisible;

            viewModel.UpdateProgressText = "ダウンロードしています… 12.0 MB / 64.0 MB";
            viewModel.IsInstallingUpdate = true;
            WpfTestHelper.Pump();

            var overlay = Overlay(window);
            overlayDuring = overlay.IsVisible;
            progressTextShown = WpfTestHelper.Descendants<TextBlock>(overlay)
                .First(t => AutomationProperties.GetName(t) == "更新の進み具合").Text;
            closeButtonEnabledDuring = WpfTestHelper.Descendants<Button>(window)
                .First(b => AutomationProperties.GetName(b) == "設定を閉じる").IsEnabled;
            // 項目は、タブの中にある（選んでいないタブの中身は、まだ作られていない）。「アプリ」のタブを選んでから探す
            WpfTestHelper.Descendants<TabControl>(window).Single().SelectedIndex = 2;
            WpfTestHelper.Pump();
            contentEnabledDuring = WpfTestHelper.Descendants<Button>(window)
                .First(b => AutomationProperties.GetName(b) == "更新を確認").IsEnabled;

            window.Close(); // 更新の間は、閉じられない
            stillOpenAfterCloseRequest = window.IsVisible;

            viewModel.IsInstallingUpdate = false;
            WpfTestHelper.Pump();
            openedAfterFinish = !Overlay(window).IsVisible;
            window.Close();
        });

        Assert.False(overlayBefore); // ふだんは、出ない
        Assert.True(overlayDuring);
        Assert.Equal("ダウンロードしています… 12.0 MB / 64.0 MB", progressTextShown);
        Assert.False(closeButtonEnabledDuring);
        Assert.False(contentEnabledDuring); // 下の項目は、キーボードでも操作できない
        Assert.True(stillOpenAfterCloseRequest);
        Assert.True(openedAfterFinish);
    }

    [Fact]
    public void 入れ替えの間のウィンドウは_更新後の版を表示し_閉じられず_許可すると閉じる()
    {
        var titleText = string.Empty;
        var visibleAfterClose = false;
        var visibleAfterAllow = true;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new UpdateApplyWindow("0.3.0"));
            window.Show();
            WpfTestHelper.Pump();
            titleText = ((TextBlock)window.FindName("TitleText")).Text;

            window.Close();
            visibleAfterClose = window.IsVisible; // 入れ替えの間は、閉じられない

            window.AllowClose();
            window.Close();
            visibleAfterAllow = window.IsVisible;
        });

        Assert.Equal("35Viewer を v0.3.0 に更新しています", titleText);
        Assert.True(visibleAfterClose);
        Assert.False(visibleAfterAllow);
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
