using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Infrastructure.Logging;

/// <summary>
/// 想定外の例外（どこでも処理されなかった例外）を、ログへ記録する（開発環境設計書 Phase 10「例外処理」）。
/// <list type="bullet">
/// <item>画面のスレッドの例外: 記録し、利用者に知らせて、アプリは続ける（作業中のデータを失わないため）</item>
/// <item>待たれなかったタスクの例外: 記録だけ行い、アプリは続ける</item>
/// <item>それ以外のスレッドの例外: 記録する（.NETの既定どおり、アプリは終了する）</item>
/// </list>
/// 例外の詳細（スタックトレース）はログだけに残し、利用者にはログの場所を伝える。
/// </summary>
public sealed partial class UnhandledExceptionReporter(ILogger<UnhandledExceptionReporter> logger, string logsDirectory)
{
    /// <summary>画面のスレッドの例外を知らせる処理。テストでは差し替える。</summary>
    public Action<string> ShowMessage { get; init; } = _ => { };

    /// <summary>短い時間に同じ知らせが続くと操作できなくなるため、知らせる間隔の最小値。</summary>
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(5);

    private DateTime _lastNotified = DateTime.MinValue;

    public void Attach(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    public void Detach(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
    }

    /// <summary>画面のスレッドで処理されなかった例外。記録し、知らせて、処理済みにする（アプリは続ける）。</summary>
    internal void HandleUiException(Exception exception)
    {
        LogUiException(logger, exception);

        var now = DateTime.UtcNow;
        if (now - _lastNotified < NotifyInterval)
        {
            return;
        }

        _lastNotified = now;
        ShowMessage(
            "予期しないエラーが発生しました。操作は続けられますが、直前の操作が完了していない可能性があります。" +
            $"{Environment.NewLine}{Environment.NewLine}詳細はログに記録しました: {logsDirectory}");
    }

    internal void HandleUnobservedTaskException(Exception exception) => LogUnobservedTaskException(logger, exception);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleUiException(e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleUnobservedTaskException(e.Exception);
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogFatal(logger, exception, e.IsTerminating);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "画面のスレッドで、処理されなかった例外が発生しました。")]
    private static partial void LogUiException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "待たれなかったタスクで、例外が発生しました。")]
    private static partial void LogUnobservedTaskException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "処理されなかった例外が発生しました。IsTerminating={IsTerminating}")]
    private static partial void LogFatal(ILogger logger, Exception exception, bool isTerminating);
}
