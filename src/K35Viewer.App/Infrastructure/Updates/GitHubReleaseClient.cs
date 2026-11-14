using System.Globalization;
using System.Text.Json;

namespace K35Viewer.Infrastructure.Updates;

/// <summary>GitHub のリリース1つ（要件定義 24.6）。</summary>
/// <param name="TagName">タグ（例: v0.2.1）。</param>
/// <param name="Version">版（タグの先頭の v を除いたもの）。</param>
/// <param name="IsPrerelease">試用版（Pre-release）か。</param>
/// <param name="PageUrl">リリースのページ（変更点を見る）。</param>
/// <param name="PackageUrl">配布物（ZIP）のダウンロード先。なければ null。</param>
/// <param name="PackageSize">配布物の大きさ（バイト）。</param>
/// <param name="HashUrl">配布物の SHA-256 を書いたファイルのダウンロード先。なければ null。</param>
public sealed record ReleaseInfo(
    string TagName,
    Version Version,
    bool IsPrerelease,
    Uri PageUrl,
    Uri? PackageUrl,
    long PackageSize,
    Uri? HashUrl)
{
    /// <summary>アプリの中で、ダウンロードして入れ替えられるか（配布物と、そのハッシュがそろっている）。</summary>
    public bool IsInstallable => PackageUrl is not null && HashUrl is not null;

    /// <summary>表示用の版（例: v0.2.1）。</summary>
    public string DisplayVersion => "v" + Version.ToString(3);
}

/// <summary>
/// GitHub のリリースの一覧を取得する。通信の相手は、<c>api.github.com</c> と、リリースの配布物を置く <c>github.com</c> だけ。
/// 送るのは、通常の HTTPS の要求だけで、利用者のデータは送らない。
/// </summary>
public sealed class GitHubReleaseClient(HttpClient http, string owner = AppInfo.RepositoryOwner, string repository = AppInfo.RepositoryName)
{
    /// <summary>配布物（ZIP）のファイル名。リリースのワークフロー（<c>release.yml</c>）と、そろえる。</summary>
    public static string PackageName(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"K35Viewer-v{version.ToString(3)}-win-x64.zip");

    /// <summary>配布物の SHA-256 を書いたファイルの名前。</summary>
    public static string HashName(Version version) => PackageName(version) + ".sha256";

    /// <summary>
    /// 公開されているリリース（下書きを除く。試用版を含む）を、版の新しい順に返す。
    /// 版を読み取れないタグのリリースは、含めない。
    /// </summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct = default)
    {
        var url = new Uri(string.Create(CultureInfo.InvariantCulture, $"https://api.github.com/repos/{owner}/{repository}/releases?per_page=30"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        _ = response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyList<ReleaseInfo> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("リリースの一覧の形式が想定と違います");
        }

        var releases = new List<ReleaseInfo>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || GetBool(item, "draft"))
            {
                continue;
            }

            var tag = GetString(item, "tag_name");
            var page = GetString(item, "html_url");
            if (tag is null || page is null || !TryParseVersion(tag, out var version)
                || !Uri.TryCreate(page, UriKind.Absolute, out var pageUrl) || !IsGitHubPage(pageUrl))
            {
                continue;
            }

            Uri? packageUrl = null;
            Uri? hashUrl = null;
            long packageSize = 0;
            if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    var download = GetString(asset, "browser_download_url");
                    if (name is null || download is null
                        || !Uri.TryCreate(download, UriKind.Absolute, out var downloadUrl) || !IsGitHubPage(downloadUrl))
                    {
                        continue;
                    }

                    if (string.Equals(name, PackageName(version), StringComparison.Ordinal))
                    {
                        packageUrl = downloadUrl;
                        packageSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                    }
                    else if (string.Equals(name, HashName(version), StringComparison.Ordinal))
                    {
                        hashUrl = downloadUrl;
                    }
                }
            }

            releases.Add(new ReleaseInfo(tag, version, GetBool(item, "prerelease"), pageUrl, packageUrl, packageSize, hashUrl));
        }

        return [.. releases.OrderByDescending(r => r.Version)];
    }

    /// <summary>タグ（v0.2.1 など）から版を読み取る。「0.2.1」の形（3つの数字）だけを受け入れる。</summary>
    internal static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag.TrimStart('v', 'V');
        var parts = text.Split('.');
        if (parts.Length != 3 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>通信とブラウザで開くのを許す相手（https の github.com）。ほかの相手の URL は、無視する。</summary>
    internal static bool IsGitHubPage(Uri url) =>
        url.IsAbsoluteUri
        && url.Scheme == Uri.UriSchemeHttps
        && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
