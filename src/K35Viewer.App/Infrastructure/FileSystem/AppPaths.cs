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
        SettingsFile = Path.Combine(root, "settings.json");
        BackupsDirectory = Path.Combine(root, "backups");
        RestorePendingFile = Path.Combine(root, "restore-pending.json");
        RestoreStagingDirectory = Path.Combine(root, "restore-staging");
        RestoreRollbackDirectory = Path.Combine(root, "restore-rollback");
    }

    /// <summary>復元の入れ替えの予定の目印。あれば、次の起動の最初に入れ替える（要件定義 24.5）。</summary>
    public string RestorePendingFile { get; }

    /// <summary>復元元のバックアップを展開したファイルを、入れ替えまで置くフォルダ。</summary>
    public string RestoreStagingDirectory { get; }

    /// <summary>入れ替えの間だけ、元のファイルを退避するフォルダ。入れ替えが終われば消える（残っていれば、途中で止まった印）。</summary>
    public string RestoreRollbackDirectory { get; }

    /// <summary>バックアップのファイルを置くフォルダ（要件定義 24.5）。最初のバックアップを作るときに作る。</summary>
    public string BackupsDirectory { get; }

    public string Root { get; }

    public string DatabaseFile { get; }

    public string ThumbnailsDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    /// <summary>設定のファイル（要件定義 24.1）。</summary>
    public string SettingsFile { get; }

    /// <summary>実行時データの配置先を切り替える環境変数（画面の確認用のサンプルデータなど、普段のライブラリに触れないため）。</summary>
    public const string DataRootVariable = "K35VIEWER_DATA_ROOT";

    /// <summary>
    /// 環境変数 <see cref="DataRootVariable"/> が指定されていればその場所、なければ %LOCALAPPDATA%\K35Viewer を配置先とする。
    /// </summary>
    public static AppPaths CreateDefault() => FromEnvironment(Environment.GetEnvironmentVariable(DataRootVariable));

    internal static AppPaths FromEnvironment(string? dataRoot) =>
        string.IsNullOrWhiteSpace(dataRoot)
            ? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K35Viewer"))
            : new(Path.GetFullPath(dataRoot.Trim()));

    /// <summary>必要なディレクトリを作成する（既に存在する場合は何もしない）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
