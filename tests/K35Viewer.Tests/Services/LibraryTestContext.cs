using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
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
internal sealed class LibraryTestContext : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly DatabaseWriter _writer;

    public LibraryTestContext()
    {
        Workspace = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Workspace);

        _writer = new DatabaseWriter(_db.Factory);
        var libraries = new LibraryRepository(_db.Factory);
        var history = new HistoryRepository();
        var fileSystem = new FileSystemService();
        ArchiveRepository = new ArchiveRepository(_db.Factory);

        Library = new LibraryService(
            libraries, ArchiveRepository, history, _writer, fileSystem, Notifications, NullLogger<LibraryService>.Instance);
        Scan = new ScanService(
            fileSystem, libraries, ArchiveRepository, new ScanRepository(), history, _writer, Notifications,
            NullLogger<ScanService>.Instance);
    }

    public string Workspace { get; }

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

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await TempDatabase.ScalarLongAsync(connection, sql);
    }

    public void Dispose()
    {
        Scan.Dispose();
        _writer.Dispose();
        _db.Dispose();
        if (Directory.Exists(Workspace))
        {
            Directory.Delete(Workspace, recursive: true);
        }
    }
}
