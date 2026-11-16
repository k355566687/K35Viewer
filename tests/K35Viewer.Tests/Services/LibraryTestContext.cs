using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>復元のダイアログの代わり。選ぶファイルと、確認の答えをあらかじめ決めておく。</summary>
internal sealed class FakeBackupDialogs : IBackupDialogs
{
    /// <summary>選ぶファイル。null ならキャンセル。</summary>
    public string? PickResult { get; set; }

    /// <summary>確認の答え。</summary>
    public bool Confirm { get; set; }

    public int PickCalls { get; private set; }

    public List<DateTime> Confirmations { get; } = [];

    public string? PickBackupFile(string initialDirectory)
    {
        PickCalls++;
        return PickResult;
    }

    public bool ConfirmRestore(DateTime createdAt)
    {
        Confirmations.Add(createdAt);
        return Confirm;
    }
}

/// <summary>再起動の代わり（自動テストは、アプリを終了しない）。</summary>
internal sealed class FakeAppRestarter : IAppRestarter
{
    public int Calls { get; private set; }

    /// <summary>false にすると、再起動できなかった場合を再現する。</summary>
    public bool Result { get; set; } = true;

    public bool TryRestart()
    {
        Calls++;
        return Result;
    }
}

/// <summary>更新の確認のダイアログの代わり。</summary>
internal sealed class FakeUpdateDialogs : IUpdateDialogs
{
    public bool Confirm { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public bool ConfirmUpdate(string displayVersion)
    {
        Confirmations.Add(displayVersion);
        return Confirm;
    }
}

/// <summary>新しい版の起動の代わり（自動テストは、プロセスを起動しない）。</summary>
internal sealed class FakeUpdateLauncher : IUpdateLauncher
{
    public List<(string Executable, string Arguments)> Launches { get; } = [];

    public bool Result { get; set; } = true;

    public bool TryLaunchAndExit(string executable, string arguments)
    {
        Launches.Add((executable, arguments));
        return Result;
    }
}

/// <summary>
/// GitHub の代わり（自動テストは、実際には通信しない）。リリースの一覧、ハッシュ、配布物を、あらかじめ決めた内容で返す。
/// 要求の記録を持つ。
/// </summary>
internal sealed class FakeGitHubHandler : HttpMessageHandler
{
    public string ReleasesJson { get; set; } = "[]";

    /// <summary>ハッシュのファイルの内容。</summary>
    public string HashText { get; set; } = string.Empty;

    public byte[] Package { get; set; } = [];

    /// <summary>設定すると、リリースの一覧を求められたときに、この状態を返す。</summary>
    public System.Net.HttpStatusCode? ReleasesStatus { get; set; }

    /// <summary>設定すると、すべての要求でこの例外を投げる（接続できない場合）。</summary>
    public Exception? Failure { get; set; }

    /// <summary>true なら、配布物のダウンロードは、取り消されるまで終わらない。</summary>
    public bool HangOnPackage { get; set; }

    public List<Uri> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        if (Failure is not null)
        {
            throw Failure;
        }

        var path = request.RequestUri!.AbsolutePath;
        if (request.RequestUri.Host == "api.github.com")
        {
            return ReleasesStatus is { } status
                ? new HttpResponseMessage(status)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(ReleasesJson) };
        }

        if (path.EndsWith(".sha256", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(HashText) };
        }

        if (HangOnPackage)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Package) };
    }
}

/// <summary>通知を記録するだけのテスト用実装。</summary>
internal sealed class FakeNotificationService : INotificationService
{
    public List<Notification> Items { get; } = [];

    public void ShowInfo(string message) => Items.Add(new Notification(NotificationLevel.Info, message));

    public void ShowSuccess(string message) => Items.Add(new Notification(NotificationLevel.Success, message));

    public void ShowWarning(string message) => Items.Add(new Notification(NotificationLevel.Warning, message));

    public void ShowError(string message) => Items.Add(new Notification(NotificationLevel.Error, message));
}

/// <summary>
/// ライブラリ・スキャンのテスト用一式。一時DBと、実ファイルを置く一時ワークスペースを持つ。
/// 実ユーザーのフォルダには一切触れない。
/// </summary>
/// <summary>ZIPを開いた回数を数える。一覧表示でZIPを開いていないことの確認に使う。</summary>
internal sealed class CountingArchiveReader(IArchiveReader inner) : IArchiveReader
{
    private int _analyzeCalls;
    private int _readCalls;
    private int _openCalls;
    private int _openedReadCalls;

    public int AnalyzeCalls => Volatile.Read(ref _analyzeCalls);

    public int ReadCalls => Volatile.Read(ref _readCalls);

    /// <summary>ビューア用に開いた回数。</summary>
    public int OpenCalls => Volatile.Read(ref _openCalls);

    /// <summary>開いたままのアーカイブから、エントリを読み出した回数。</summary>
    public int OpenedReadCalls => Volatile.Read(ref _openedReadCalls);

    public int TotalCalls => AnalyzeCalls + ReadCalls + OpenCalls;

    public ArchiveOpenResult Open(string archivePath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _openCalls);
        var result = inner.Open(archivePath, ct);
        return result.Archive is null ? result : result with { Archive = new CountingOpenedArchive(result.Archive, this) };
    }

    private sealed class CountingOpenedArchive(IOpenedArchive inner, CountingArchiveReader owner) : IOpenedArchive
    {
        public IReadOnlyList<ArchiveImageEntry> Images => inner.Images;

        public bool CanModify => inner.CanModify;

        public bool IsSolid => inner.IsSolid;

        public byte[] ReadEntry(string entryName, long maxBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref owner._openedReadCalls);
            return inner.ReadEntry(entryName, maxBytes, ct);
        }

        public void Dispose() => inner.Dispose();
    }

    public ArchiveAnalysis Analyze(string archivePath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _analyzeCalls);
        return inner.Analyze(archivePath, ct);
    }

    public byte[] ReadEntry(string archivePath, string entryName, long maxBytes, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _readCalls);
        return inner.ReadEntry(archivePath, entryName, maxBytes, ct);
    }
}

/// <summary>
/// テスト用のフォルダ・ファイルの移動。Windowsでは、ウイルス対策ソフトやインデックス処理が、作ったばかりの
/// フォルダやファイルを一時的に掴み、名前の変更が「アクセス拒否」になることがある。短い間隔で再試行する。
/// </summary>
internal static class TestFileOperations
{
    private const int Attempts = 40;

    public static void MoveDirectory(string source, string destination) =>
        Retry(() => Directory.Move(source, destination));

    public static void MoveFile(string source, string destination) =>
        Retry(() => File.Move(source, destination));

    /// <summary>フォルダを中身ごと削除する（存在しなければ何もしない）。</summary>
    public static void DeleteDirectory(string path) =>
        Retry(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        });

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>実際のエクスプローラーの代わりのテスト用実装（自動テストは、エクスプローラーを起動しない）。</summary>
internal sealed class FakeExplorerLauncher : IExplorerLauncher
{
    public List<string> Revealed { get; } = [];

    /// <summary>設定すると、表示を求められたときにこの例外を投げる。</summary>
    public Exception? Failure { get; set; }

    public void Reveal(string fullPath)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Revealed.Add(fullPath);
    }

    public List<string> OpenedFolders { get; } = [];

    public List<Uri> OpenedWebPages { get; } = [];

    public void OpenFolder(string fullPath)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        OpenedFolders.Add(fullPath);
    }

    public void OpenWebPage(Uri url)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        OpenedWebPages.Add(url);
    }
}

/// <summary>
/// 実際のごみ箱の代わりのテスト用実装（自動テストは、実際のごみ箱を操作しない）。
/// 移動するファイルは、テスト用の一時フォルダへ移す。利用不可・失敗・移動直後の処理を設定できる。
/// </summary>
internal sealed class FakeRecycleBin(string directory) : IRecycleBinService
{
    private int _moveCalls;

    /// <summary>移動されたファイルを置くフォルダ。</summary>
    public string Directory { get; } = directory;

    /// <summary>null なら利用できる。それ以外は、利用できない理由。</summary>
    public string? Unavailable { get; set; }

    /// <summary>true なら、移動を試みて失敗する（元のファイルはそのまま残る）。</summary>
    public bool FailToMove { get; set; }

    /// <summary>移動した直後（元の場所が空になった後）に実行する処理。置き換えの失敗を再現するために使う。</summary>
    public Action<string>? AfterMove { get; set; }

    public int MoveCalls => Volatile.Read(ref _moveCalls);

    /// <summary>ごみ箱へ移されたファイル。</summary>
    public IReadOnlyList<string> Items =>
        System.IO.Directory.Exists(Directory) ? [.. System.IO.Directory.GetFiles(Directory)] : [];

    public string? CheckAvailability(string path) => Unavailable;

    public RecycleBinResult MoveToRecycleBin(string path)
    {
        _ = Interlocked.Increment(ref _moveCalls);
        if (Unavailable is not null)
        {
            return new RecycleBinResult(RecycleBinStatus.Unavailable, Unavailable);
        }

        if (FailToMove)
        {
            return new RecycleBinResult(RecycleBinStatus.Failed, "テスト用の失敗");
        }

        System.IO.Directory.CreateDirectory(Directory);
        File.Move(path, Path.Combine(Directory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path)));
        AfterMove?.Invoke(path);
        return new RecycleBinResult(RecycleBinStatus.Moved);
    }
}

internal sealed class LibraryTestContext : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly DatabaseWriter _writer;
    private readonly LibraryRepository _libraries;
    private readonly FileSystemService _fileSystem = new();
    private readonly ThumbnailRepository _thumbnailRepository;
    private readonly HistoryRepository _history;
    private readonly List<IDisposable> _disposables = [];

    public LibraryTestContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests");
        var id = Guid.NewGuid().ToString("N");
        Workspace = Path.Combine(root, "ws-" + id);
        CacheDirectory = Path.Combine(root, "thumbs-" + id);
        Directory.CreateDirectory(Workspace);

        _writer = new DatabaseWriter(_db.Factory);
        _libraries = new LibraryRepository(_db.Factory);
        var history = new HistoryRepository();
        _history = history;
        ArchiveRepository = new ArchiveRepository(_db.Factory);
        _thumbnailRepository = new ThumbnailRepository(_db.Factory);
        Cache = new ThumbnailCacheStore(CacheDirectory);
        Reader = new CountingArchiveReader(new ArchiveReader());

        Library = new LibraryService(
            _libraries, ArchiveRepository, history, _writer, _fileSystem, Cache, Notifications, NullLogger<LibraryService>.Instance);
        Scan = new ScanService(
            _fileSystem, _libraries, ArchiveRepository, new ScanRepository(), history, _writer, Notifications,
            NullLogger<ScanService>.Instance);
        Thumbnails = CreateThumbnailService();
        Viewer = new ViewerService(
            Reader, ArchiveRepository, _writer, _fileSystem, Notifications, NullLogger<ViewerService>.Instance);
        ImageLoader = new ThumbnailImageLoader(Cache);
        _disposables.Add(ImageLoader);

        RecycleBin = new FakeRecycleBin(Path.Combine(root, "bin-" + id));
        Explorer = new FakeExplorerLauncher();
        Archives = CreateArchiveService();
    }

    /// <summary>実際のごみ箱の代わり。元のファイルを、テスト用の一時フォルダへ移す。</summary>
    public FakeRecycleBin RecycleBin { get; }

    /// <summary>実際のエクスプローラーの代わり。表示を求められたパスを記録する。</summary>
    public FakeExplorerLauncher Explorer { get; }

    /// <summary>実際のZIP再構築と、偽のごみ箱・エクスプローラーを使うアーカイブ操作サービス。</summary>
    public ArchiveService Archives { get; }

    /// <summary>
    /// アーカイブ更新サービスを作る。書き込み処理やごみ箱を差し替えて、失敗を再現できる。
    /// 置き換えの再試行の待ち時間は、テストを速くするため短くする。
    /// </summary>
    public ArchiveService CreateArchiveService(
        IArchiveWriter? writer = null, IRecycleBinService? recycleBin = null, IReadOnlyList<TimeSpan>? replaceDelays = null, bool alwaysCopyOnMove = false,
        Func<string, long?>? freeSpace = null, IZipConverter? converter = null) =>
        new(writer ?? new ArchiveWriter(), recycleBin ?? RecycleBin, _fileSystem, ArchiveRepository, _thumbnailRepository,
            _history, _libraries, Cache, Explorer, _writer, Notifications, NullLogger<ArchiveService>.Instance)
        {
            ReplaceDelays = replaceDelays ?? [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)],
            AlwaysCopyOnMove = alwaysCopyOnMove,
            FreeSpaceProvider = freeSpace,
            Converter = converter ?? new ZipConverter(),
        };

    public ThumbnailImageLoader ImageLoader { get; }

    public string Workspace { get; }

    public string CacheDirectory { get; }

    public ThumbnailCacheStore Cache { get; }

    public CountingArchiveReader Reader { get; }

    public ThumbnailService Thumbnails { get; }

    public ViewerService Viewer { get; }

    /// <summary>
    /// 新しいサムネイルサービスを作る(アプリの再起動に相当。同一セッション内で覚えている状態を持たない)。
    /// </summary>
    public ThumbnailService CreateThumbnailService(
        IArchiveReader? reader = null, IReadOnlyList<TimeSpan>? retryDelays = null) =>
        new(reader ?? Reader, new ThumbnailGenerator(), Cache, _fileSystem, ArchiveRepository, _thumbnailRepository,
            _writer, NullLogger<ThumbnailService>.Instance, retryDelays);

    /// <summary>画像入りのZIPをワークスペースに作る。</summary>
    public string CreateZip(string relative, params (string Name, byte[] Data)[] entries) =>
        TestZips.Create(PathOf(relative), entries);

    /// <summary>管理フォルダ(ワークスペース)を登録してスキャンする。登録したID を返す。</summary>
    public async Task<long> RegisterAndScanAsync(string? relative = null)
    {
        var token = TestContext.Current.CancellationToken;
        var result = await Library.AddFolderAsync(relative is null ? Workspace : PathOf(relative), true, token);
        await Scan.ScanAsync(result.LibraryFolderId!.Value, ScanType.Initial, token);
        return result.LibraryFolderId.Value;
    }

    public FakeNotificationService Notifications { get; } = new();

    /// <summary>一時DBへの接続の生成元。</summary>
    public K35Viewer.Infrastructure.Database.SqliteConnectionFactory ConnectionFactory => _db.Factory;

    /// <summary>復元のサービスを作る（展開先などは、キャッシュのフォルダの中。テストの後始末で消える）。</summary>
    public RestoreService CreateRestoreService(K35Viewer.Services.SettingsService? settings = null, TimeProvider? clock = null) =>
        new(new K35Viewer.Infrastructure.FileSystem.AppPaths(CacheDirectory), CreateBackupService(settings, clock),
            NullLogger<RestoreService>.Instance);

    /// <summary>設定画面の ViewModel を作る。</summary>
    public K35Viewer.ViewModels.SettingsViewModel CreateSettingsViewModel(
        K35Viewer.Services.SettingsService settings,
        IThumbnailCacheMaintenance cache,
        TimeProvider? clock = null,
        FakeBackupDialogs? dialogs = null,
        FakeAppRestarter? restarter = null,
        UpdateService? updates = null,
        FakeUpdateDialogs? updateDialogs = null) =>
        new(settings, new K35Viewer.Infrastructure.FileSystem.AppPaths(CacheDirectory), Explorer, cache,
            CreateBackupService(settings, clock), CreateRestoreService(settings, clock), dialogs ?? new FakeBackupDialogs(),
            restarter ?? new FakeAppRestarter(), updates ?? CreateUpdateService(settings: settings),
            updateDialogs ?? new FakeUpdateDialogs(), Notifications, NullLogger<K35Viewer.ViewModels.SettingsViewModel>.Instance);

    /// <summary>更新のサービスを作る。通信は、偽の相手に向ける（実際の通信はしない）。</summary>
    public UpdateService CreateUpdateService(
        HttpMessageHandler? handler = null,
        FakeUpdateLauncher? launcher = null,
        Version? current = null,
        string? appDirectory = null,
        K35Viewer.Services.SettingsService? settings = null)
    {
        var http = new HttpClient(handler ?? new FakeGitHubHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new UpdateService(
            new K35Viewer.Infrastructure.FileSystem.AppPaths(CacheDirectory),
            new K35Viewer.Infrastructure.Updates.GitHubReleaseClient(http),
            http,
            CreateBackupService(settings),
            launcher ?? new FakeUpdateLauncher(),
            NullLogger<UpdateService>.Instance,
            current ?? new Version(0, 2, 0),
            appDirectory ?? Path.Combine(Workspace, "app"));
    }

    /// <summary>バックアップのサービスを作る（保存先は、キャッシュのフォルダの中の backups。テストの後始末で消える）。</summary>
    public BackupService CreateBackupService(K35Viewer.Services.SettingsService? settings = null, TimeProvider? clock = null) =>
        new(new K35Viewer.Infrastructure.FileSystem.AppPaths(CacheDirectory), ConnectionFactory, settings ?? CreateSettings(),
            NullLogger<BackupService>.Instance, clock);

    public LibraryService Library { get; }

    public ScanService Scan { get; }

    public ArchiveRepository ArchiveRepository { get; }

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync() => _db.OpenAsync();

    public string PathOf(string relative) => Path.Combine(Workspace, relative);

    /// <summary>設定のファイル（テストの後始末で消える、キャッシュのフォルダの中）。</summary>
    public string SettingsFile => Path.Combine(CacheDirectory, "settings.json");

    /// <summary>設定のサービスを作る。同じファイルを使うため、作り直すとアプリの再起動に相当する。</summary>
    public K35Viewer.Services.SettingsService CreateSettings() =>
        new(new K35Viewer.Infrastructure.Settings.SettingsStore(SettingsFile), NullLogger<K35Viewer.Services.SettingsService>.Instance);

    /// <summary>中身は空のダミーファイルを作る(Phase 2ではZIPの内容を読まない)。</summary>
    public string CreateFile(string relative, int size = 10, DateTime? lastWriteUtc = null)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await TempDatabase.ExecuteAsync(connection, sql);
    }

    /// <summary>1つの文字列を返すクエリを実行する（診断用）。</summary>
    public async Task<string> StringAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await TempDatabase.ScalarLongAsync(connection, sql);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Scan.Dispose();
        _writer.Dispose();
        _db.Dispose();
        foreach (var directory in new[] { Workspace, CacheDirectory, RecycleBin.Directory })
        {
            TestFileOperations.DeleteDirectory(directory);
        }
    }
}
