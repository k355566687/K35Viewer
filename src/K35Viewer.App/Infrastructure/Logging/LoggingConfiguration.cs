using K35Viewer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace K35Viewer.Infrastructure.Logging;

/// <summary>
/// Serilogによるローカルファイルログの設定。日次ローリング・1ファイル20MB上限・30日保持。
/// </summary>
public static class LoggingConfiguration
{
    private const long FileSizeLimitBytes = 20L * 1024 * 1024;
    private const int RetainedFileCount = 30;

    public static Serilog.Core.Logger CreateLogger(AppPaths paths, LogEventLevel minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(
                path: Path.Combine(paths.LogsDirectory, "k35viewer-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCount,
                shared: false,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static ILoggingBuilder AddK35ViewerLogging(this ILoggingBuilder builder, Serilog.ILogger logger)
    {
        builder.ClearProviders();
        builder.AddSerilog(logger, dispose: true);
        return builder;
    }

    public static LogEventLevel DefaultMinimumLevel =>
#if DEBUG
        LogEventLevel.Debug;
#else
        LogEventLevel.Information;
#endif
}
