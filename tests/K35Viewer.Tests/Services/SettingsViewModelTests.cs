using System.Windows;
using System.Windows.Controls;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using K35Viewer.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>設定画面（SCR-03。画面一覧 8.3）と、表示方式の記憶（要件定義 24.4）。</summary>
[Collection(WpfViewsGroup.Name)]
public class SettingsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SettingsViewModel CreateViewModel(LibraryTestContext ctx, K35Viewer.Services.SettingsService settings) =>
        ctx.CreateSettingsViewModel(settings, new NoCacheMaintenance());

    [Fact]
    public void 外観を選ぶと_その場で保存され_選んだものだけが選ばれた状態になる()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        var viewModel = CreateViewModel(ctx, settings);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(viewModel.IsDarkTheme);
        viewModel.IsLightTheme = true;
        viewModel.IsDarkTheme = false; // ラジオボタンが外れたときの false は無視する

        Assert.Equal(AppTheme.Light, settings.Current.Theme);
        Assert.Equal(AppTheme.Light, ctx.CreateSettings().Current.Theme); // 保存されている（再起動しても残る）
        Assert.False(viewModel.IsDarkTheme);
        Assert.True(viewModel.IsLightTheme);
        Assert.Contains(nameof(SettingsViewModel.IsDarkTheme), changed);

        viewModel.IsSystemTheme = true;
        Assert.Equal(AppTheme.System, settings.Current.Theme);
    }

    [Fact]
    public void 保存場所を表示し_フォルダとGitHubのページを開ける()
    {
        using var ctx = new LibraryTestContext();
        var viewModel = CreateViewModel(ctx, ctx.CreateSettings());
        var paths = new AppPaths(ctx.CacheDirectory);

        viewModel.OpenDataFolderCommand.Execute(null);
        viewModel.OpenThumbnailsFolderCommand.Execute(null);
        viewModel.OpenRepositoryCommand.Execute(null);
        viewModel.OpenLicenseCommand.Execute(null);

        Assert.Equal(paths.Root, viewModel.DataFolder);
        Assert.Equal(paths.ThumbnailsDirectory, viewModel.ThumbnailsFolder);
        Assert.Equal([paths.Root, paths.ThumbnailsDirectory], ctx.Explorer.OpenedFolders);
        Assert.Equal([AppInfo.RepositoryPage, AppInfo.LicensePage], ctx.Explorer.OpenedWebPages);
        Assert.StartsWith("35Viewer ", viewModel.VersionText, StringComparison.Ordinal);
    }

    [Fact]
    public void フォルダを開けなかった場合は_エラーを知らせる()
    {
        using var ctx = new LibraryTestContext();
        var viewModel = CreateViewModel(ctx, ctx.CreateSettings());
        ctx.Explorer.Failure = new System.ComponentModel.Win32Exception("起動できない");

        viewModel.OpenDataFolderCommand.Execute(null);

        Assert.Contains(ctx.Notifications.Items, n => n.Level == K35Viewer.Services.NotificationLevel.Error);
    }

    [Theory]
    [InlineData("https://github.com/k355566687/K35Viewer/releases", true)]
    [InlineData("http://github.com/k355566687/K35Viewer", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://github.com.example.com/", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    public void ブラウザで開けるのは_GitHubのhttpsのページだけ(string url, bool allowed)
    {
        Assert.Equal(allowed, ExplorerLauncher.IsAllowedWebPage(new Uri(url)));
    }

    [Fact]
    public void 設定画面を表示できる_選んでいる外観が選ばれている()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { Theme = AppTheme.System });
        var viewModel = CreateViewModel(ctx, settings);
        var checkedNames = new List<string>();
        var closeIsCancel = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new SettingsWindow(viewModel));
            window.Show();
            WpfTestHelper.Pump();
            checkedNames.AddRange(WpfTestHelper.Descendants<RadioButton>(window)
                .Where(r => r.IsChecked == true)
                .Select(r => System.Windows.Automation.AutomationProperties.GetName(r)));
            closeIsCancel = WpfTestHelper.Descendants<Button>(window).Single(b => (string)b.Content == "閉じる").IsCancel;
            window.Close();
        });

        Assert.Equal(["テーマ: Windowsの設定に合わせる"], checkedNames);
        Assert.True(closeIsCancel); // Esc で閉じる
    }

    [Fact]
    public async Task ライブラリの表示方式を記憶し_次の起動でも同じ表示方式で始める()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();

        using (var first = CreateLibrary(ctx, ctx.CreateSettings()))
        {
            await first.InitializeAsync();
            Assert.True(first.IsDetailView); // 既定は詳細表示
            first.IsThumbnailView = true;
        }

        Assert.Equal(LibraryViewMode.Thumbnail, ctx.CreateSettings().Current.LibraryViewMode);

        using var second = CreateLibrary(ctx, ctx.CreateSettings());
        await second.InitializeAsync();
        Assert.True(second.IsThumbnailView);
    }

    [Fact]
    public async Task ビューアの表示方式を記憶し_次に開いたときも同じ表示方式で始める()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(40, 30)), ("2.jpg", TestImages.Jpeg(40, 30)));
        await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.zip'");
        var settings = ctx.CreateSettings();
        using var viewer = new ViewerViewModel(
            ctx.Viewer, new ViewerImageDecoder(), new ThumbnailGenerator(), NullLogger<ViewerViewModel>.Instance, settings);

        Assert.True(await viewer.OpenAsync(id, Ct));
        Assert.False(viewer.IsListView); // 既定は1枚表示
        viewer.ToggleViewModeCommand.Execute(null);
        Assert.True(viewer.IsListView);
        await viewer.CloseAsync();

        Assert.Equal(ViewerViewMode.List, ctx.CreateSettings().Current.ViewerViewMode);
        Assert.True(await viewer.OpenAsync(id, Ct));
        Assert.True(viewer.IsListView); // 一覧表示で始まる
        Assert.Null(viewer.CurrentImage); // 一覧表示では、画像本体を読み込まない
        await viewer.CloseAsync();
    }

    private static LibraryViewModel CreateLibrary(LibraryTestContext ctx, K35Viewer.Services.SettingsService settings) =>
        new(ctx.Library, ctx.Scan, ctx.Thumbnails, ctx.ImageLoader, new NoDialogs(), ctx.Archives,
            NullLogger<LibraryViewModel>.Instance, settings);

    /// <summary>キャッシュの管理の代わり（このテストでは使わない）。</summary>
    private sealed class NoCacheMaintenance : K35Viewer.Services.IThumbnailCacheMaintenance
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

    private sealed class NoDialogs : K35Viewer.Services.IDialogService
    {
        public string? PickFolder() => null;

        public bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths) => false;

        public K35Viewer.Services.LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, string? updateUnavailableReason) =>
            K35Viewer.Services.LeaveChoice.Cancel;

        public string? PromptRename(string currentFileName, Func<string, string?> validate) => null;

        public string? PickMoveDestination(string initialDirectory) => null;

        public bool ConfirmDeleteArchive(string fileName) => false;

        public bool ConfirmPermanentDelete(string fileName, string reason) => false;

        public bool ConfirmRemoveLibraryFolder(string folderPath, int archiveCount) => false;

        public void ShowSettings()
        {
        }
    }
}
