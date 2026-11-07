using System.Windows;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectories();

        var builder = Host.CreateApplicationBuilder();
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
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton<ToastHostViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        LogStarted(logger, paths.Root);

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

        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "35Viewer started. DataRoot={Root}")]
    private static partial void LogStarted(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Critical, Message = "DBの初期化に失敗しました。")]
    private static partial void LogDatabaseInitializationFailed(ILogger logger, Exception exception);

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
