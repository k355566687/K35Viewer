using System.Diagnostics;

namespace K35Viewer.Infrastructure.FileSystem;

/// <summary>エクスプローラーでファイルを表示する境界（UC-15）。自動テストでは、実際のエクスプローラーを起動しない。</summary>
public interface IExplorerLauncher
{
    /// <summary>エクスプローラーを開き、指定したファイルを選択状態で表示する。</summary>
    void Reveal(string fullPath);

    /// <summary>エクスプローラーで、指定したフォルダを開く（設定画面の「フォルダを開く」）。</summary>
    void OpenFolder(string fullPath);

    /// <summary>既定のブラウザで、Web のページを開く（設定画面の、リリースのページなど）。https の GitHub のページだけ。</summary>
    void OpenWebPage(Uri url);
}

/// <summary>
/// <c>explorer.exe /select,"パス"</c> でファイルを選択状態にして表示する。
/// パスは常に引用符で囲む。Windowsのファイル名には引用符を含められないため、囲んだ文字列からはみ出す入力はない。
/// </summary>
public sealed class ExplorerLauncher : IExplorerLauncher
{
    public void Reveal(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var path = Path.GetFullPath(fullPath);
        if (path.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("パスに使用できない文字が含まれています", nameof(fullPath));
        }

        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    public void OpenFolder(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var path = Path.GetFullPath(fullPath);
        if (path.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("パスに使用できない文字が含まれています", nameof(fullPath));
        }

        Directory.CreateDirectory(path);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
        });
    }

    public void OpenWebPage(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // 既定のブラウザに渡すのは、アプリが組み立てた GitHub の https のページだけ（任意の文字列を実行させない）
        if (!IsAllowedWebPage(url))
        {
            throw new ArgumentException("開けるのは、GitHub の https のページだけです", nameof(url));
        }

        using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    internal static bool IsAllowedWebPage(Uri url) =>
        url.IsAbsoluteUri
        && url.Scheme == Uri.UriSchemeHttps
        && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase);
}
