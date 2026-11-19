using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Uninstall;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>確認のダイアログで、利用者が選んだこと。<see cref="BackupFolder"/> は、バックアップを保存する場所（保存しないなら null）。</summary>
public sealed record UninstallChoice(string? BackupFolder);

/// <summary>アンインストールの確認のダイアログ（GitHub の Issue #24）。</summary>
public interface IUninstallDialogs
{
    /// <summary>
    /// 何を消し、何を消さないかを示して、確認する。キャンセルの場合は null。
    /// <paramref name="validateBackupFolder"/> は、バックアップの保存先として使ってよいかを調べ、使えなければ、その理由を返す。
    /// </summary>
    UninstallChoice? Prompt(UninstallPlan plan, string defaultBackupFolder, Func<string, string?> validateBackupFolder);
}

/// <summary>消す処理を引き継ぐ控えを起動して、このアプリを終了する（実行中の実行ファイルは、自分では消せないため）。</summary>
public interface IUninstallLauncher
{
    /// <summary>控えを用意して、起動する。画面のスレッドの外で呼んでよい。できなければ false で、<paramref name="reason"/> に理由。</summary>
    bool TryStart(UninstallTargets targets, out string? reason);

    /// <summary>このアプリを終了する（画面のスレッドから呼ぶ）。</summary>
    void ExitApplication();
}

/// <summary>アンインストールを始めた結果。始められなかった場合は、何も消していない。</summary>
public sealed record UninstallStartResult(bool Started, string? Error = null, string? BackupFile = null);

/// <summary>アンインストール（要件定義 24.9）。確認、バックアップ、控えの起動までを行う。消す処理は、控えが行う。</summary>
public interface IUninstallService
{
    /// <summary>バックアップの保存先の、初めの候補（Windows の「ダウンロード」フォルダ）。</summary>
    string DefaultBackupFolder { get; }

    /// <summary>消すものと、実行できない理由（あれば）を調べる。ファイルを調べるため、少し時間がかかる。</summary>
    Task<UninstallPlan> PlanAsync(CancellationToken ct = default);

    /// <summary>バックアップの保存先として使ってよいか。使えなければ、その理由。</summary>
    string? ValidateBackupFolder(UninstallPlan plan, string? folder);

    /// <summary>
    /// 始める。もう一度、安全を確かめ、バックアップを保存し（選んだ場合）、控えを起動して、このアプリを終了する。
    /// バックアップを保存できなかった場合は、何も消さずに、理由を返す。例外は投げない。
    /// </summary>
    Task<UninstallStartResult> StartAsync(UninstallPlan plan, string? backupFolder, CancellationToken ct = default);
}

public sealed partial class UninstallService(
    UninstallPlanner planner,
    IShortcutService shortcuts,
    LibraryService library,
    BackupService backups,
    IUninstallLauncher launcher,
    AppPaths paths,
    string? executablePath,
    string defaultBackupFolder,
    ILogger<UninstallService> logger) : IUninstallService
{
    public string DefaultBackupFolder => defaultBackupFolder;

    public async Task<UninstallPlan> PlanAsync(CancellationToken ct = default)
    {
        var libraryFolders = (await library.GetLibraryFoldersAsync(ct)).Select(folder => folder.Path).ToList();

        // 消すショートカットは、このアプリを指しているものだけ（別の場所のアプリのものには、さわらない）
        var shortcutFiles = new[] { ShortcutPlace.StartMenu, ShortcutPlace.Desktop }
            .Where(place => shortcuts.GetState(place) == ShortcutState.Current)
            .Select(shortcuts.PathOf)
            .ToList();

        return await Task.Run(() => planner.Plan(executablePath, paths.Root, libraryFolders, shortcutFiles), ct);
    }

    public string? ValidateBackupFolder(UninstallPlan plan, string? folder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return UninstallPlanner.ValidateBackupFolder(plan.Targets, folder);
    }

    public async Task<UninstallStartResult> StartAsync(UninstallPlan plan, string? backupFolder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            // 確認のダイアログを見ている間に、状況が変わっていないかを、もう一度確かめる
            var fresh = await PlanAsync(ct);
            if (!fresh.CanRun)
            {
                return new UninstallStartResult(false, fresh.Blockers[0]);
            }

            string? backupFile = null;
            if (backupFolder is not null)
            {
                if (ValidateBackupFolder(fresh, backupFolder) is { } folderError)
                {
                    return new UninstallStartResult(false, folderError);
                }

                backupFile = await SaveBackupAsync(backupFolder, ct);
            }

            var started = await Task.Run(() => (Ok: launcher.TryStart(fresh.Targets, out var reason), Error: reason), ct);
            if (!started.Ok)
            {
                LogLaunchFailed(logger, started.Error ?? string.Empty);
                return new UninstallStartResult(false, started.Error ?? "アンインストールを始められませんでした", backupFile);
            }

            LogStarted(logger);
            launcher.ExitApplication();
            return new UninstallStartResult(true, null, backupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            LogFailed(logger, ex);
            return new UninstallStartResult(false, $"アンインストールを始められませんでした（{ex.Message}）。何も削除していません");
        }
    }

    // 今のデータのバックアップを作り、選ばれた場所へ写す（データのフォルダの中のものは、消えるため、写したものが残る）
    private async Task<string> SaveBackupAsync(string folder, CancellationToken ct)
    {
        _ = Directory.CreateDirectory(folder);
        var backup = await backups.CreateAsync(BackupKind.Manual, ct: ct);
        var destination = Path.Combine(folder, backup.FileName);
        for (var number = 2; File.Exists(destination); number++)
        {
            destination = Path.Combine(
                folder,
                $"{Path.GetFileNameWithoutExtension(backup.FileName)}-{number}{Path.GetExtension(backup.FileName)}");
        }

        File.Copy(backup.FilePath, destination);
        if (new FileInfo(destination).Length != new FileInfo(backup.FilePath).Length)
        {
            File.Delete(destination);
            throw new IOException("保存したバックアップの大きさが、元と合いません");
        }

        LogBackupSaved(logger, destination);
        return destination;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "アンインストール前のバックアップを保存しました。{Path}")]
    private static partial void LogBackupSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "アンインストールを始めました（控えを起動して、アプリを終了します）。")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "アンインストールの控えを起動できませんでした。{Reason}")]
    private static partial void LogLaunchFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "アンインストールを始められませんでした。")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
