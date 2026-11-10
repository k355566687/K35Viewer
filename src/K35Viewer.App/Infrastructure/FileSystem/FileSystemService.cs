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
    /// アーカイブの再構築で使う一時ファイルのパスを作る。置換時に別ボリュームへのコピーが起きないよう、元のファイルと
    /// 同じフォルダに置く。拡張子を .zip / .cbz にしないため、スキャンの対象にならない。
    /// </summary>
    public string CreateTempPath(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new ArgumentException("フォルダを特定できません", nameof(archivePath));
        return Path.Combine(directory, $"{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}{TempFileSuffix}");
    }

    /// <summary>一時ファイルの拡張子（スキャンの対象外）。</summary>
    public const string TempFileSuffix = ".k35tmp";

    /// <summary>ファイルを削除する。存在しない・削除できない場合は何もせず false（後始末用で、例外にしない）。</summary>
    public bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>ファイルを移動（名前変更）する。移動先に同名のファイルがあれば失敗する（上書きしない）。</summary>
    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);

    /// <summary>ファイルをコピーする（更新日時も引き継ぐ）。コピー先に同名のファイルがあれば失敗する（上書きしない）。</summary>
    public void CopyFile(string sourcePath, string destinationPath) => File.Copy(sourcePath, destinationPath, overwrite: false);

    /// <summary>ファイルを完全に削除する（ごみ箱へは移さない）。削除できない場合は例外。</summary>
    public void DeleteFile(string path) => File.Delete(path);

    public bool FileExists(string path) => File.Exists(path);

    /// <summary>
    /// 2つのパスが同じボリューム（ドライブ、またはUNCの共有）にあるか。同じなら移動は名前の変更で済み、
    /// 異なればコピーして元を削除する必要がある。
    /// </summary>
    public bool IsSameVolume(string pathA, string pathB) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(pathA)),
            Path.GetPathRoot(Path.GetFullPath(pathB)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ファイルの置き場所のドライブの空き容量（バイト）。UNCパスなど調べられない場合は null。
    /// </summary>
    public long? GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
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
