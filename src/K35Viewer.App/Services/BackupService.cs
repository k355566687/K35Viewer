using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>バックアップの種類。名前の中に含める。</summary>
public enum BackupKind
{
    /// <summary>利用者が作った。自動では削除しない。</summary>
    Manual,

    /// <summary>アプリが、復元や更新の前に作った。新しいものから、設定の数だけ残す。</summary>
    Auto,
}

/// <summary>バックアップのファイル1つ。</summary>
public sealed record BackupInfo(string FilePath, BackupKind Kind, DateTime CreatedAt, long SizeBytes)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>バックアップのファイルの中の <c>manifest.json</c>。復元のときに、中身を確かめるために使う。</summary>
public sealed record BackupManifest(int FormatVersion, string AppVersion, int SchemaVersion, DateTimeOffset CreatedAt, BackupKind Kind);

/// <summary>
/// アプリのデータ（ライブラリの情報、閲覧位置、操作履歴、設定）のバックアップ（要件定義 24.5）。
/// 1つの ZIP に、DB の整合のとれたコピー（<c>library.db</c>）、設定のファイル（<c>settings.json</c>）、
/// 中身の説明（<c>manifest.json</c>）をまとめる。サムネイルのキャッシュとログは含めない（作り直せるため）。
/// DB は SQLite のバックアップ機能で写すため、スキャンなどで使っている最中でも安全に作れる。
/// </summary>
public sealed partial class BackupService(
    AppPaths paths,
    SqliteConnectionFactory connections,
    SettingsService settings,
    ILogger<BackupService> logger,
    TimeProvider? clock = null)
{
    /// <summary>バックアップの ZIP の形式の版。中身の構成を変えたときに上げる。</summary>
    public const int FormatVersion = 1;

    public const string DatabaseEntry = "library.db";

    public const string SettingsEntry = "settings.json";

    public const string ManifestEntry = "manifest.json";

    private static readonly Regex NamePattern = NameRegex();

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>バックアップを作り、できたファイルの情報を返す。自動の場合は、古い自動バックアップも整理する。</summary>
    /// <param name="kind">種類。</param>
    /// <param name="protectedFiles">整理で消さないファイル（復元元など）。</param>
    public async Task<BackupInfo> CreateAsync(
        BackupKind kind, IReadOnlyCollection<string>? protectedFiles = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        var work = Path.Combine(paths.TempDirectory, "backup-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var now = _clock.GetLocalNow();
            var schemaVersion = await WriteDatabaseSnapshotAsync(Path.Combine(work, DatabaseEntry), ct);

            if (File.Exists(paths.SettingsFile))
            {
                File.Copy(paths.SettingsFile, Path.Combine(work, SettingsEntry));
            }

            var manifest = new BackupManifest(FormatVersion, AppInfo.VersionText, schemaVersion, now, kind);
            await File.WriteAllTextAsync(
                Path.Combine(work, ManifestEntry), JsonSerializer.Serialize(manifest, ManifestOptions), ct);

            var destination = ChooseFilePath(kind, now.DateTime);
            var temporary = destination + ".tmp";
            try
            {
                ZipFile.CreateFromDirectory(work, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(temporary, destination);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            LogCreated(logger, destination, kind);
            if (kind == BackupKind.Auto)
            {
                PruneAutoBackups(settings.Current.AutoBackupKeepCount, protectedFiles);
            }

            return Describe(destination, kind, now.DateTime);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>バックアップの一覧。新しいものが先。名前の形式に合わないファイルは含めない。</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(paths.BackupsDirectory))
        {
            return [];
        }

        var result = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(paths.BackupsDirectory, "K35Viewer-*.zip"))
        {
            var match = NamePattern.Match(Path.GetFileName(file));
            if (!match.Success
                || !DateTime.TryParseExact(
                    match.Groups["time"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            var kind = match.Groups["kind"].Value == "auto" ? BackupKind.Auto : BackupKind.Manual;
            result.Add(Describe(file, kind, time));
        }

        return [.. result.OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.FileName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 自動バックアップを、新しいものから <paramref name="keepCount"/> 個だけ残して削除する。
    /// 手動のバックアップと、<paramref name="protectedFiles"/> は、削除しない。
    /// </summary>
    public void PruneAutoBackups(int keepCount, IReadOnlyCollection<string>? protectedFiles = null)
    {
        keepCount = Math.Clamp(keepCount, AppSettings.MinAutoBackupKeepCount, AppSettings.MaxAutoBackupKeepCount);
        var protectedSet = new HashSet<string>(
            (protectedFiles ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var backup in List().Where(b => b.Kind == BackupKind.Auto).Skip(keepCount))
        {
            if (protectedSet.Contains(Path.GetFullPath(backup.FilePath)))
            {
                continue;
            }

            if (TryDelete(backup.FilePath))
            {
                LogPruned(logger, backup.FilePath);
            }
        }
    }

    /// <summary>DB の整合のとれたコピーを作り、そのスキーマの版を返す。</summary>
    private async Task<int> WriteDatabaseSnapshotAsync(string snapshotFile, CancellationToken ct)
    {
        await using var source = await connections.OpenAsync(ct);
        var schemaVersion = await MigrationRunner.GetSchemaVersionAsync(source, ct);

        // プールを使わない（閉じたときにファイルを手放し、作業フォルダを消せるようにする）
        var destinationString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        await using var destination = new SqliteConnection(destinationString);
        await destination.OpenAsync(ct);
        await Task.Run(() => source.BackupDatabase(destination), ct);

        // 1つのファイルとして持ち運べるよう、WAL をやめる（-wal / -shm のファイルを残さない）
        await using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE";
        _ = await command.ExecuteNonQueryAsync(ct);
        return schemaVersion;
    }

    private string ChooseFilePath(BackupKind kind, DateTime localTime)
    {
        var label = kind == BackupKind.Auto ? "auto" : "backup";
        var baseName = string.Create(CultureInfo.InvariantCulture, $"K35Viewer-{label}-{localTime:yyyyMMdd-HHmmss}");
        var path = Path.Combine(paths.BackupsDirectory, baseName + ".zip");
        for (var number = 2; File.Exists(path); number++)
        {
            // 同じ秒に続けて作った場合
            path = Path.Combine(paths.BackupsDirectory, string.Create(CultureInfo.InvariantCulture, $"{baseName}-{number}.zip"));
        }

        return path;
    }

    private static BackupInfo Describe(string path, BackupKind kind, DateTime createdAt)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = 0;
        }

        return new BackupInfo(path, kind, createdAt, size);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時フォルダが残っても、次の起動時の片付けで消える
        }
    }

    [GeneratedRegex(@"^K35Viewer-(?<kind>backup|auto)-(?<time>\d{8}-\d{6})(-\d+)?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "バックアップを作成しました。File={File} Kind={Kind}")]
    private static partial void LogCreated(ILogger logger, string file, BackupKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "古い自動バックアップを削除しました。File={File}")]
    private static partial void LogPruned(ILogger logger, string file);
}
