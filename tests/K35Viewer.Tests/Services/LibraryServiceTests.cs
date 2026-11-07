using K35Viewer.Domain;
using K35Viewer.Services;

namespace K35Viewer.Tests.Services;

public class LibraryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddFolderAsync_フォルダを登録し_起点フォルダと履歴を作る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();

        var result = await ctx.Library.AddFolderAsync(ctx.Workspace, false, Ct);

        Assert.Equal(AddFolderOutcome.Added, result.Outcome);
        Assert.NotNull(result.LibraryFolderId);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders WHERE relative_path = '' AND depth = 0 AND parent_folder_id IS NULL"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'LIBRARY_FOLDER_ADD' AND status = 'SUCCESS'"));
        var summaries = await ctx.Library.GetLibraryFoldersAsync(Ct);
        Assert.Single(summaries);
        Assert.Equal(0, summaries[0].ArchiveCount);
    }

    [Fact]
    public async Task AddFolderAsync_同じフォルダは_表記が違っても登録済みとして扱う()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        await ctx.Library.AddFolderAsync(ctx.Workspace, false, Ct);

        var again = await ctx.Library.AddFolderAsync(ctx.Workspace.ToUpperInvariant() + Path.DirectorySeparatorChar, false, Ct);

        Assert.Equal(AddFolderOutcome.AlreadyRegistered, again.Outcome);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(NotificationLevel.Info, ctx.Notifications.Items[^1].Level);
    }

    [Fact]
    public async Task AddFolderAsync_登録済みフォルダの配下は追加しない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        Directory.CreateDirectory(ctx.PathOf("child"));
        await ctx.Library.AddFolderAsync(ctx.Workspace, false, Ct);

        var result = await ctx.Library.AddFolderAsync(ctx.PathOf("child"), false, Ct);

        Assert.Equal(AddFolderOutcome.ContainedInExisting, result.Outcome);
        Assert.Equal(ctx.Workspace, result.RelatedPath);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
    }

    [Fact]
    public async Task AddFolderAsync_名前が前方一致するだけの別フォルダは配下として扱わない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        Directory.CreateDirectory(ctx.PathOf("Books"));
        Directory.CreateDirectory(ctx.PathOf("Books2"));
        await ctx.Library.AddFolderAsync(ctx.PathOf("Books"), false, Ct);

        var result = await ctx.Library.AddFolderAsync(ctx.PathOf("Books2"), false, Ct);

        Assert.Equal(AddFolderOutcome.Added, result.Outcome);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
    }

    [Fact]
    public async Task AddFolderAsync_存在しないフォルダはエラーを通知して登録しない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();

        var result = await ctx.Library.AddFolderAsync(ctx.PathOf("nothing"), false, Ct);

        Assert.Equal(AddFolderOutcome.NotAccessible, result.Outcome);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(NotificationLevel.Error, ctx.Notifications.Items[^1].Level);
    }

    [Fact]
    public async Task AddFolderAsync_既存フォルダの親を追加するとき_確認なしでは統合せず確認を要求する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"parent\child\a.zip");
        await ctx.Library.AddFolderAsync(ctx.PathOf(@"parent\child"), false, Ct);

        var result = await ctx.Library.AddFolderAsync(ctx.PathOf("parent"), false, Ct);

        Assert.Equal(AddFolderOutcome.MergeRequired, result.Outcome);
        Assert.Equal([ctx.PathOf(@"parent\child")], result.MergeTargets);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
    }

    [Fact]
    public async Task AddFolderAsync_親フォルダの統合でArchiveIDと閲覧位置が維持される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"parent\top.zip");
        ctx.CreateFile(@"parent\mid\child\a.zip");
        ctx.CreateFile(@"parent\mid\child\inner\b.zip");

        var childAdd = await ctx.Library.AddFolderAsync(ctx.PathOf(@"parent\mid\child"), false, Ct);
        await ctx.Scan.ScanAsync(childAdd.LibraryFolderId!.Value, ScanType.Initial, Ct);
        await ctx.ExecuteAsync("UPDATE archives SET last_view_index = 7 WHERE file_name = 'a.zip'");
        var idA = await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.zip'");
        var idB = await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'b.zip'");

        var merged = await ctx.Library.AddFolderAsync(ctx.PathOf("parent"), true, Ct);

        Assert.Equal(AddFolderOutcome.Added, merged.Outcome);
        var parentId = merged.LibraryFolderId!.Value;

        // 子の登録は消え、親の1件だけになる。配下のアーカイブは消えない
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(idA, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.zip'"));
        Assert.Equal(idB, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'b.zip'"));
        Assert.Equal(7, await ctx.ScalarAsync("SELECT last_view_index FROM archives WHERE file_name = 'a.zip'"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders WHERE library_folder_id <> " + parentId));

        // 付け替え後のツリー: parent(0) > mid(1, 未登録だった中間フォルダ) > child(2) > inner(3)
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders c JOIN folders p ON c.parent_folder_id = p.id " +
            "WHERE c.relative_path = 'mid\\child' AND c.depth = 2 AND p.relative_path = 'mid' AND p.depth = 1"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM folders WHERE relative_path = 'mid\\child\\inner' AND depth = 3"));
        Assert.Equal(1, await ctx.ScalarAsync(
            "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'LIBRARY_FOLDER_ADD' AND affected_count = 1"));

        // 統合後に親を再スキャンしても、フォルダ・アーカイブが重複せず、既存のIDも変わらない
        var foldersBefore = await ctx.ScalarAsync("SELECT COUNT(*) FROM folders");
        var summary = await ctx.Scan.ScanAsync(parentId, ScanType.Initial, Ct);

        Assert.Equal(1, summary.Added); // 親直下の top.zip だけが新規
        Assert.Equal(0, summary.Changed);
        Assert.Equal(0, summary.Missing);
        Assert.Equal(foldersBefore, await ctx.ScalarAsync("SELECT COUNT(*) FROM folders"));
        Assert.Equal(3, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(idA, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.zip'"));
    }
}
