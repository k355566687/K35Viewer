using System.Windows;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Logging;
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
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        LogStarted(logger, paths.Root);

        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "35Viewer started. DataRoot={Root}")]
    private static partial void LogStarted(ILogger logger, string root);

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
