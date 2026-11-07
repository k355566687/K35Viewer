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
public sealed record ScanTree(
    bool RootAccessible,
    IReadOnlyList<ScannedDirectory> Directories,
    int FailedCount)
{
    public static ScanTree Unavailable { get; } = new(false, [], 0);
}
