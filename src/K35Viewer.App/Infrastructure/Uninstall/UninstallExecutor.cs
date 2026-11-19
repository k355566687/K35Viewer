using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Infrastructure.Uninstall;

/// <summary>
/// アンインストールの実行（GitHub の Issue #24）。消すのは、次のものだけ。
/// ・作ったショートカット
/// ・データのフォルダの、35Viewer が作る既知のもの（DB、設定、サムネイル、ログ、バックアップ、一時のファイル）。それ以外のファイルがあれば、残す
/// ・プログラムのフォルダ（中身ごと）
/// 管理フォルダの中のファイル（アーカイブ）には、さわらない。消す直前に、もう一度、安全を確かめる（確かめて、問題があれば、何も消さない）。
/// 1つ消せなくても、残りは続ける（消せなかったものは、結果に入れる）。リンク（ジャンクション、シンボリックリンク）は、リンクだけを消し、先には触れない。
/// </summary>
public sealed class UninstallExecutor(UninstallPlanner planner)
{
    public UninstallReport Execute(UninstallTargets targets, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        // 消す直前の確認。引き継いだあとに、状況が変わっていても、巻き込まない
        var executable = Path.Combine(targets.ProgramDirectory, UninstallPlanner.ExecutableFileName);
        var plan = planner.Plan(executable, targets.DataDirectory, targets.LibraryFolders, targets.ShortcutFiles);
        if (!plan.CanRun)
        {
            return UninstallReport.AbortedWith(plan.Blockers);
        }

        var failures = new List<UninstallFailure>();

        progress?.Report("ショートカットを削除しています…");
        var deletedShortcuts = 0;
        foreach (var shortcut in targets.ShortcutFiles)
        {
            if (File.Exists(shortcut))
            {
                var before = failures.Count;
                DeleteFile(shortcut, failures);
                if (failures.Count == before)
                {
                    deletedShortcuts++;
                }
            }
        }

        progress?.Report("データを削除しています…");
        var leftovers = DeleteData(targets.DataDirectory, failures);

        progress?.Report("プログラムを削除しています…");
        DeleteTree(targets.ProgramDirectory, failures);

        return new UninstallReport(
            false,
            [],
            deletedShortcuts,
            !Directory.Exists(targets.ProgramDirectory),
            !Directory.Exists(targets.DataDirectory),
            leftovers,
            failures);
    }

    // データのフォルダ。35Viewer が作る既知のものだけを消す。ほかのものがあれば、フォルダごと残して、一覧を返す
    private static List<string> DeleteData(string dataDirectory, List<UninstallFailure> failures)
    {
        var leftovers = new List<string>();
        if (!Directory.Exists(dataDirectory))
        {
            return leftovers;
        }

        var paths = new AppPaths(dataDirectory);
        string[] files =
        [
            paths.DatabaseFile,
            paths.DatabaseFile + "-wal",
            paths.DatabaseFile + "-shm",
            paths.DatabaseFile + "-journal",
            paths.SettingsFile,
            paths.SettingsFile + ".tmp",
            paths.SettingsFile + ".broken",
            paths.RestorePendingFile,
            paths.UpdateResultFile,
        ];
        string[] directories =
        [
            paths.ThumbnailsDirectory,
            paths.LogsDirectory,
            paths.TempDirectory,
            paths.BackupsDirectory,
            paths.RestoreStagingDirectory,
            paths.RestoreRollbackDirectory,
            paths.UpdateStagingDirectory,
            paths.UpdateRollbackDirectory,
        ];

        foreach (var file in files)
        {
            DeleteFile(file, failures);
        }

        foreach (var directory in directories)
        {
            DeleteTree(directory, failures);
        }

        try
        {
            var rest = Directory.EnumerateFileSystemEntries(dataDirectory).ToList();
            if (rest.Count == 0)
            {
                Directory.Delete(dataDirectory);
            }
            else
            {
                leftovers.AddRange(rest);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new UninstallFailure(dataDirectory, ex.Message));
        }

        return leftovers;
    }

    private static void DeleteFile(string path, List<UninstallFailure> failures)
    {
        try
        {
            if (File.Exists(path))
            {
                // 読み取り専用でも消せるようにする
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new UninstallFailure(path, ex.Message));
        }
    }

    // フォルダを、中身ごと消す。リンクは、リンクだけを消して、先には触れない。消せないものは、記録して、残りを続ける
    private static void DeleteTree(string directory, List<UninstallFailure> failures)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return;
        }

        try
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete();
                return;
            }

            foreach (var entry in info.EnumerateFileSystemInfos().ToList())
            {
                if (entry is DirectoryInfo child)
                {
                    DeleteTree(child.FullName, failures);
                }
                else
                {
                    DeleteFile(entry.FullName, failures);
                }
            }

            info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new UninstallFailure(directory, ex.Message));
        }
    }
}
