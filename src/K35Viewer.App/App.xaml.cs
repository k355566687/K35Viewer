using System.Windows;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Infrastructure.Logging;
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
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<IArchiveWriter, ArchiveWriter>();
        builder.Services.AddSingleton<IRecycleBinService, RecycleBinService>();
        builder.Services.AddSingleton<IExplorerLauncher, ExplorerLauncher>();
        builder.Services.AddSingleton<ArchiveService>();
        builder.Services.AddSingleton<StartupMaintenance>();
        builder.Services.AddSingleton<ViewerService>();
        builder.Services.AddSingleton<ViewerImageDecoder>();
        builder.Services.AddSingleton<LibraryViewModel>();
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
            ShowMessage = message => MessageBox.Show(message, "35Viewer", MessageBoxButton.OK, MessageBoxImage.Warning),
        };
        _exceptionReporter.Attach(Dispatcher);

        try
        {
            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        }
        catch (Exception ex)
        {
            LogDatabaseInitializationFailed(logger, ex);
            MessageBox.Show(
                $"データベースの初期化に失敗しました。\n{ex.Message}\n\nログ: {paths.LogsDirectory}",
                "35Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 前回の異常終了などで残った一時ファイルを片付ける（画面の表示を待たせない）
        var maintenance = _host.Services.GetRequiredService<StartupMaintenance>();
        _host.Services.GetRequiredService<MainWindow>().Show();
        _ = maintenance.RunAsync();
    }

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
