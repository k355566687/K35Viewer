namespace K35Viewer.Infrastructure.FileSystem;

/// <summary>列挙で見つかったZIP / CBZ。LastWriteTime はUTC Unix time(秒)。</summary>
public sealed record ScannedArchive(string FileName, long FileSize, long LastWriteTime);

/// <summary>
/// 列挙した1フォルダ。Failed が true の場合、中身を読み取れなかったことを表す
/// (配下の既存登録を「不在」と判定してはならない)。
/// </summary>
public sealed record ScannedDirectory(
    string FullPath,
    string? ParentFullPath,
    bool Failed,
    IReadOnlyList<ScannedArchive> Archives);

/// <summary>
/// フォルダ列挙の結果。Directories は親が必ず子より前に並ぶ。
/// RootAccessible が false の場合は登録フォルダ自体にアクセスできない(ネットワーク切断など)。
/// </summary>
/// <summary>列挙で見つかった、アプリが作る一時ファイル（〜.k35tmp）。異常終了などで残ったものの後始末に使う。</summary>
public sealed record ScannedTempFile(string FullPath, DateTime LastWriteTimeUtc);

public sealed record ScanTree(
    bool RootAccessible,
    IReadOnlyList<ScannedDirectory> Directories,
    int FailedCount,
    IReadOnlyList<ScannedTempFile>? TempFiles = null)
{
    public static ScanTree Unavailable { get; } = new(false, [], 0);
}
