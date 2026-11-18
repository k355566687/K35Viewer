using System.Windows;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Backup;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Infrastructure.Logging;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Infrastructure.Shortcuts;
using K35Viewer.Infrastructure.Updates;
using K35Viewer.Services;
using K35Viewer.ViewModels;
using K35Viewer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace K35Viewer;

public partial class App : Application
{
    private IHost? _host;
    private UnhandledExceptionReporter? _exceptionReporter;
    private SingleInstanceGuard? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 更新の入れ替えのための起動（新しい版の実行ファイルが、アプリのフォルダへ自分を重ねる）。画面は出さない
        if (UpdateApplier.ParseApplyTarget(e.Args) is { } updateTarget)
        {
            await RunUpdateApplyAsync(e.Args, updateTarget);
            return;
        }

        // 復元のために再起動された場合は、古いプロセスが終わるまで待つ（待たないと、二重の起動として止められる）
        RestartArguments.WaitForPreviousProcess(e.Args);
        // 二重に起動された場合は、すでに動いている方を前面に出して終了する
        _singleInstance = SingleInstanceGuard.Acquire();
        if (!_singleInstance.IsFirstInstance)
        {
            SingleInstanceGuard.ActivateExistingInstance();
            Shutdown(0);
            return;
        }

        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectories();
        // 復元の予定があれば、DB を開く前に、データを入れ替える（要件定義 24.5）。結果は、画面を出してから知らせる
        var restoreResult = PendingRestore.TryApply(paths);
        // 前回の更新の結果（あれば）を、画面を出してから知らせる。ダウンロードして展開した更新用のフォルダは、片付ける
        var updateResult = UpdateApplier.TakeResult(paths);
        UpdateApplier.CleanupStaging(paths);

        // 基準のフォルダは、起動したときのカレントフォルダではなく、実行ファイルの場所にする
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSingleton(paths);
        builder.Logging.AddK35ViewerLogging(
            LoggingConfiguration.CreateLogger(paths, LoggingConfiguration.DefaultMinimumLevel));
        builder.Services.AddSingleton(new SqliteConnectionFactory(paths.DatabaseFile));
        builder.Services.AddSingleton<MigrationRunner>();
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddSingleton<DatabaseWriter>();
        builder.Services.AddSingleton<LibraryRepository>();
        builder.Services.AddSingleton<ArchiveRepository>();
        builder.Services.AddSingleton<ScanRepository>();
        builder.Services.AddSingleton<HistoryRepository>();
        builder.Services.AddSingleton<FileSystemService>();
        builder.Services.AddSingleton<NotificationService>();
        builder.Services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        builder.Services.AddSingleton<HistoryService>();
        builder.Services.AddSingleton<LibraryService>();
        builder.Services.AddSingleton<ScanService>();
        builder.Services.AddSingleton<IArchiveReader, ArchiveReader>();
        builder.Services.AddSingleton<ThumbnailGenerator>();
        builder.Services.AddSingleton(new ThumbnailCacheStore(paths.ThumbnailsDirectory));
        builder.Services.AddSingleton<ThumbnailRepository>();
        builder.Services.AddSingleton<ThumbnailService>();
        builder.Services.AddSingleton<ThumbnailImageLoader>(sp => new ThumbnailImageLoader(sp.GetRequiredService<ThumbnailCacheStore>()));
        builder.Services.AddSingleton(new SettingsStore(paths.SettingsFile));
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<RestoreService>();
        builder.Services.AddSingleton<IBackupDialogs, WpfBackupDialogs>();
        builder.Services.AddSingleton<IAppRestarter, WpfAppRestarter>();
        // 通信するのは、利用者が「更新を確認」「更新する」を押したときだけ。大きなダウンロードがあるため、全体の時間切れは設けず、呼び出しごとに決める
        builder.Services.AddSingleton(_ =>
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"35Viewer/{AppInfo.VersionText}");
            return client;
        });
        builder.Services.AddSingleton(sp => new GitHubReleaseClient(sp.GetRequiredService<HttpClient>()));
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<IUpdateDialogs, WpfUpdateDialogs>();
        builder.Services.AddSingleton<IUpdateLauncher, WpfUpdateLauncher>();
        builder.Services.AddSingleton<IShortcutService>(sp => new ShortcutService(
            ShortcutLocations.FromSystem(),
            Environment.ProcessPath,
            Path.GetTempPath(),
            sp.GetRequiredService<ILogger<ShortcutService>>()));
        builder.Services.AddSingleton<IShortcutDialogs, WpfShortcutDialogs>();
        builder.Services.AddSingleton<ShortcutFirstRun>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddSingleton<Func<SettingsViewModel>>(sp => sp.GetRequiredService<SettingsViewModel>);
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<IArchiveWriter, ArchiveWriter>();
        builder.Services.AddSingleton<IRecycleBinService, RecycleBinService>();
        builder.Services.AddSingleton<IExplorerLauncher, ExplorerLauncher>();
        builder.Services.AddSingleton<ArchiveService>();
        builder.Services.AddSingleton<StartupMaintenance>();
        builder.Services.AddSingleton<ViewerService>();
        builder.Services.AddSingleton<ViewerImageDecoder>();
        builder.Services.AddSingleton<LibraryViewModel>();
builder.Services.AddSingleton<IThumbnailCacheMaintenance>(sp => sp.GetRequiredService<LibraryViewModel>());
        builder.Services.AddSingleton<ViewerViewModel>();
        builder.Services.AddSingleton<ToastHostViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        LogStarted(logger, paths.Root);

        // 想定外の例外は、ログへ記録する。画面のスレッドの例外では、アプリを落とさずに知らせる
        _exceptionReporter = new UnhandledExceptionReporter(
            _host.Services.GetRequiredService<ILogger<UnhandledExceptionReporter>>(), paths.LogsDirectory)
        {
            ShowMessage = message => ConfirmDialog.Inform(MainWindow, "35Viewer", message),
        };
        _exceptionReporter.Attach(Dispatcher);

        try
        {
            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        }
        catch (Exception ex)
        {
            LogDatabaseInitializationFailed(logger, ex);

            // ダイアログを閉じたときに（最後のウィンドウが閉じたとして）終了コード 0 で終わらないよう、終了は明示的に行う
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ConfirmDialog.Inform(
                null,
                "35Viewer",
                "データベースの初期化に失敗しました。",
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}ログ: {paths.LogsDirectory}");
            Shutdown(1);
            return;
        }

        // 外観は、画面を表示する前に当てる。設定画面で変えたときは、その場で当て直す（要件定義 24.2）
        var settings = _host.Services.GetRequiredService<SettingsService>();
        ThemeManager.Apply(settings.Current.Theme);
        settings.Changed += (_, changed) => ThemeManager.Apply(changed.Theme);

        // 前回の異常終了などで残った一時ファイルを片付ける（画面の表示を待たせない）
        var maintenance = _host.Services.GetRequiredService<StartupMaintenance>();
        if (restoreResult.Outcome == RestoreApplyOutcome.Succeeded)
        {
            // 復元したDBは古い時点のもの。画面を出す（起動時のスキャンが始まる）前に、新着の印を付けない指定をしておく
            _host.Services.GetRequiredService<ScanService>().SuppressNewMarkingOnNextScan();
        }

        _host.Services.GetRequiredService<MainWindow>().Show();
        _ = maintenance.RunAsync();

        ReportRestoreResult(restoreResult, logger);
        ReportUpdateResult(updateResult);
        if (settings.LoadFailed)
        {
            _host.Services.GetRequiredService<NotificationService>().ShowWarning(
                "設定のファイルを読めなかったため、初期の設定で起動しました。設定画面で設定し直してください");
        }

        // はじめて起動したときの、ショートカットの確認（要件定義 24.8）。画面が表示されて、落ち着いてから出す
        var shortcutFirstRun = _host.Services.GetRequiredService<ShortcutFirstRun>();
        _ = Dispatcher.InvokeAsync(shortcutFirstRun.RunAsync, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 更新の入れ替えを行う（新しい版の実行ファイルとして、展開したフォルダから動いている）。
    /// 「更新しています」のウィンドウを出してから、古いアプリの終了を待ち、アプリのフォルダへ自分の一式を重ね、結果を書いて、
    /// アプリのフォルダの実行ファイルを起動する。成功なら新しい版、失敗なら元の版が起動する。
    /// 起動した画面が出るまで、ウィンドウを残す（古いアプリが消えてから、新しい画面が出るまでの間、何も表示されない時間をなくす）。
    /// </summary>
    private async Task RunUpdateApplyAsync(string[] args, string targetDirectory)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectories();

        // 外観は、設定のファイルに合わせる（読めなければ、既定のまま）
        try
        {
            ThemeManager.Apply(new SettingsStore(paths.SettingsFile).Load().Settings.Theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 既定の外観で、表示する
        }

        var window = new UpdateApplyWindow(AppInfo.VersionText);
        window.Show();

        // ファイルの入れ替えは、別のスレッドで行う（ウィンドウを固めない）
        var result = await Task.Run(() =>
        {
            RestartArguments.WaitForPreviousProcess(args);
            var applied = UpdateApplier.Apply(
                AppContext.BaseDirectory, targetDirectory, paths.UpdateRollbackDirectory, AppInfo.VersionText);
            UpdateApplier.WriteResult(paths, applied);
            return applied;
        });

        System.Diagnostics.Process? started = null;
        try
        {
            started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(targetDirectory, UpdateService.ExecutableName))
            {
                WorkingDirectory = targetDirectory,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 起動できなかった場合は、利用者が、アプリのフォルダの実行ファイルを起動する
        }

        if (started is not null)
        {
            using (started)
            {
                await Task.Run(() => WaitForMainWindow(started, TimeSpan.FromSeconds(20)));
            }
        }

        window.AllowClose();
        window.Close();
        Shutdown(result.Succeeded ? 0 : 1);
    }

    /// <summary>起動したプロセスの画面（メインウィンドウ）が出るまで待つ。終了した、または時間が過ぎたら、待つのをやめる。</summary>
    private static void WaitForMainWindow(System.Diagnostics.Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return;
                }

                Thread.Sleep(200);
            }
        }
        catch (InvalidOperationException)
        {
            // プロセスがすでにない
        }
    }
    private void ReportUpdateResult(UpdateResult? result)
    {
        if (result is null)
        {
            return;
        }

        var notifications = _host!.Services.GetRequiredService<NotificationService>();
        if (result.Succeeded)
        {
            notifications.ShowSuccess(result.Message);
        }
        else
        {
            notifications.ShowError(result.Message);
        }
    }

    private void ReportRestoreResult(RestoreApplyResult result, ILogger logger)
    {
        var notifications = _host!.Services.GetRequiredService<NotificationService>();
        switch (result.Outcome)
        {
            case RestoreApplyOutcome.Succeeded:
                LogRestored(logger);
                notifications.ShowSuccess(
                    result.BackupCreatedAt is { } createdAt
                        ? string.Create(
                            System.Globalization.CultureInfo.CurrentCulture,
                            $"{createdAt.DateTime:yyyy/MM/dd HH:mm} のバックアップから復元しました。サムネイルを作り直します")
                        : "バックアップから復元しました。サムネイルを作り直します");
                break;
            case RestoreApplyOutcome.Failed:
                LogRestoreFailed(logger, result.Message ?? string.Empty);
                notifications.ShowError(result.Message ?? "復元できませんでした");
                break;
            default:
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "バックアップから復元しました。")]
    private static partial void LogRestored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "復元できませんでした。{Message}")]
    private static partial void LogRestoreFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "35Viewer started. DataRoot={Root}")]
    private static partial void LogStarted(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Critical, Message = "DBの初期化に失敗しました。")]
    private static partial void LogDatabaseInitializationFailed(ILogger logger, Exception exception);

    protected override async void OnExit(ExitEventArgs e)
    {
        _exceptionReporter?.Detach(Dispatcher);
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
