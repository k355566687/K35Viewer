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
    IThumbnailCacheMaintenance thumbnailCache,
    INotificationService notifications,
    ILogger<SettingsViewModel> logger) : ObservableObject, IDisposable
{
    private bool _isClearing;
    private bool _started;

    // 確認（要件定義 24.3）
    /// <summary>アーカイブから離れるとき、削除予約を反映するか確認する。</summary>
    public bool ConfirmBeforeLeave
    {
        get => settings.Current.ConfirmBeforeLeave;
        set
        {
            settings.Update(s => s with { ConfirmBeforeLeave = value });
            OnPropertyChanged();
        }
    }

    /// <summary>アーカイブをごみ箱へ移すとき、確認する。</summary>
    public bool ConfirmBeforeDelete
    {
        get => settings.Current.ConfirmBeforeDelete;
        set
        {
            settings.Update(s => s with { ConfirmBeforeDelete = value });
            OnPropertyChanged();
        }
    }

    /// <summary>サムネイルのキャッシュの大きさの表示（例: 「120.5 MB」）。</summary>
    [ObservableProperty]
    private string _cacheSizeText = "計算中…";

    /// <summary>「キャッシュを削除」のツールチップ。押せないときは、その理由を示す。</summary>
    public string ClearCacheToolTip =>
        thumbnailCache.ClearBlockedReason
        ?? "サムネイルのキャッシュを削除します。削除したサムネイルは、順に作り直します（アーカイブ自体は変わりません）";

    /// <summary>設定画面を開いたときに呼ぶ。キャッシュの大きさを調べ、削除できる状態の変化を追う。</summary>
    public async Task InitializeAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        thumbnailCache.PropertyChanged += OnThumbnailCacheChanged;
        await RefreshCacheSizeAsync();
    }

    /// <summary>設定画面を閉じるときに呼ぶ（ライブラリ側の通知の購読をやめる）。</summary>
    public void Dispose() => thumbnailCache.PropertyChanged -= OnThumbnailCacheChanged;

    private void OnThumbnailCacheChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IThumbnailCacheMaintenance.ClearBlockedReason))
        {
            return;
        }

        OnPropertyChanged(nameof(ClearCacheToolTip));
        ClearThumbnailCacheCommand.NotifyCanExecuteChanged();
        if (thumbnailCache.ClearBlockedReason is null)
        {
            // スキャンや作成が終わった。キャッシュの大きさが変わっているため、調べ直す
            _ = RefreshCacheSizeAsync();
        }
    }

    private async Task RefreshCacheSizeAsync()
    {
        try
        {
            CacheSizeText = ByteSizeFormatter.Format(await thumbnailCache.GetCacheSizeAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheSizeFailed(logger, ex);
            CacheSizeText = "調べられませんでした";
        }
    }

    private bool CanClearThumbnailCache() => !_isClearing && thumbnailCache.ClearBlockedReason is null;

    /// <summary>サムネイルのキャッシュを削除する。削除したサムネイルは、順に作り直す。</summary>
    [RelayCommand(CanExecute = nameof(CanClearThumbnailCache))]
    private async Task ClearThumbnailCacheAsync()
    {
        _isClearing = true;
        ClearThumbnailCacheCommand.NotifyCanExecuteChanged();
        try
        {
            if (await thumbnailCache.ClearCacheAsync())
            {
                notifications.ShowSuccess("サムネイルのキャッシュを削除しました。サムネイルを順に作り直します");
            }
            else
            {
                notifications.ShowWarning(thumbnailCache.ClearBlockedReason ?? "今は削除できません。しばらくしてから操作してください");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogClearFailed(logger, ex);
            notifications.ShowError("サムネイルのキャッシュを削除できませんでした");
        }
        finally
        {
            _isClearing = false;
            ClearThumbnailCacheCommand.NotifyCanExecuteChanged();
            await RefreshCacheSizeAsync();
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "サムネイルのキャッシュの大きさを調べられませんでした。")]
    private static partial void LogCacheSizeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "サムネイルのキャッシュを削除できませんでした。")]
    private static partial void LogClearFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "フォルダ・ページを開けませんでした。")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
