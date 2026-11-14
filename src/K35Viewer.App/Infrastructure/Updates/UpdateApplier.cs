using System.Text.Json;
using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Infrastructure.Updates;

/// <summary>更新の入れ替えの結果。次に起動したアプリが、利用者へ知らせる（<see cref="AppPaths.UpdateResultFile"/>）。</summary>
public sealed record UpdateResult(bool Succeeded, string Message, string Version);

/// <summary>
/// アプリのフォルダの入れ替え（要件定義 24.6）。ダウンロードして展開した<b>新しい版</b>の実行ファイルが、自分自身の一式を、
/// アプリのフォルダへ重ねる（古いアプリは、終了している。新しい版は、展開したフォルダから動くため、アプリのフォルダのファイルは使われていない）。
/// 置き換えるファイルは、先に退避しておき、途中で失敗したら、置いたファイルを消して、退避したファイルを戻す。
/// </summary>
public static class UpdateApplier
{
    /// <summary>入れ替えの専用の起動を表す引数。続けて、アプリのフォルダを渡す。</summary>
    public const string ApplyArgument = "--apply-update";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>入れ替えの専用の起動のための引数。</summary>
    public static string ArgumentsFor(string targetDirectory, int waitProcessId) =>
        $"{ApplyArgument} \"{targetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\" {RestartArguments.ForProcess(waitProcessId)}";

    /// <summary>引数に、入れ替えの専用の起動があれば、入れ替える先のフォルダを返す。</summary>
    public static string? ParseApplyTarget(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], ApplyArgument, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="sourceDirectory"/> の中身を、<paramref name="targetDirectory"/> へ重ねる。
    /// 置き換えるファイルは <paramref name="rollbackDirectory"/> へ退避する（成功したら、退避したファイルは消す）。
    /// 例外は投げない（失敗は結果で返す。元に戻せなかった場合は、退避したファイルを残す）。
    /// </summary>
    public static UpdateResult Apply(string sourceDirectory, string targetDirectory, string rollbackDirectory, string version)
    {
        var moved = new List<(string Original, string Saved)>();
        var placed = new List<string>();
        try
        {
            if (Directory.Exists(rollbackDirectory))
            {
                Directory.Delete(rollbackDirectory, recursive: true);
            }

            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, source);
                var target = Path.Combine(targetDirectory, relative);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var saved = Path.Combine(rollbackDirectory, relative);
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    Retry(() => File.Move(target, saved));
                    moved.Add((target, saved));
                }

                Retry(() => File.Copy(source, target, overwrite: false));
                placed.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                foreach (var file in placed.Where(File.Exists))
                {
                    File.Delete(file);
                }

                foreach (var (original, saved) in moved)
                {
                    File.Move(saved, original, overwrite: true);
                }
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                return new UpdateResult(
                    false,
                    $"更新できず、元の版にも戻せませんでした。アプリのフォルダを確認してください（{rollback.Message}）",
                    version);
            }

            TryDelete(rollbackDirectory);
            return new UpdateResult(false, $"更新できませんでした。元の版で起動しました（{ex.Message}）", version);
        }

        TryDelete(rollbackDirectory);
        return new UpdateResult(true, $"35Viewer を v{version} に更新しました", version);
    }

    /// <summary>入れ替えの結果を書く（次に起動したアプリが読む）。</summary>
    public static void WriteResult(AppPaths paths, UpdateResult result)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            File.WriteAllText(paths.UpdateResultFile, JsonSerializer.Serialize(result, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 結果を伝えられなくても、更新自体の成否には影響しない
        }
    }

    /// <summary>前回の入れ替えの結果を読み、ファイルを消す。なければ null。</summary>
    public static UpdateResult? TakeResult(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            if (!File.Exists(paths.UpdateResultFile))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(paths.UpdateResultFile), Options);
            File.Delete(paths.UpdateResultFile);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>ダウンロードして展開した、更新用のフォルダを片付ける（使用中なら、次の起動で片付ける）。</summary>
    public static void CleanupStaging(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        TryDelete(paths.UpdateStagingDirectory);
    }

    // ウイルス対策ソフトなどが、置いたばかりのファイルを一時的に掴むことがあるため、短い間隔で再試行する
    private static void Retry(Action action)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts && !IsPermanent(ex))
            {
                Thread.Sleep(200);
            }
        }
    }

    // 再試行しても変わらないもの（コピー先がすでにある）は、再試行しない
    private static bool IsPermanent(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException;

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 次の起動で、もう一度片付ける
        }
    }
}
