using K35Viewer.Domain;

namespace K35Viewer.Infrastructure.FileSystem;

/// <summary>Windowsファイルシステム操作の集約点。ViewModelやRepositoryから直接ファイルを操作しない。</summary>
public sealed class FileSystemService
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".cbz" };

    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { "$RECYCLE.BIN", "System Volume Information" };

    /// <summary>フォルダが存在し、内容を列挙できるか。</summary>
    public bool CanAccessDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>UNCパスまたはネットワークドライブなら true。</summary>
    public bool IsNetworkPath(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>ファイルの現在のサイズと更新日時(UTC Unix time)。存在しない・読めない場合は null。</summary>
    public (long FileSize, long LastWriteTime)? TryGetFileState(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists ? (info.Length, ToUnixSeconds(info)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 登録フォルダ配下を再帰的に列挙し、ZIP / CBZ とフォルダ構造を返す。
    /// 読み取れないフォルダはスキップして Failed として記録し、処理を継続する。
    /// シンボリックリンク等(ReparsePoint)のフォルダは循環を避けるため辿らない。
    /// </summary>
    public ScanTree ScanDirectoryTree(string rootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
        {
            return ScanTree.Unavailable;
        }

        var directories = new List<ScannedDirectory>();
        var failedCount = 0;
        var queue = new Queue<(DirectoryInfo Directory, string? ParentFullPath)>();
        queue.Enqueue((root, null));

        // 幅優先で処理するため、親フォルダは必ず子フォルダより先に directories へ入る
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, parentFullPath) = queue.Dequeue();
            var archives = new List<ScannedArchive>();
            var failed = false;

            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo child)
                    {
                        if (ShouldDescend(child))
                        {
                            queue.Enqueue((child, directory.FullName));
                        }
                    }
                    else if (entry is FileInfo file && ArchiveExtensions.Contains(file.Extension))
                    {
                        archives.Add(new ScannedArchive(file.Name, file.Length, ToUnixSeconds(file)));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 途中まで読めた内容は信用せず、このフォルダ全体を「確認できなかった」扱いにする
                failed = true;
                archives.Clear();
                failedCount++;
            }

            directories.Add(new ScannedDirectory(directory.FullName, parentFullPath, failed, archives));
        }

        return new ScanTree(true, directories, failedCount);
    }

    private static bool ShouldDescend(DirectoryInfo directory) =>
        !IgnoredDirectoryNames.Contains(directory.Name)
        && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static long ToUnixSeconds(FileInfo file) =>
        UnixTime.ToUnixSeconds(new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
}
