using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>
/// 起動時の後始末（ユースケース定義書 6.2「異常終了後に残った一時ファイル」）。
/// サムネイルのキャッシュとアプリの一時フォルダに残った書きかけのファイルを削除する。
/// 管理フォルダの中に残った一時ファイル（〜.k35tmp）は、スキャンが片付ける（<see cref="ScanService"/>）。
/// 失敗しても起動は妨げない。
/// </summary>
public sealed partial class StartupMaintenance(
    ThumbnailCacheStore thumbnailCache,
    AppPaths paths,
    ILogger<StartupMaintenance> logger)
{
    /// <summary>これより新しいファイルは、書き込み中の可能性があるため触れない。テストでは短くする。</summary>
    internal TimeSpan MinimumAge { get; init; } = TimeSpan.FromMinutes(10);

    public Task<int> RunAsync() => Task.Run(Run);

    /// <summary>後始末を行い、削除したファイルの数を返す。</summary>
    internal int Run()
    {
        var deleted = 0;
        try
        {
            // サムネイルの保存は「〜.jpg.〈GUID〉.tmp」へ書いてから置き換える。置き換える前に止まると残る
            deleted += DeleteOld(thumbnailCache.RootDirectory, "*.tmp");
            deleted += DeleteOld(paths.TempDirectory, "*");
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
        }

        if (deleted > 0)
        {
            LogCleaned(logger, deleted);
        }

        return deleted;
    }

    private int DeleteOld(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        var threshold = DateTime.UtcNow - MinimumAge;
        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 使用中などで削除できなければ、次の起動で再び試す
            }
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "起動時の後始末で、残っていた一時ファイルを削除しました。Count={Count}")]
    private static partial void LogCleaned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "起動時の後始末に失敗しました。")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
