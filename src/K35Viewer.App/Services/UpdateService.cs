using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Updates;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>更新の確認の結果の種類。</summary>
public enum UpdateCheckStatus
{
    /// <summary>今の版が最新（または、今の版のほうが新しい）。</summary>
    UpToDate,

    /// <summary>新しい版がある。</summary>
    Available,

    /// <summary>確認できなかった（通信の失敗など）。</summary>
    Failed,
}

/// <summary>更新の確認の結果。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, string? Message = null);

/// <summary>更新の準備の進み具合。</summary>
public enum UpdatePhase
{
    Downloading,
    Verifying,
    Extracting,
    BackingUp,
    Starting,
}

/// <summary>更新の進行状況（<see cref="Total"/> が 0 のときは、大きさが分からない）。</summary>
public sealed record UpdateProgress(UpdatePhase Phase, long Done = 0, long Total = 0);

/// <summary>更新できない理由。<see cref="Exception.Message"/> は、利用者に見せる。</summary>
/// <param name="message">理由。</param>
/// <param name="offerReleasePage">リリースのページから、手で入れ替えるよう案内するか。</param>
public sealed class UpdateException(string message, bool offerReleasePage = true) : Exception(message)
{
    public bool OfferReleasePage { get; } = offerReleasePage;
}

/// <summary>新しい版の実行ファイルを起動して、このアプリを終了する（更新の入れ替えのため）。</summary>
public interface IUpdateLauncher
{
    /// <summary>起動して、このアプリを終了する。起動できなかった場合は false（終了しない）。</summary>
    bool TryLaunchAndExit(string executable, string arguments);
}

/// <summary>更新の確認のダイアログ。</summary>
public interface IUpdateDialogs
{
    /// <summary>更新してよいか確認する。</summary>
    bool ConfirmUpdate(string displayVersion);
}

/// <summary>
/// アプリの更新（要件定義 24.6）。GitHub のリリースに新しい版があるかを確かめ（ボタンを押したときだけ）、
/// ダウンロードして、ハッシュ（SHA-256）で確かめ、今のデータをバックアップしてから、新しい版の実行ファイルを起動する。
/// アプリのフォルダの入れ替えは、起動した新しい版が行う（<see cref="UpdateApplier"/>）。
/// </summary>
public sealed partial class UpdateService
{
    /// <summary>アプリの実行ファイルの名前（K35Viewer.App.csproj の AssemblyName）。</summary>
    public const string ExecutableName = "K35Viewer.exe";

    private const int MaxHashFileBytes = 1024;

    private readonly AppPaths _paths;
    private readonly GitHubReleaseClient _client;
    private readonly HttpClient _http;
    private readonly BackupService _backups;
    private readonly IUpdateLauncher _launcher;
    private readonly ILogger<UpdateService> _logger;
    private readonly Version _currentVersion;
    private readonly string _appDirectory;

    /// <param name="currentVersion">今の版。省略時は、アプリの版。</param>
    /// <param name="appDirectory">アプリのフォルダ。省略時は、実行ファイルの場所。</param>
    public UpdateService(
        AppPaths paths,
        GitHubReleaseClient client,
        HttpClient http,
        BackupService backups,
        IUpdateLauncher launcher,
        ILogger<UpdateService> logger,
        Version? currentVersion = null,
        string? appDirectory = null)
    {
        _paths = paths;
        _client = client;
        _http = http;
        _backups = backups;
        _launcher = launcher;
        _logger = logger;
        _currentVersion = currentVersion ?? AppInfo.Version;
        _appDirectory = (appDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>今の版。</summary>
    public Version CurrentVersion => _currentVersion;

    /// <summary>新しい版があるかを確かめる。試用版（Pre-release）も対象にする。例外は投げない（失敗は結果で返す）。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var releases = await _client.GetReleasesAsync(timeout.Token);
            var latest = releases.Count > 0 ? releases[0] : null;
            return latest is null || latest.Version <= _currentVersion
                ? new UpdateCheckResult(UpdateCheckStatus.UpToDate)
                : new UpdateCheckResult(UpdateCheckStatus.Available, latest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            LogCheckFailed(_logger, ex);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Message: DescribeFailure(ex));
        }
    }

    /// <summary>アプリのフォルダに書き込めるか（書き込めなければ、アプリの中では入れ替えられない）。</summary>
    public bool CanWriteToAppDirectory()
    {
        try
        {
            if (!Directory.Exists(_appDirectory))
            {
                return false;
            }

            var probe = Path.Combine(_appDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 更新の準備をして、新しい版を起動する。成功すると、このアプリは終了する（<see cref="IUpdateLauncher"/>）。
    /// 更新できない場合は <see cref="UpdateException"/>。そのときは、アプリのフォルダにもデータにも、変更を加えない。
    /// </summary>
    public async Task InstallAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.PackageUrl is null || release.HashUrl is null)
        {
            throw new UpdateException("この版には、アプリの中で更新するためのファイルがありません。リリースのページから、手で入れ替えてください");
        }

        if (!CanWriteToAppDirectory())
        {
            throw new UpdateException("アプリのフォルダに書き込めないため、アプリの中では更新できません。リリースのページから、手で入れ替えてください");
        }

        try
        {
            ResetStaging();
            var zipPath = Path.Combine(_paths.UpdateStagingDirectory, GitHubReleaseClient.PackageName(release.Version));
            var expectedHash = await DownloadHashAsync(release.HashUrl, ct);
            await DownloadPackageAsync(release.PackageUrl, zipPath, progress, ct);

            progress?.Report(new UpdateProgress(UpdatePhase.Verifying));
            await VerifyHashAsync(zipPath, expectedHash, ct);

            progress?.Report(new UpdateProgress(UpdatePhase.Extracting));
            var appFolder = Path.Combine(_paths.UpdateStagingDirectory, "app");
            await Task.Run(() => Extract(zipPath, appFolder, ct), ct);
            var executable = Path.Combine(appFolder, ExecutableName);
            if (!File.Exists(executable))
            {
                throw new UpdateException("ダウンロードした配布物に、アプリの実行ファイルがありません");
            }

            // 今のデータを、更新の前にバックアップする（新しい版が、DB の形を新しくした場合、古い版には戻せないため）
            progress?.Report(new UpdateProgress(UpdatePhase.BackingUp));
            _ = await _backups.CreateAsync(BackupKind.Auto, ct: ct);

            progress?.Report(new UpdateProgress(UpdatePhase.Starting));
            LogLaunching(_logger, release.DisplayVersion);
            if (!_launcher.TryLaunchAndExit(executable, UpdateApplier.ArgumentsFor(_appDirectory, Environment.ProcessId)))
            {
                throw new UpdateException("新しい版を起動できませんでした。リリースのページから、手で入れ替えてください");
            }
        }
        catch
        {
            // 失敗・キャンセルの場合は、ダウンロードしたファイルを残さない
            UpdateApplier.CleanupStaging(_paths);
            throw;
        }
    }

    private void ResetStaging()
    {
        UpdateApplier.CleanupStaging(_paths);
        _ = Directory.CreateDirectory(_paths.UpdateStagingDirectory);
    }

    /// <summary>ハッシュのファイル（「〈16進の64文字〉  〈ファイル名〉」の形）から、SHA-256 を読み取る。</summary>
    private async Task<string> DownloadHashAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            _ = response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxHashFileBytes];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var first = text.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not { Length: 64 } || !first.All(char.IsAsciiHexDigit))
            {
                throw new UpdateException("配布物のハッシュ（SHA-256）のファイルを読めませんでした。更新を中止しました");
            }

            return first;
        }
        catch (HttpRequestException ex)
        {
            LogDownloadFailed(_logger, ex);
            throw new UpdateException(DescribeFailure(ex, "ハッシュのファイルをダウンロードできませんでした"));
        }
    }

    private async Task DownloadPackageAsync(Uri url, string destination, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            _ = response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(destination);
            var buffer = new byte[81920];
            long done = 0;
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, total));
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(new UpdateProgress(UpdatePhase.Downloading, done, total));
            }
        }
        catch (HttpRequestException ex)
        {
            LogDownloadFailed(_logger, ex);
            throw new UpdateException(DescribeFailure(ex, "配布物をダウンロードできませんでした"));
        }
        catch (IOException ex)
        {
            LogDownloadFailed(_logger, ex);
            throw new UpdateException($"ダウンロードしたファイルを保存できませんでした（{ex.Message}）", offerReleasePage: false);
        }
    }

    private async Task VerifyHashAsync(string zipPath, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            LogHashMismatch(_logger, expected, actual);
            throw new UpdateException("ダウンロードした配布物のハッシュ（SHA-256）が、公開されているものと一致しません。破損または改ざんの可能性があるため、更新を中止しました");
        }
    }

    /// <summary>
    /// ZIP を展開する。ZIP の中が、1つのフォルダ（K35Viewer-win-x64）に収まっていれば、その1階層を除く。
    /// 展開先の外へ書かれる名前のエントリがあれば、何も展開せずに断る。
    /// </summary>
    private static void Extract(string zipPath, string destination, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var topLevel = files
            .Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var strip = topLevel.Count == 1 && files.All(e => e.FullName.Replace('\\', '/').Contains('/'));

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var plan = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in files)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (strip)
            {
                relative = relative[(relative.IndexOf('/', StringComparison.Ordinal) + 1)..];
            }

            var full = Path.GetFullPath(Path.Combine(destination, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("ダウンロードした配布物の中に、不正なファイルの名前があります。更新を中止しました");
            }

            plan.Add((entry, full));
        }

        foreach (var (entry, path) in plan)
        {
            ct.ThrowIfCancellationRequested();
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static string DescribeFailure(Exception ex, string? prefix = null)
    {
        var reason = ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
                "GitHub への問い合わせが多すぎます。しばらくしてから、もう一度お試しください",
            HttpRequestException { StatusCode: { } code } => string.Create(CultureInfo.CurrentCulture, $"GitHub がエラーを返しました（{(int)code}）"),
            HttpRequestException => "インターネットに接続できません",
            OperationCanceledException => "時間内に応答がありませんでした",
            JsonException => "GitHub の応答を読めませんでした",
            _ => ex.Message,
        };
        return prefix is null ? $"確認できませんでした（{reason}）" : $"{prefix}（{reason}）";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "更新の確認に失敗しました。")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "更新のダウンロードに失敗しました。")]
    private static partial void LogDownloadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "ダウンロードした配布物のハッシュが一致しません。Expected={Expected} Actual={Actual}")]
    private static partial void LogHashMismatch(ILogger logger, string expected, string actual);

    [LoggerMessage(Level = LogLevel.Information, Message = "新しい版を起動して、更新の入れ替えを始めます。Version={Version}")]
    private static partial void LogLaunching(ILogger logger, string version);
}
