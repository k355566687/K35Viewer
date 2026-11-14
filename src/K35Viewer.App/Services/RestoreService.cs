using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using K35Viewer.Infrastructure.Backup;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>バックアップの確認の結果。<see cref="Error"/> が null なら、復元できる。</summary>
public sealed record BackupCheck(BackupManifest? Manifest, string? Error)
{
    public bool IsValid => Error is null && Manifest is not null;
}

/// <summary>復元できないバックアップ。<see cref="Exception.Message"/> は、利用者に見せる理由。</summary>
public sealed class RestoreRejectedException(string message) : Exception(message);

/// <summary>
/// バックアップからの復元の準備（要件定義 24.5）。ZIP を確かめ、展開し、今のデータを自動でバックアップして、
/// 入れ替えの予定を残す。実際の入れ替えは、アプリを再起動したあとの、起動の最初に行う（<see cref="PendingRestore"/>）。
/// </summary>
public sealed partial class RestoreService
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly BackupService _backups;
    private readonly ILogger<RestoreService> _logger;
    private readonly int _supportedSchemaVersion;

    /// <param name="supportedSchemaVersion">このアプリが扱える DB の形の版。省略時は、アプリに埋め込んだ最新の版。</param>
    public RestoreService(AppPaths paths, BackupService backups, ILogger<RestoreService> logger, int? supportedSchemaVersion = null)
    {
        _paths = paths;
        _backups = backups;
        _logger = logger;
        _supportedSchemaVersion = supportedSchemaVersion
            ?? Migration.LoadEmbedded(typeof(Migration).Assembly).Max(m => m.Version);
    }

    /// <summary>ZIP が、このアプリで復元できるバックアップかを、中身を展開せずに確かめる。</summary>
    public async Task<BackupCheck> CheckAsync(string zipPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var database = zip.GetEntry(BackupEntries.Database);
            var manifestEntry = zip.GetEntry(BackupEntries.Manifest);
            if (database is null || manifestEntry is null)
            {
                return new BackupCheck(null, "35Viewer のバックアップのファイルではありません");
            }

            BackupManifest? manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestOptions, ct);
            }

            if (manifest is null)
            {
                return new BackupCheck(null, "バックアップの説明を読めませんでした");
            }

            if (manifest.FormatVersion > BackupService.FormatVersion || manifest.SchemaVersion > _supportedSchemaVersion)
            {
                return new BackupCheck(
                    manifest,
                    string.Create(
                        CultureInfo.CurrentCulture,
                        $"このバックアップは、今のアプリより新しい版（{manifest.AppVersion}）で作られたため、復元できません。アプリを更新してから、もう一度お試しください"));
            }

            return new BackupCheck(manifest, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            LogCheckFailed(_logger, ex, zipPath);
            return new BackupCheck(null, "バックアップのファイルを読めませんでした（壊れているか、ZIP ではありません）");
        }
    }

    /// <summary>
    /// 復元の準備をする。バックアップを展開して確かめ、今のデータを自動でバックアップし、入れ替えの予定を残す。
    /// このあと、アプリを再起動すると、起動の最初に入れ替わる。
    /// 復元できないバックアップの場合は <see cref="RestoreRejectedException"/>（このときは、何も変更しない）。
    /// </summary>
    public async Task PrepareAsync(string zipPath, CancellationToken ct = default)
    {
        var check = await CheckAsync(zipPath, ct);
        if (!check.IsValid)
        {
            throw new RestoreRejectedException(check.Error ?? "復元できません");
        }

        try
        {
            await ExtractAsync(zipPath, ct);
            await VerifyExtractedDatabaseAsync(ct);

            // 今のデータを、戻す前にバックアップする。復元元は、整理で消さない（残す数が1でも）
            var backup = await _backups.CreateAsync(BackupKind.Auto, [zipPath], ct);
            PendingRestore.WriteMarker(
                _paths,
                new PendingRestoreMarker(zipPath, backup.FilePath, check.Manifest!.CreatedAt, DateTimeOffset.Now));
            LogPrepared(_logger, zipPath, backup.FilePath);
        }
        catch
        {
            // 準備の途中で失敗した場合は、展開したファイルを残さない（予定の目印は、最後に書くため、ない）
            TryDeleteStaging();
            throw;
        }
    }

    private async Task ExtractAsync(string zipPath, CancellationToken ct)
    {
        TryDeleteStaging();
        _ = Directory.CreateDirectory(_paths.RestoreStagingDirectory);
        using var zip = ZipFile.OpenRead(zipPath);

        // 決まった名前のファイルだけを、決まった場所へ展開する（ZIP の中の名前で、ほかの場所へ書かれることはない）
        foreach (var name in new[] { BackupEntries.Database, BackupEntries.Settings })
        {
            var entry = zip.GetEntry(name);
            if (entry is null)
            {
                continue;
            }

            await using var source = entry.Open();
            await using var destination = File.Create(Path.Combine(_paths.RestoreStagingDirectory, name));
            await source.CopyToAsync(destination, ct);
        }
    }

    /// <summary>展開した DB が、SQLite として読め、壊れておらず、今のアプリが扱える形かを確かめる。</summary>
    private async Task VerifyExtractedDatabaseAsync(CancellationToken ct)
    {
        var file = Path.Combine(_paths.RestoreStagingDirectory, BackupEntries.Database);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check";
                var result = Convert.ToString(await check.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new RestoreRejectedException("バックアップの中のデータが壊れているため、復元できません");
                }
            }

            var schemaVersion = await MigrationRunner.GetSchemaVersionAsync(connection, ct);
            if (schemaVersion > _supportedSchemaVersion)
            {
                throw new RestoreRejectedException(
                    "このバックアップは、今のアプリより新しい版で作られたため、復元できません。アプリを更新してから、もう一度お試しください");
            }

            if (schemaVersion == 0)
            {
                throw new RestoreRejectedException("バックアップの中に、ライブラリの情報が見つかりません");
            }
        }
        catch (SqliteException ex)
        {
            LogVerifyFailed(_logger, ex);
            throw new RestoreRejectedException("バックアップの中のデータを読めないため、復元できません");
        }
    }

    private void TryDeleteStaging()
    {
        try
        {
            if (Directory.Exists(_paths.RestoreStagingDirectory))
            {
                Directory.Delete(_paths.RestoreStagingDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 次の準備が、上書きする
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "バックアップのファイルを確かめられませんでした。File={File}")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "展開したバックアップのDBを確かめられませんでした。")]
    private static partial void LogVerifyFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "復元の準備ができました。Source={Source} PreRestoreBackup={Backup}")]
    private static partial void LogPrepared(ILogger logger, string source, string backup);
}
