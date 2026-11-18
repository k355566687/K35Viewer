using System.Runtime.InteropServices;
using K35Viewer.Infrastructure.Shortcuts;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>ショートカットを置く場所。</summary>
public enum ShortcutPlace
{
    StartMenu,
    Desktop,
}

/// <summary>ショートカットの状態。</summary>
public enum ShortcutState
{
    /// <summary>作っていない。</summary>
    Missing,

    /// <summary>作ってあり、リンク先が今の実行ファイルと同じ。</summary>
    Current,

    /// <summary>作ってあるが、リンク先が今の実行ファイルと違う（フォルダを移動した、など）。</summary>
    Stale,
}

/// <summary>この実行ファイルに、ショートカットを作ってよいか。</summary>
public enum ShortcutAvailability
{
    Available,

    /// <summary>35Viewer の実行ファイルではない（開発中に <c>dotnet</c> から動かしている、など）。</summary>
    UnsuitableExecutable,

    /// <summary>一時的な場所（ZIP の中から直接起動した、など）。そこへのショートカットは、あとで壊れる。</summary>
    TemporaryLocation,
}

/// <summary>作るショートカット。</summary>
public sealed record ShortcutRequest(bool StartMenu, bool Desktop)
{
    public bool IsEmpty => !StartMenu && !Desktop;
}

public sealed record ShortcutFailure(ShortcutPlace Place, string Message);

/// <summary>作った結果。</summary>
public sealed record ShortcutOutcome(IReadOnlyList<ShortcutPlace> Created, IReadOnlyList<ShortcutFailure> Failures);

/// <summary>ショートカットの作成と削除（GitHub の Issue #23。要件定義 24.8）。</summary>
public interface IShortcutService
{
    /// <summary>この実行ファイルに、ショートカットを作ってよいか。</summary>
    ShortcutAvailability Availability { get; }

    /// <summary>リンク先にする実行ファイル（作れないときは null）。</summary>
    string? ExecutablePath { get; }

    /// <summary>ショートカットのファイルのパス。</summary>
    string PathOf(ShortcutPlace place);

    ShortcutState GetState(ShortcutPlace place);

    /// <summary>ショートカットを作る（あれば、作り直す）。例外は投げない（失敗は結果で返す）。</summary>
    Task<ShortcutOutcome> CreateAsync(ShortcutRequest request);

    /// <summary>ショートカットを削除する。失敗したら、その理由を返す（なかった場合は、何もせず null）。</summary>
    string? Remove(ShortcutPlace place);
}

/// <summary>ショートカットに関する、利用者に聞くダイアログ。</summary>
public interface IShortcutDialogs
{
    /// <summary>はじめて起動したときの確認。作る場所を選んでもらう。「今はしない」の場合は null。</summary>
    ShortcutRequest? PromptFirstRun();

    /// <summary>一時的な場所にある実行ファイルへのショートカットを、作ってよいか確認する。</summary>
    bool ConfirmTemporaryLocation(string executablePath);
}

/// <summary>
/// ショートカットの作成と削除。リンク先は、今動いている実行ファイル。現在の利用者用の場所（スタートメニュー、デスクトップ）だけを使う。
/// タスクバーへのピン止めは、アプリからは行わない（Windows の標準の操作で、利用者が行う。要件定義 24.8）。
/// </summary>
public sealed partial class ShortcutService(
    ShortcutLocations locations,
    string? executablePath,
    string temporaryDirectory,
    ILogger<ShortcutService> logger) : IShortcutService
{
    /// <summary>ショートカットの名前（拡張子を除く）。アプリの表示名。</summary>
    public const string ShortcutName = "35Viewer";

    private const string ExecutableFileName = "K35Viewer.exe";

    private readonly string? _executablePath = IsUsableExecutable(executablePath) ? Path.GetFullPath(executablePath!) : null;

    public string? ExecutablePath => _executablePath;

    public ShortcutAvailability Availability
    {
        get
        {
            if (_executablePath is null)
            {
                return ShortcutAvailability.UnsuitableExecutable;
            }

            return IsUnder(_executablePath, temporaryDirectory)
                ? ShortcutAvailability.TemporaryLocation
                : ShortcutAvailability.Available;
        }
    }

    public string PathOf(ShortcutPlace place) => Path.Combine(
        place == ShortcutPlace.StartMenu ? locations.StartMenuDirectory : locations.DesktopDirectory,
        ShortcutName + ".lnk");

    public ShortcutState GetState(ShortcutPlace place)
    {
        var path = PathOf(place);
        if (!File.Exists(path))
        {
            return ShortcutState.Missing;
        }

        var target = ShellLink.ReadTarget(path);
        return target is not null && _executablePath is not null && SamePath(target, _executablePath)
            ? ShortcutState.Current
            : ShortcutState.Stale;
    }

    // OneDrive などでリダイレクトされたデスクトップは、書き込みが遅いことがあるため、画面のスレッドを止めない
    public Task<ShortcutOutcome> CreateAsync(ShortcutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Create(request));
    }

    public string? Remove(ShortcutPlace place)
    {
        var path = PathOf(place);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                LogRemoved(logger, path);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogRemoveFailed(logger, ex, path);
            return ex.Message;
        }
    }

    private ShortcutOutcome Create(ShortcutRequest request)
    {
        var created = new List<ShortcutPlace>();
        var failures = new List<ShortcutFailure>();
        foreach (var place in Requested(request))
        {
            if (_executablePath is null)
            {
                // 呼び出し側が、作れるかを先に確かめている。ここへ来たら、失敗として返す
                failures.Add(new ShortcutFailure(place, "35Viewer の実行ファイルから起動していないため、作れません"));
                continue;
            }

            var error = TryCreate(place, _executablePath);
            if (error is null)
            {
                created.Add(place);
            }
            else
            {
                failures.Add(new ShortcutFailure(place, error));
            }
        }

        return new ShortcutOutcome(created, failures);
    }
    private static IEnumerable<ShortcutPlace> Requested(ShortcutRequest request)
    {
        if (request.StartMenu)
        {
            yield return ShortcutPlace.StartMenu;
        }

        if (request.Desktop)
        {
            yield return ShortcutPlace.Desktop;
        }
    }

    private string? TryCreate(ShortcutPlace place, string executable)
    {
        var path = PathOf(place);
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ShellLink.Create(path, executable, Path.GetDirectoryName(executable)!, ShortcutName);
            LogCreated(logger, path, executable);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            LogCreateFailed(logger, ex, path);
            return ex.Message;
        }
    }

    private static bool IsUsableExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetFileName(path), ExecutableFileName, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ショートカットを作りました。{Path} → {Target}")]
    private static partial void LogCreated(ILogger logger, string path, string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ショートカットを作れませんでした。{Path}")]
    private static partial void LogCreateFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "ショートカットを削除しました。{Path}")]
    private static partial void LogRemoved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ショートカットを削除できませんでした。{Path}")]
    private static partial void LogRemoveFailed(ILogger logger, Exception exception, string path);
}
