using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Infrastructure.Uninstall;

/// <summary>
/// アンインストールの前の、安全の確認（GitHub の Issue #24）。消す場所（プログラムのフォルダ、データのフォルダ）が、
/// 管理フォルダ（アーカイブ）や、Windows の大事なフォルダを巻き込まないことを確かめる。
/// 1つでも問題があれば、<see cref="UninstallPlan.Blockers"/> に理由を入れ、実行させない。
/// </summary>
public sealed class UninstallPlanner(IReadOnlyList<string> protectedDirectories, string temporaryDirectory)
{
    public const string ExecutableFileName = "K35Viewer.exe";

    private const string LibraryFileName = "K35Viewer.dll";

    /// <summary>プログラムのフォルダに、これより多くのファイルがあれば、35Viewer のフォルダではないとみなす（配布物は、約440個）。</summary>
    public const int MaxProgramFiles = 5000;

    /// <summary>プログラムのフォルダが、これより大きければ、35Viewer のフォルダではないとみなす（配布物は、約150 MB）。</summary>
    public const long MaxProgramBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>アーカイブらしいファイルの拡張子。プログラムのフォルダにあれば、巻き込まないよう、実行しない。</summary>
    public static readonly IReadOnlyList<string> ArchiveExtensions = [".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7"];

    /// <summary>Windows の大事なフォルダ、と、ユーザーのよく使うフォルダ。消す場所が、これと同じか、これを含んでいれば、実行しない。</summary>
    public static IReadOnlyList<string> DefaultProtectedDirectories()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.StartMenu,
        };
        var result = folders
            .Select(Environment.GetFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Append(KnownFolders.Downloads())
            .ToList();
        return result;
    }

    public static UninstallPlanner CreateDefault() => new(DefaultProtectedDirectories(), Path.GetTempPath());

    /// <summary>
    /// 確認する。ファイルシステムを調べる（プログラムのフォルダの中を数える）ため、画面のスレッドの外で呼ぶ。
    /// </summary>
    /// <param name="executablePath">今動いている実行ファイル。</param>
    /// <param name="dataDirectory">今使っているデータのフォルダ。</param>
    /// <param name="libraryFolders">登録されている管理フォルダ（消さないものを守るため）。</param>
    /// <param name="shortcutFiles">消すショートカット（リンク先が、この実行ファイルのもの）。</param>
    public UninstallPlan Plan(
        string? executablePath,
        string dataDirectory,
        IEnumerable<string> libraryFolders,
        IEnumerable<string> shortcutFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(libraryFolders);
        ArgumentNullException.ThrowIfNull(shortcutFiles);

        var blockers = new List<string>();
        var libraries = libraryFolders.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        var shortcuts = shortcutFiles.ToList();

        var executable = string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFullPath(executablePath);
        var programDirectory = executable is null ? string.Empty : (Path.GetDirectoryName(executable) ?? string.Empty);
        var data = Path.GetFullPath(dataDirectory);

        long programBytes = 0;
        var programFiles = 0;

        if (executable is null || !string.Equals(Path.GetFileName(executable), ExecutableFileName, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add($"35Viewer の実行ファイル（{ExecutableFileName}）から起動していないため、アンインストールできません");
        }
        else
        {
            CheckProgramDirectory(programDirectory, libraries, blockers, out programBytes, out programFiles);
        }

        CheckDataDirectory(data, libraries, blockers);

        if (programDirectory.Length > 0 && IsInside(temporaryDirectory, programDirectory))
        {
            blockers.Add("一時的な場所にあるアプリは、アンインストールできません（展開したフォルダを、ふだん使う場所へ移してください）");
        }

        var targets = new UninstallTargets(programDirectory, data, shortcuts, libraries);
        return new UninstallPlan(targets, blockers, programBytes, programFiles, DirectoryBytes(data));
    }

    /// <summary>バックアップの保存先として、使ってよいか。使えなければ、その理由。</summary>
    public static string? ValidateBackupFolder(UninstallTargets targets, string? folder)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "バックアップの保存先を選んでください";
        }

        string full;
        try
        {
            full = Path.GetFullPath(folder.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "バックアップの保存先の場所が正しくありません";
        }

        // 削除する場所の中には、保存できない（バックアップごと、消えてしまう）
        if (targets.ProgramDirectory.Length > 0 && IsInside(full, targets.ProgramDirectory))
        {
            return "プログラムのフォルダの中には、保存できません（アンインストールで消えるため）";
        }

        if (IsInside(full, targets.DataDirectory))
        {
            return "データのフォルダの中には、保存できません（アンインストールで消えるため）";
        }

        return null;
    }

    private void CheckProgramDirectory(
        string programDirectory,
        IReadOnlyList<string> libraries,
        List<string> blockers,
        out long bytes,
        out int fileCount)
    {
        bytes = 0;
        fileCount = 0;

        if (!File.Exists(Path.Combine(programDirectory, ExecutableFileName)) || !File.Exists(Path.Combine(programDirectory, LibraryFileName)))
        {
            blockers.Add($"プログラムのフォルダに、{ExecutableFileName} と {LibraryFileName} がそろっていません。35Viewer のフォルダではない可能性があるため、削除しません");
            return;
        }

        CheckNotProtected(programDirectory, "プログラムのフォルダ", blockers);
        CheckLibraries(programDirectory, libraries, "プログラムのフォルダ", blockers);

        var archive = ScanProgramDirectory(programDirectory, out bytes, out fileCount, out var scanError);
        if (scanError is not null)
        {
            blockers.Add($"プログラムのフォルダの中を調べられませんでした（{scanError}）");
        }
        else if (archive is not null)
        {
            blockers.Add($"プログラムのフォルダの中に、アーカイブらしいファイル（{archive}）があります。アーカイブを巻き込まないよう、削除しません。移すか、削除してから、アンインストールしてください");
        }
        else if (fileCount > MaxProgramFiles || bytes > MaxProgramBytes)
        {
            blockers.Add("プログラムのフォルダの中に、35Viewer の配布物としては多すぎるファイルがあります。ほかのファイルを巻き込まないよう、削除しません。手で確認してください");
        }

        if (!CanWrite(programDirectory))
        {
            blockers.Add("プログラムのフォルダに書き込めません（管理者の権限が要る場所の可能性があります）。アプリからは、削除できません");
        }
    }

    private static void CheckDataDirectory(string dataDirectory, IReadOnlyList<string> libraries, List<string> blockers)
    {
        // 消すのは、35Viewer が作る既知のものだけ。フォルダ自体が、大事な場所でないことと、管理フォルダを含まないことを確かめる
        if (IsDriveRoot(dataDirectory))
        {
            blockers.Add("データのフォルダが、ドライブの直下です。削除しません");
        }

        CheckLibraries(dataDirectory, libraries, "データのフォルダ", blockers);
    }

    private void CheckNotProtected(string directory, string label, List<string> blockers)
    {
        if (IsDriveRoot(directory))
        {
            blockers.Add($"{label}が、ドライブの直下です。削除しません");
            return;
        }

        var normalized = PathNormalizer.Normalize(directory);
        foreach (var protectedDirectory in protectedDirectories)
        {
            if (string.IsNullOrWhiteSpace(protectedDirectory))
            {
                continue;
            }

            // 消す場所が、大事なフォルダと同じか、それを中に含んでいる
            if (PathNormalizer.IsSameOrDescendant(PathNormalizer.Normalize(protectedDirectory), normalized))
            {
                blockers.Add($"{label}（{directory}）が、Windows の大事なフォルダ（{protectedDirectory}）と同じか、それを含んでいます。削除しません");
                return;
            }
        }
    }

    private static void CheckLibraries(string directory, IReadOnlyList<string> libraries, string label, List<string> blockers)
    {
        var normalized = PathNormalizer.Normalize(directory);
        foreach (var library in libraries)
        {
            // 管理フォルダが、消す場所と同じか、その中にある
            if (PathNormalizer.IsSameOrDescendant(PathNormalizer.Normalize(library), normalized))
            {
                blockers.Add($"管理フォルダ（{library}）が、{label}（{directory}）の中にあります。アーカイブを巻き込まないよう、削除しません。管理フォルダを、ほかの場所へ移してから、アンインストールしてください");
            }
        }
    }

    // プログラムのフォルダの中を調べる。リンク（ジャンクション、シンボリックリンク）の先は、たどらない。
    // アーカイブらしいファイルを見つけたら、その名前を返す
    private static string? ScanProgramDirectory(string root, out long bytes, out int fileCount, out string? error)
    {
        bytes = 0;
        fileCount = 0;
        error = null;
        string? archive = null;
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                var directory = new DirectoryInfo(pending.Pop());
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child.FullName);
                        continue;
                    }

                    fileCount++;
                    bytes += ((FileInfo)entry).Length;
                    if (archive is null && ArchiveExtensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase))
                    {
                        archive = entry.FullName;
                    }

                    if (fileCount > MaxProgramFiles || bytes > MaxProgramBytes)
                    {
                        return archive; // 多すぎる。これ以上、調べない
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }

        return archive;
    }

    private static long DirectoryBytes(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            long total = 0;
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child.FullName);
                    }
                    else
                    {
                        total += ((FileInfo)entry).Length;
                    }
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, ".k35-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(
            full.TrimEnd(Path.DirectorySeparatorChar),
            (Path.GetPathRoot(full) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInside(string path, string directory) =>
        PathNormalizer.IsSameOrDescendant(PathNormalizer.Normalize(path), PathNormalizer.Normalize(directory));
}
