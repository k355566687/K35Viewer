using K35Viewer.Domain;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;

namespace K35Viewer.Tests.Services;

/// <summary>新着の表示（GitHub の Issue #16）。新しく見つかったアーカイブに印を付け、開く・既読にすると外す。</summary>
public class NewArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void CreateBook(LibraryTestContext ctx, string relative) =>
        ctx.CreateZip(relative, ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(100, 80)));

    private static Task<long> NewCountAsync(LibraryTestContext ctx) =>
        ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE is_new = 1");

    private static Task<long> IdOfAsync(LibraryTestContext ctx, string fileName) =>
        ctx.ScalarAsync($"SELECT id FROM archives WHERE file_name = '{fileName}'");

    private static async Task<long> RegisterAsync(LibraryTestContext ctx)
    {
        var result = await ctx.Library.AddFolderAsync(ctx.Workspace, confirmMerge: true, Ct);
        return result.LibraryFolderId!.Value;
    }

    [Fact]
    public async Task 管理フォルダのはじめてのスキャンでは_中のアーカイブを新着にしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        CreateBook(ctx, @"sub\b.zip");

        _ = await ctx.RegisterAndScanAsync();

        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(0, await NewCountAsync(ctx));
    }

    [Fact]
    public async Task そのあとのスキャンで見つかったアーカイブだけが_新着になる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "b.zip");
        CreateBook(ctx, @"sub\c.zip");

        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        Assert.Equal(2, await NewCountAsync(ctx));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'a.zip'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'b.zip'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'c.zip'"));
    }

    [Fact]
    public async Task 起動時のスキャンで見つかったアーカイブも_新着になる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        _ = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "b.zip");

        _ = await ctx.Scan.ScanAllAsync(ScanType.Startup, Ct);

        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'b.zip'"));
    }

    [Fact]
    public async Task 登録済みのアーカイブが変更されても_新着にはならない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "a.zip"); // 同じ名前で作り直す（更新日時が変わる）
        File.SetLastWriteTimeUtc(ctx.PathOf("a.zip"), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var summary = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, await NewCountAsync(ctx));
    }

    [Fact]
    public async Task アプリ内で名前を変えても移動しても_新着にはならず_新着の印も変わらない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "b.zip");
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct); // b.zip が新着
        var idA = await IdOfAsync(ctx, "a.zip");
        var idB = await IdOfAsync(ctx, "b.zip");

        // 名前の変更は、同じ行を更新する（アプリ内の操作と同じ処理）
        await using (var connection = await ctx.OpenAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await ctx.ArchiveRepository.RenameAsync(connection, transaction, idA, "a2.zip", "zip", 1, Ct);
            await ctx.ArchiveRepository.RenameAsync(connection, transaction, idB, "b2.zip", "zip", 1, Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(0, await ctx.ScalarAsync($"SELECT is_new FROM archives WHERE id = {idA}")); // 新着でなかったものは、新着にならない
        Assert.Equal(1, await ctx.ScalarAsync($"SELECT is_new FROM archives WHERE id = {idB}")); // 新着だったものは、新着のまま
    }

    [Fact]
    public async Task 復元の直後のスキャンでは_新着の印を付けず_そのあとのスキャンからは付ける()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "b.zip");

        ctx.Scan.SuppressNewMarkingOnNextScan();
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Startup, Ct);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(0, await NewCountAsync(ctx));

        CreateBook(ctx, "c.zip");
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'c.zip'"));
        Assert.Equal(1, await NewCountAsync(ctx));
    }

    [Fact]
    public async Task はじめてのスキャンが終わっていない管理フォルダは_次のスキャンでも新着にしない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await RegisterAsync(ctx); // 登録だけして、スキャンは完了していない（最終スキャン日時がない）

        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Initial, Ct);

        Assert.Equal(0, await NewCountAsync(ctx));
    }

    [Fact]
    public async Task アーカイブを開くと_新着の印が外れる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "b.zip");
        CreateBook(ctx, "c.zip");
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);
        Assert.Equal(2, await NewCountAsync(ctx));

        var opened = await ctx.Viewer.OpenAsync(await IdOfAsync(ctx, "b.zip"), Ct);

        Assert.True(opened.Succeeded);
        opened.Session!.Dispose();
        Assert.Equal(0, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'b.zip'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'c.zip'")); // 開いていないものは、そのまま
    }

    [Fact]
    public async Task 開けなかったアーカイブの新着の印は_外れない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        _ = ctx.CreateFile("broken.zip", size: 64); // ZIP ではない
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        var opened = await ctx.Viewer.OpenAsync(await IdOfAsync(ctx, "broken.zip"), Ct);

        Assert.False(opened.Succeeded);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'broken.zip'"));
    }

    [Fact]
    public async Task 既読にすると_対象のフォルダ以下の新着の印だけが外れる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "top.zip");
        CreateBook(ctx, @"sub\s1.zip");
        CreateBook(ctx, @"sub\deep\s2.zip");
        CreateBook(ctx, @"other\o1.zip");
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);
        Assert.Equal(4, await NewCountAsync(ctx));
        var subFolderId = await ctx.ScalarAsync("SELECT id FROM folders WHERE name = 'sub'");

        var cleared = await ctx.Library.MarkAllReadAsync(subFolderId, Ct);

        Assert.Equal(2, cleared); // sub と、その配下の deep
        Assert.Equal(2, await NewCountAsync(ctx));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'top.zip'"));
        Assert.Equal(1, await ctx.ScalarAsync("SELECT is_new FROM archives WHERE file_name = 'o1.zip'"));

        Assert.Equal(2, await ctx.Library.MarkAllReadAsync(null, Ct)); // すべて
        Assert.Equal(0, await NewCountAsync(ctx));
    }

    [Fact]
    public async Task 一覧は_新着の印を返し_新着だけに絞り込める()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        CreateBook(ctx, "a.zip");
        var libraryId = await ctx.RegisterAndScanAsync();
        CreateBook(ctx, "b.zip");
        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        var all = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(), Ct);
        var onlyNew = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(NewOnly: true), Ct);

        Assert.Equal(["a.zip", "b.zip"], all.Items.Select(i => i.FileName).ToArray());
        Assert.Equal([false, true], all.Items.Select(i => i.IsNew).ToArray());
        Assert.Equal(["b.zip"], onlyNew.Items.Select(i => i.FileName).ToArray());
        Assert.Equal(1, onlyNew.TotalCount);
    }

    [Fact]
    public void 一覧の行は_開いたあとに新着の印を外し_変更を通知する()
    {
        var item = new ArchiveListItem(new ArchiveSummary(1, "a.zip", @"D:\A", 10, 1700000000, 5, "READY", IsNew: true));
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(item.IsNew);

        item.MarkRead();

        Assert.False(item.IsNew);
        Assert.Contains(nameof(ArchiveListItem.IsNew), changed);

        changed.Clear();
        item.MarkRead(); // 2回目は、何も通知しない
        Assert.Empty(changed);
    }
}
