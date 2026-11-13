using K35Viewer.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>
/// アプリの設定の窓口（要件定義 24.1）。起動時に設定のファイルを読み、変更のたびに保存する（その場で反映）。
/// 画面のスレッドから使う。保存に失敗しても、変更はアプリの実行中は有効（ログに記録し、次の変更でもう一度保存する）。
/// </summary>
public sealed partial class SettingsService
{
    private readonly SettingsStore _store;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(SettingsStore store, ILogger<SettingsService> logger)
    {
        _store = store;
        _logger = logger;
        try
        {
            var result = store.Load();
            Current = result.Settings;
            LoadFailed = result.WasCorrupted;
            if (result.WasCorrupted)
            {
                LogCorrupted(logger, store.FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
            LoadFailed = true;
            LogLoadFailed(logger, ex, store.FilePath);
        }
    }

    /// <summary>今の設定。</summary>
    public AppSettings Current { get; private set; }

    /// <summary>起動時に設定のファイルを読めず、既定値で始めた（利用者に知らせる）。</summary>
    public bool LoadFailed { get; }

    /// <summary>設定が変わったとき（変更後の設定）。</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>設定を変えて保存する。変わらなければ何もしない。保存できた場合は true。</summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var next = change(Current);
        if (next == Current)
        {
            return true;
        }

        Current = next;
        var saved = true;
        try
        {
            _store.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            saved = false;
            LogSaveFailed(_logger, ex, _store.FilePath);
        }

        Changed?.Invoke(this, next);
        return saved;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "設定のファイルが壊れていたため、既定値で起動しました。壊れたファイルは .broken として残しました。Path={Path}")]
    private static partial void LogCorrupted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "設定のファイルを読めなかったため、既定値で起動しました。Path={Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "設定を保存できませんでした。Path={Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
