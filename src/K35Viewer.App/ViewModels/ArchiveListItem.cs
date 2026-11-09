using System.Globalization;
using K35Viewer.Domain;

namespace K35Viewer.ViewModels;

/// <summary>ファイルサイズの表示用書式(1024単位)。</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} B");
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:N1} {Units[unit]}");
    }
}

/// <summary>
/// 一覧の1行。表示用の文字列は、画面に表示される行だけがバインド時に組み立てる
/// (1万件を一括で整形しない)。
/// </summary>
public sealed class ArchiveListItem(ArchiveSummary summary)
{
    public long Id => summary.Id;

    public string FileName => summary.FileName;

    /// <summary>保存場所(アーカイブを格納しているフォルダ)。</summary>
    public string Location => summary.FolderPath;

    /// <summary>未解析の場合は「－」。</summary>
    public string ImageCountText => summary.ImageCount is { } count
        ? count.ToString("N0", CultureInfo.CurrentCulture)
        : "－";

    public string FileSizeText => ByteSizeFormatter.Format(summary.FileSize);

    public string LastWriteTimeText => UnixTime.FromUnixSeconds(summary.LastWriteTime)
        .ToLocalTime()
        .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
}
