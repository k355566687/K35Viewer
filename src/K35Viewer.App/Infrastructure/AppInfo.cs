using System.Reflection;

namespace K35Viewer.Infrastructure;

/// <summary>アプリの版と、公開しているリポジトリの場所。</summary>
public static class AppInfo
{
    public const string ProductName = "35Viewer";

    public const string RepositoryOwner = "k355566687";

    public const string RepositoryName = "K35Viewer";

    public static Uri RepositoryPage { get; } = new($"https://github.com/{RepositoryOwner}/{RepositoryName}");

    public static Uri LicensePage { get; } = new($"https://github.com/{RepositoryOwner}/{RepositoryName}/blob/main/LICENSE");

    public static Uri ReleasesPage { get; } = new($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases");

    /// <summary>
    /// アプリの版（K35Viewer.App.csproj の Version）。表示用の版（InformationalVersion）は「0.2.0+〈コミット〉」の形のため、
    /// 「+」より前を使う。
    /// </summary>
    public static Version Version { get; } = ParseVersion(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>表示用の版（例: 0.2.0）。</summary>
    public static string VersionText => Version.ToString(3);

    internal static Version ParseVersion(string? text)
    {
        var core = (text ?? string.Empty).Split('+', '-')[0].Trim().TrimStart('v', 'V');
        return Version.TryParse(core, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
            : new Version(0, 0, 0);
    }
}
