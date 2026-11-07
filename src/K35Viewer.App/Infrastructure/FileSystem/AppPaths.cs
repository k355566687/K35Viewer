namespace K35Viewer.Infrastructure.FileSystem;

/// <summary>
/// 実行時データ（DB・サムネイル・ログ・一時ファイル）の配置先を表す。
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        DatabaseFile = Path.Combine(root, "library.db");
        ThumbnailsDirectory = Path.Combine(root, "thumbnails");
        LogsDirectory = Path.Combine(root, "logs");
        TempDirectory = Path.Combine(root, "temp");
    }

    public string Root { get; }

    public string DatabaseFile { get; }

    public string ThumbnailsDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    /// <summary>%LOCALAPPDATA%\K35Viewer を配置先とする。</summary>
    public static AppPaths CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K35Viewer"));

    /// <summary>必要なディレクトリを作成する（既に存在する場合は何もしない）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
