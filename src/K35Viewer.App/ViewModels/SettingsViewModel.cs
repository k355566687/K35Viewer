using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>
/// 設定画面（SCR-03。画面一覧 8.3）の ViewModel。変更は、その場で <see cref="SettingsService"/> に反映し、保存する。
/// 開くたびに作り直す（閉じたあとに、古い表示が残らないようにする）。
/// </summary>
public sealed partial class SettingsViewModel(
    SettingsService settings,
    AppPaths paths,
    IExplorerLauncher explorer,
    INotificationService notifications,
    ILogger<SettingsViewModel> logger) : ObservableObject
{
    // 外観（ラジオボタンごとに1つの値。選ばれたものだけが true になる）
    public bool IsDarkTheme
    {
        get => settings.Current.Theme == AppTheme.Dark;
        set => SetTheme(AppTheme.Dark, value);
    }

    public bool IsLightTheme
    {
        get => settings.Current.Theme == AppTheme.Light;
        set => SetTheme(AppTheme.Light, value);
    }

    public bool IsSystemTheme
    {
        get => settings.Current.Theme == AppTheme.System;
        set => SetTheme(AppTheme.System, value);
    }

    /// <summary>アプリのデータの保存先。</summary>
    public string DataFolder => paths.Root;

    /// <summary>サムネイルのキャッシュの保存先。</summary>
    public string ThumbnailsFolder => paths.ThumbnailsDirectory;

    public string VersionText => $"{AppInfo.ProductName} {AppInfo.VersionText}";

    private void SetTheme(AppTheme theme, bool selected)
    {
        // ラジオボタンは、選ばれたものだけが true を送ってくる（外れたものの false は無視する）
        if (!selected || settings.Current.Theme == theme)
        {
            return;
        }

        settings.Update(s => s with { Theme = theme });
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
    }

    [RelayCommand]
    private void OpenDataFolder() => Open(() => explorer.OpenFolder(paths.Root));

    [RelayCommand]
    private void OpenThumbnailsFolder() => Open(() => explorer.OpenFolder(paths.ThumbnailsDirectory));

    [RelayCommand]
    private void OpenRepository() => Open(() => explorer.OpenWebPage(AppInfo.RepositoryPage));

    [RelayCommand]
    private void OpenLicense() => Open(() => explorer.OpenWebPage(AppInfo.LicensePage));

    private void Open(Action open)
    {
        try
        {
            open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            LogOpenFailed(logger, ex);
            notifications.ShowError("開けませんでした");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "フォルダ・ページを開けませんでした。")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
