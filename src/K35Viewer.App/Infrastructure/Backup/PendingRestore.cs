using System.Text.Json;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Backup;

/// <summary>バックアップの ZIP の中のファイルの名前。</summary>
public static class BackupEntries
{
    public const string Database = "library.db";

    public const string Settings = "settings.json";

    public const string Manifest = "manifest.json";
}

/// <summary>入れ替えの予定の目印（<see cref="AppPaths.RestorePendingFile"/>）の中身。</summary>
public sealed record PendingRestoreMarker(string SourceFile, string PreRestoreBackup, DateTimeOffset BackupCreatedAt, DateTimeOffset PreparedAt);

/// <summary>入れ替えの結果。</summary>
public enum RestoreApplyOutcome
{
    /// <summary>予定がなかった。</summary>
    NothingPending,

    /// <summary>復元できた。</summary>
    Succeeded,

    /// <summary>復元できず、元のデータに戻した。</summary>
    Failed,
}

/// <summary>入れ替えの結果と、利用者へ知らせる内容。</summary>
public sealed record RestoreApplyResult(RestoreApplyOutcome Outcome, string? Message = null, DateTimeOffset? BackupCreatedAt = null);

/// <summary>
/// バックアップからの復元の、データの入れ替え（要件定義 24.5）。アプリの起動の最初、DB を開く前に行う
/// （動いているアプリは、DB のファイルを使っているため、その場では入れ替えられない）。
/// 復元の準備（<see cref="K35Viewer.Services.RestoreService"/>）が、展開したファイルと、予定の目印を残している。
/// 入れ替えの途中で失敗した場合は、元のファイルに戻す。
/// </summary>
public static class PendingRestore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>入れ替えの予定の目印を書く。</summary>
    public static void WriteMarker(AppPaths paths, PendingRestoreMarker marker)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(marker);
        var temporary = paths.RestorePendingFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(marker, Options));
        File.Move(temporary, paths.RestorePendingFile, overwrite: true);
    }

    /// <summary>入れ替えの予定があれば、入れ替える。予定がなければ何もしない。例外は投げない（失敗は結果で返す）。</summary>
    public static RestoreApplyResult TryApply(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            if (Directory.Exists(paths.RestoreRollbackDirectory))
            {
                // 前回の入れ替えの途中で、アプリが止まった。元のファイルに戻す
                MoveBack(paths);
                Cleanup(paths);
                return new RestoreApplyResult(
                    RestoreApplyOutcome.Failed, "前回の復元が途中で止まったため、元のデータに戻しました");
            }

            if (!File.Exists(paths.RestorePendingFile))
            {
                return new RestoreApplyResult(RestoreApplyOutcome.NothingPending);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreApplyResult(RestoreApplyOutcome.Failed, $"復元の状態を確認できませんでした（{ex.Message}）");
        }

        PendingRestoreMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<PendingRestoreMarker>(File.ReadAllText(paths.RestorePendingFile), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            marker = null;
        }

        if (marker is null)
        {
            TryCleanup(paths);
            return new RestoreApplyResult(RestoreApplyOutcome.Failed, "復元の予定を読めなかったため、復元しませんでした");
        }

        try
        {
            Swap(paths);
        }
        catch (RollbackFailedException ex)
        {
            // 元に戻せなかった。退避したファイルを消さずに残し、次の起動で、もう一度戻す
            return new RestoreApplyResult(
                RestoreApplyOutcome.Failed,
                $"復元できず、元のデータにも戻せませんでした。データのフォルダの restore-rollback を確認してください（{ex.Message}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryCleanup(paths);
            return new RestoreApplyResult(RestoreApplyOutcome.Failed, $"復元できませんでした。元のデータで起動しました（{ex.Message}）");
        }
        // サムネイルは、復元したライブラリのものではないため、作り直す（失敗しても、復元は成功とする）
        TryClearThumbnails(paths);
        TryCleanup(paths);
        return new RestoreApplyResult(RestoreApplyOutcome.Succeeded, null, marker.BackupCreatedAt);
    }

    private static string[] ReplacedFiles(AppPaths paths) =>
    [
        paths.DatabaseFile,
        paths.DatabaseFile + "-wal",
        paths.DatabaseFile + "-shm",
        paths.SettingsFile,
    ];

    /// <summary>
    /// 今のファイルを退避し、展開したファイルを所定の場所へ置く。途中で失敗した場合は、動かしたファイルを、その場で元に戻す
    /// （戻せなかった場合は <see cref="RollbackFailedException"/>）。
    /// </summary>
    private static void Swap(AppPaths paths)
    {
        var stagedDatabase = Path.Combine(paths.RestoreStagingDirectory, BackupEntries.Database);
        if (!File.Exists(stagedDatabase))
        {
            throw new FileNotFoundException("展開したバックアップのDBが見つかりません", stagedDatabase);
        }

        var moved = new List<(string Original, string Saved)>();
        var placed = new List<string>();
        try
        {
            _ = Directory.CreateDirectory(paths.RestoreRollbackDirectory);
            foreach (var file in ReplacedFiles(paths).Where(File.Exists))
            {
                var saved = Path.Combine(paths.RestoreRollbackDirectory, Path.GetFileName(file));
                File.Move(file, saved);
                moved.Add((file, saved));
            }

            File.Move(stagedDatabase, paths.DatabaseFile);
            placed.Add(paths.DatabaseFile);
            var stagedSettings = Path.Combine(paths.RestoreStagingDirectory, BackupEntries.Settings);
            if (File.Exists(stagedSettings))
            {
                File.Move(stagedSettings, paths.SettingsFile);
                placed.Add(paths.SettingsFile);
            }
        }
        catch
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
                throw new RollbackFailedException(rollback.Message, rollback);
            }

            throw;
        }
    }

    /// <summary>
    /// 退避したファイルを、元の場所へ戻す。入れ替えの途中でアプリが止まったあとの、起動時の復旧に使う
    /// （置かれたファイルは、退避したファイルで上書きされる）。
    /// </summary>
    private static void MoveBack(AppPaths paths)
    {
        foreach (var file in ReplacedFiles(paths))
        {
            var saved = Path.Combine(paths.RestoreRollbackDirectory, Path.GetFileName(file));
            if (File.Exists(saved))
            {
                File.Move(saved, file, overwrite: true);
            }
        }
    }

    /// <summary>入れ替えの失敗のあと、元のファイルへ戻せなかった。</summary>
    private sealed class RollbackFailedException(string message, Exception inner) : IOException(message, inner);
    private static void TryClearThumbnails(AppPaths paths)
    {
        try
        {
            _ = new ThumbnailCacheStore(paths.ThumbnailsDirectory).Clear();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabaseFile,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM thumbnail_cache";
            _ = command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // 記録が残っても、ファイルがなければ、サムネイルの作成が、作成待ちに戻して作り直す
        }
    }

    private static void TryCleanup(AppPaths paths)
    {
        try
        {
            Cleanup(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 片付けられなくても、次の復元の準備が、上書きする
        }
    }

    private static void Cleanup(AppPaths paths)
    {
        if (Directory.Exists(paths.RestoreRollbackDirectory))
        {
            Directory.Delete(paths.RestoreRollbackDirectory, recursive: true);
        }

        if (Directory.Exists(paths.RestoreStagingDirectory))
        {
            Directory.Delete(paths.RestoreStagingDirectory, recursive: true);
        }

        File.Delete(paths.RestorePendingFile);
    }
}
