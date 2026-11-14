using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Infrastructure.Updates;
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
    BackupService backups,
    RestoreService restore,
    IBackupDialogs backupDialogs,
    IAppRestarter restarter,
    UpdateService updates,
    IUpdateDialogs updateDialogs,
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

    // バックアップ（要件定義 24.5）

    /// <summary>自動バックアップを残す数。</summary>
    public int AutoBackupKeepCount
    {
        get => Math.Clamp(settings.Current.AutoBackupKeepCount, AppSettings.MinAutoBackupKeepCount, AppSettings.MaxAutoBackupKeepCount);
        set
        {
            var clamped = Math.Clamp(value, AppSettings.MinAutoBackupKeepCount, AppSettings.MaxAutoBackupKeepCount);
            settings.Update(s => s with { AutoBackupKeepCount = clamped });
            OnPropertyChanged();
            if (settings.Current.AutoBackupKeepCount == clamped)
            {
                // 数を減らした場合は、すぐに古い自動バックアップを整理する
                PruneAutoBackups();
            }
        }
    }

    /// <summary>「自動バックアップを残す数」の選択肢。</summary>
    public IReadOnlyList<int> AutoBackupKeepChoices { get; } =
        [.. Enumerable.Range(AppSettings.MinAutoBackupKeepCount, AppSettings.MaxAutoBackupKeepCount - AppSettings.MinAutoBackupKeepCount + 1)];

    /// <summary>最後のバックアップの表示（例: 「2026/11/13 10:20（手動）」。ないときは「まだありません」）。</summary>
    [ObservableProperty]
    private string _lastBackupText = "調べています…";

    /// <summary>バックアップの保存先。</summary>
    public string BackupsFolder => paths.BackupsDirectory;

    private bool _isBackingUp;

    private bool CanCreateBackup() => !_isBackingUp && !_isRestoring;

    /// <summary>アプリのデータのバックアップを作る。</summary>
    [RelayCommand(CanExecute = nameof(CanCreateBackup))]
    private async Task CreateBackupAsync()
    {
        _isBackingUp = true;
        CreateBackupCommand.NotifyCanExecuteChanged();
        try
        {
            var backup = await backups.CreateAsync(BackupKind.Manual);
            notifications.ShowSuccess($"バックアップを作成しました（{backup.FileName}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogBackupFailed(logger, ex);
            notifications.ShowError("バックアップを作成できませんでした");
        }
        finally
        {
            _isBackingUp = false;
            CreateBackupCommand.NotifyCanExecuteChanged();
            await RefreshLastBackupAsync();
        }
    }

    private bool _isRestoring;

    private bool CanRestoreBackup() => !_isRestoring && !_isBackingUp;

    /// <summary>
    /// バックアップを選んで、アプリのデータをその時点に戻す。確認のうえ、今のデータを自動でバックアップし、
    /// アプリを再起動して入れ替える（起動の最初に入れ替わる）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreBackup))]
    private async Task RestoreBackupAsync()
    {
        var file = backupDialogs.PickBackupFile(paths.BackupsDirectory);
        if (file is null)
        {
            return;
        }

        _isRestoring = true;
        RestoreBackupCommand.NotifyCanExecuteChanged();
        try
        {
            var check = await restore.CheckAsync(file);
            if (!check.IsValid)
            {
                notifications.ShowError(check.Error ?? "復元できません");
                return;
            }

            if (!backupDialogs.ConfirmRestore(check.Manifest!.CreatedAt.DateTime))
            {
                return;
            }

            await restore.PrepareAsync(file);
            if (!restarter.TryRestart())
            {
                // 予定は残してある。アプリを終了して、起動し直すと、入れ替わる
                notifications.ShowWarning("復元の準備はできましたが、アプリを再起動できませんでした。アプリを終了して、起動し直すと、復元されます");
            }
        }
        catch (RestoreRejectedException ex)
        {
            notifications.ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogBackupFailed(logger, ex);
            notifications.ShowError("復元の準備に失敗しました。データは変更していません");
        }
        finally
        {
            _isRestoring = false;
            RestoreBackupCommand.NotifyCanExecuteChanged();
        }
    }

    // アプリの更新（要件定義 24.6）

    /// <summary>今の版の表示（例: 0.2.0）。</summary>
    public string CurrentVersionText => updates.CurrentVersion.ToString(3);

    /// <summary>更新の確認・ダウンロードの状況の表示。</summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    /// <summary>確認や準備の最中か。この間は、確認と更新のボタンを押せず、「中止」を押せる。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    private bool _isUpdateBusy;

    /// <summary>確認で見つかった、新しい版。なければ null。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenReleaseNotesCommand))]
    private ReleaseInfo? _availableRelease;

    private CancellationTokenSource? _updateCts;

    private bool CanCheckForUpdate() => !IsUpdateBusy;

    /// <summary>GitHub のリリースに、新しい版があるかを確かめる（押したときだけ通信する）。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdate))]
    private async Task CheckForUpdateAsync()
    {
        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        IsUpdateBusy = true;
        AvailableRelease = null;
        UpdateStatusText = "確認しています…";
        try
        {
            var result = await updates.CheckAsync(cts.Token);
            switch (result.Status)
            {
                case UpdateCheckStatus.Available:
                    AvailableRelease = result.Release;
                    UpdateStatusText = result.Release!.IsInstallable
                        ? $"新しい版 {result.Release.DisplayVersion} があります"
                        : $"新しい版 {result.Release.DisplayVersion} があります（アプリの中では更新できません。「変更点を見る」から、手で入れ替えてください）";
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText = "最新の版です";
                    break;
                default:
                    UpdateStatusText = result.Message ?? "確認できませんでした";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "確認を中止しました";
        }
        finally
        {
            _updateCts = null;
            IsUpdateBusy = false;
        }
    }

    private bool CanOpenReleaseNotes() => AvailableRelease is not null;

    /// <summary>新しい版の変更点（リリースのページ）を、ブラウザで開く。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenReleaseNotes))]
    private void OpenReleaseNotes() => Open(() => explorer.OpenWebPage(AvailableRelease!.PageUrl));

    private bool CanInstallUpdate() => !IsUpdateBusy && AvailableRelease is { IsInstallable: true };

    /// <summary>
    /// 新しい版へ更新する。確認のうえ、今のデータをバックアップし、ダウンロードして、ハッシュで確かめ、
    /// アプリを再起動して入れ替える。更新できない場合は、理由を示す（リリースのページから、手で入れ替えられる）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        var release = AvailableRelease!;
        if (!updateDialogs.ConfirmUpdate(release.DisplayVersion))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        IsUpdateBusy = true;
        var progress = new Progress<UpdateProgress>(p => UpdateStatusText = DescribeProgress(p));
        try
        {
            await updates.InstallAsync(release, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "更新を中止しました。アプリは、このまま使えます";
        }
        catch (UpdateException ex)
        {
            UpdateStatusText = ex.OfferReleasePage ? ex.Message + "。「変更点を見る」から、手で入れ替えられます" : ex.Message;
            notifications.ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogUpdateFailed(logger, ex);
            UpdateStatusText = "更新の準備に失敗しました。アプリは、このまま使えます";
            notifications.ShowError(UpdateStatusText);
        }
        finally
        {
            _updateCts = null;
            IsUpdateBusy = false;
        }
    }

    private bool CanCancelUpdate() => IsUpdateBusy;

    /// <summary>確認やダウンロードを中止する。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelUpdate))]
    private void CancelUpdate() => _updateCts?.Cancel();

    private static string DescribeProgress(UpdateProgress progress) => progress.Phase switch
    {
        UpdatePhase.Downloading when progress.Total > 0 =>
            $"ダウンロードしています… {ByteSizeFormatter.Format(progress.Done)} / {ByteSizeFormatter.Format(progress.Total)}",
        UpdatePhase.Downloading => "ダウンロードしています…",
        UpdatePhase.Verifying => "ハッシュ（SHA-256）を確かめています…",
        UpdatePhase.Extracting => "展開しています…",
        UpdatePhase.BackingUp => "アプリのデータをバックアップしています…",
        _ => "新しい版を起動しています…",
    };

    [RelayCommand]
    private void OpenBackupsFolder() => Open(() => explorer.OpenFolder(paths.BackupsDirectory));

    private async Task RefreshLastBackupAsync()
    {
        try
        {
            var list = await Task.Run(backups.List);
            var latest = list.Count > 0 ? list[0] : null;
            LastBackupText = latest is null
                ? "まだありません"
                : string.Create(
                    System.Globalization.CultureInfo.CurrentCulture,
                    $"{latest.CreatedAt:yyyy/MM/dd HH:mm}（{(latest.Kind == BackupKind.Auto ? "自動" : "手動")}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupFailed(logger, ex);
            LastBackupText = "調べられませんでした";
        }
    }

    private void PruneAutoBackups()
    {
        try
        {
            backups.PruneAutoBackups(settings.Current.AutoBackupKeepCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupFailed(logger, ex);
        }

        _ = RefreshLastBackupAsync();
    }

    /// <summary>設定画面を開いたときに呼ぶ。キャッシュの大きさを調べ、削除できる状態の変化を追う。</summary>
    public async Task InitializeAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        thumbnailCache.PropertyChanged += OnThumbnailCacheChanged;
        await Task.WhenAll(RefreshCacheSizeAsync(), RefreshLastBackupAsync());
    }

    /// <summary>設定画面を閉じるときに呼ぶ（ライブラリ側の通知の購読をやめる）。</summary>
    public void Dispose()
    {
        thumbnailCache.PropertyChanged -= OnThumbnailCacheChanged;
        _updateCts?.Cancel();
    }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "更新の準備に失敗しました。")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "バックアップの作成または一覧の取得に失敗しました。")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "サムネイルのキャッシュの大きさを調べられませんでした。")]
    private static partial void LogCacheSizeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "サムネイルのキャッシュを削除できませんでした。")]
    private static partial void LogClearFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "フォルダ・ページを開けませんでした。")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
