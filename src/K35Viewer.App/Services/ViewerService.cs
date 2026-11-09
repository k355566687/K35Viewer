using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>アーカイブを開いた結果。開けなかった場合は Session が null で、理由は通知済み(Message)。</summary>
public sealed record ViewerOpenResult(ViewerSession? Session, string? Message)
{
    public bool Succeeded => Session is not null;
}

/// <summary>
/// アーカイブ内部の閲覧に必要な処理（UC-05）。アーカイブを開いて画像を自然順に並べ、
/// 前回の閲覧位置を復元し、閲覧位置を記録する。
/// </summary>
public sealed partial class ViewerService(
    IArchiveReader reader,
    ArchiveRepository archives,
    DatabaseWriter writer,
    FileSystemService fileSystem,
    INotificationService notifications,
    ILogger<ViewerService> logger)
{
    /// <summary>
    /// アーカイブを開く。ファイルがない、壊れている、暗号化されている、画像がない場合は、
    /// 理由をトーストで通知して Session なしの結果を返す（呼び出し側はライブラリ画面に留まる）。
    /// 前回の閲覧位置が有効（その画像エントリが存在する）ならそこから、無効なら先頭から開く。
    /// </summary>
    public async Task<ViewerOpenResult> OpenAsync(long archiveId, CancellationToken ct = default)
    {
        var info = await archives.GetOpenInfoAsync(archiveId, ct);
        if (info is null || !string.Equals(info.AvailabilityStatus, "AVAILABLE", StringComparison.Ordinal))
        {
            return Fail("アーカイブが見つかりません。ライブラリを再スキャンしてください", warning: true);
        }

        if (fileSystem.TryGetFileState(info.FullPath) is null)
        {
            return Fail(
                Format($"「{info.FileName}」が見つかりません。ライブラリを再スキャンしてください"), warning: true);
        }

        var result = await Task.Run(() => reader.Open(info.FullPath, ct), ct);
        switch (result.Status)
        {
            case ArchiveAnalysisStatus.Ready when result.Archive is { } archive:
                var session = new ViewerSession(
                    info.Id, info.FileName, info.FullPath, archive, InitialIndex(info, archive.Images));
                LogOpened(logger, info.Id, session.Count, session.CurrentIndex);
                return new ViewerOpenResult(session, null);

            case ArchiveAnalysisStatus.Empty:
                return Fail(Format($"「{info.FileName}」には表示できる画像（JPEG / PNG）がありません"), warning: true);

            case ArchiveAnalysisStatus.Unsupported:
                return Fail(Format($"「{info.FileName}」は暗号化されているため開けません"), warning: true);

            case ArchiveAnalysisStatus.Broken:
                return Fail(
                    Format($"「{info.FileName}」を開けません。ファイルが破損している可能性があります"), warning: false);

            default:
                LogOpenFailed(logger, info.Id, result.ErrorCode);
                return Fail(
                    result.ErrorCode == "ACCESS_DENIED"
                        ? Format($"「{info.FileName}」を読み込めません（アクセスが拒否されました）")
                        : Format($"「{info.FileName}」を読み込めません"),
                    warning: false);
        }
    }

    /// <summary>現在の位置を、最後に閲覧した位置として記録する（DBへの書き込み）。</summary>
    public Task SaveLastViewAsync(ViewerSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 画像の位置は、保存する時点の値で固定する（保存の待ち中に位置が動いても、組が食い違わないように）
        var entry = session.CurrentEntry.FullName;
        var index = session.CurrentIndex;
        var archiveId = session.ArchiveId;

        return writer.ExecuteAsync(
            (connection, transaction, token) =>
                archives.UpdateLastViewAsync(connection, transaction, archiveId, entry, index, UnixTime.Now(), token),
            ct);
    }

    /// <summary>前回の閲覧位置が有効（画像エントリが存在する）ならその位置、無効なら先頭。</summary>
    private static int InitialIndex(ArchiveOpenInfo info, IReadOnlyList<ArchiveImageEntry> images)
    {
        if (info.LastViewEntry is null)
        {
            return 0;
        }

        for (var i = 0; i < images.Count; i++)
        {
            if (string.Equals(images[i].FullName, info.LastViewEntry, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    private ViewerOpenResult Fail(string message, bool warning)
    {
        if (warning)
        {
            notifications.ShowWarning(message);
        }
        else
        {
            notifications.ShowError(message);
        }

        return new ViewerOpenResult(null, message);
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "アーカイブを開きました。ArchiveId={Id} 画像数={Count} 開始位置={Index}")]
    private static partial void LogOpened(ILogger logger, long id, int count, int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "アーカイブを開けませんでした。ArchiveId={Id} ErrorCode={Code}")]
    private static partial void LogOpenFailed(ILogger logger, long id, string? code);
}
