using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

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

internal sealed class LibraryTestContext : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly DatabaseWriter _writer;
    private readonly LibraryRepository _libraries;
    private readonly FileSystemService _fileSystem = new();
    private readonly ThumbnailRepository _thumbnailRepository;
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
        ArchiveRepository = new ArchiveRepository(_db.Factory);
        _thumbnailRepository = new ThumbnailRepository(_db.Factory);
        Cache = new ThumbnailCacheStore(CacheDirectory);
        Reader = new CountingArchiveReader(new ArchiveReader());

        Library = new LibraryService(
            _libraries, ArchiveRepository, history, _writer, _fileSystem, Notifications, NullLogger<LibraryService>.Instance);
        Scan = new ScanService(
            _fileSystem, _libraries, ArchiveRepository, new ScanRepository(), history, _writer, Notifications,
            NullLogger<ScanService>.Instance);
        Thumbnails = CreateThumbnailService();
        Viewer = new ViewerService(
            Reader, ArchiveRepository, _writer, _fileSystem, Notifications, NullLogger<ViewerService>.Instance);
        ImageLoader = new ThumbnailImageLoader(Cache);
        _disposables.Add(ImageLoader);
    }

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

    public LibraryService Library { get; }

    public ScanService Scan { get; }

    public ArchiveRepository ArchiveRepository { get; }

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync() => _db.OpenAsync();

    public string PathOf(string relative) => Path.Combine(Workspace, relative);

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
        foreach (var directory in new[] { Workspace, CacheDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
