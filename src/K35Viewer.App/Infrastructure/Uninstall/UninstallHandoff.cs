using System.Diagnostics;
using System.Text.Json;
using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Infrastructure.Uninstall;

/// <summary>控えの準備ができた。実行ファイルを、<see cref="Arguments"/> で起動する。</summary>
public sealed record UninstallHandoffInfo(string CopyDirectory, string ExecutablePath, string PlanFile);

/// <summary>
/// 自分自身を消すための引き継ぎ（GitHub の Issue #24）。実行中の実行ファイルは、そのままでは消せないため、
/// プログラムの一式を、一時的な場所（%TEMP%）へ写し、その控えを、<c>--uninstall</c> で起動して、消す処理を任せる
/// （アプリの更新で、新しい版が別の場所から入れ替えるのと同じ考え方）。控えの場所は、データのフォルダにも、プログラムのフォルダにも、置かない。
/// 最後に残る、控え自身は、少し待ってから、切り離した <c>cmd</c> が消す。
/// </summary>
public static class UninstallHandoff
{
    public const string UninstallArgument = "--uninstall";

    /// <summary>控えのフォルダの名前の先頭。消す前に、この名前であることを確かめる。</summary>
    public const string CopyDirectoryPrefix = "K35Viewer-uninstall-";

    private const string AppSubdirectory = "app";

    private const string PlanFileName = "plan.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>控えを起動する引数。続けて、終了を待つプロセスの番号を渡す。</summary>
    public static string ArgumentsFor(string planFile, int waitProcessId) =>
        $"{UninstallArgument} \"{planFile}\" {RestartArguments.ForProcess(waitProcessId)}";

    /// <summary>引数に、アンインストールの専用の起動があれば、予定のファイルを返す。</summary>
    public static string? ParsePlanFile(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], UninstallArgument, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// プログラムの一式を <paramref name="temporaryRoot"/> の下へ写し、予定のファイルを書く。
    /// 写す先が、消す場所の中にある場合は、準備しない（例外）。
    /// </summary>
    public static UninstallHandoffInfo Prepare(UninstallTargets targets, string temporaryRoot)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);

        var copy = Path.Combine(Path.GetFullPath(temporaryRoot), CopyDirectoryPrefix + Guid.NewGuid().ToString("N"));
        foreach (var guarded in new[] { targets.ProgramDirectory, targets.DataDirectory })
        {
            if (PathNormalizer.IsSameOrDescendant(PathNormalizer.Normalize(copy), PathNormalizer.Normalize(guarded)))
            {
                throw new InvalidOperationException("控えの場所が、消す場所の中にあります");
            }
        }

        var app = Path.Combine(copy, AppSubdirectory);
        try
        {
            CopyDirectory(targets.ProgramDirectory, app);
            var planFile = Path.Combine(copy, PlanFileName);
            File.WriteAllText(planFile, JsonSerializer.Serialize(targets, Options));
            return new UninstallHandoffInfo(copy, Path.Combine(app, UninstallPlanner.ExecutableFileName), planFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteCopy(copy);
            throw;
        }
    }

    /// <summary>予定のファイルを読む。読めなければ null。</summary>
    public static UninstallTargets? ReadPlan(string planFile)
    {
        try
        {
            return JsonSerializer.Deserialize<UninstallTargets>(File.ReadAllText(planFile), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 控えのフォルダを、少し待ってから消すよう、切り離した <c>cmd</c> に頼む。
    /// 控えのフォルダの名前と場所（%TEMP% の直下で、決まった先頭の名前）を確かめ、cmd で特別な意味を持つ文字を含むときは、頼まない
    /// （一時フォルダは、Windows が、あとで片付ける）。頼めたら true。
    /// </summary>
    public static bool TryScheduleCleanup(string copyDirectory, string temporaryRoot)
    {
        var full = Path.GetFullPath(copyDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(CopyDirectoryPrefix, StringComparison.Ordinal)
            || full.IndexOfAny(['&', '^', '%', '!', '|', '<', '>', '"']) >= 0)
        {
            return false;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{full}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>控えを消す（準備に失敗したときの後片付け）。消せなければ、そのまま。</summary>
    public static void TryDeleteCopy(string copyDirectory)
    {
        try
        {
            if (Directory.Exists(copyDirectory) && Path.GetFileName(copyDirectory.TrimEnd(Path.DirectorySeparatorChar)).StartsWith(CopyDirectoryPrefix, StringComparison.Ordinal))
            {
                Directory.Delete(copyDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時フォルダに残るだけ
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue; // リンクは写さない（先をたどらない）
            }

            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyDirectory(directory.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target, overwrite: true);
            }
        }
    }
}
