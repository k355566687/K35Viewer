using System.Diagnostics;
using K35Viewer.Domain;

namespace K35Viewer.Tests.Services;

/// <summary>
/// 10,000件のライブラリで、一覧・検索・ソート・フォルダ絞り込みが実用速度で動くことの確認。
/// 時間はテスト出力へ記録し、極端な劣化(桁違いの遅さ)だけを緩い上限で検知する。
/// </summary>
public class LibraryPerformanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    // 100フォルダ × 100件。フォルダ1が起点で、フォルダ2〜100はその直下。
    private const string SeedFolders = """
        INSERT INTO library_folders (id, path, normalized_path, created_at, updated_at)
        VALUES (1, 'C:\L', 'C:\L', 1, 1);
        WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 99)
        INSERT INTO folders (id, library_folder_id, parent_folder_id, name, full_path, normalized_path,
                             relative_path, depth, created_at, updated_at)
        SELECT i + 1, 1,
               CASE WHEN i = 0 THEN NULL ELSE 1 END,
               printf('f%03d', i),
               printf('C:\L\f%03d', i),
               printf('C:\L\F%03d', i),
               CASE WHEN i = 0 THEN '' ELSE printf('f%03d', i) END,
               CASE WHEN i = 0 THEN 0 ELSE 1 END,
               1, 1
          FROM n;
        """;

    private const string SeedArchives = """
        WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 9999)
        INSERT INTO archives (folder_id, file_name, extension, file_size, last_write_time, image_count,
                              created_at, updated_at)
        SELECT (i % 100) + 1,
               printf('書籍_%05d.zip', i),
               'zip',
               (i * 7919) % 1000003,
               1700000000 + i * 60,
               i % 300,
               1, 1
          FROM n;
        """;

    private static async Task<(TimeSpan Elapsed, PagedResult<ArchiveSummary> Result)> TimeAsync(
        LibraryTestContext ctx, ArchiveQuery query)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ctx.Library.QueryArchivesAsync(query, Ct);
        return (stopwatch.Elapsed, result);
    }

    [Fact]
    public async Task 一万件でも全件表示_検索_ソート_フォルダ絞り込みが実用速度で動く()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        await ctx.ExecuteAsync(SeedFolders);
        await ctx.ExecuteAsync(SeedArchives);
        Assert.Equal(10_000, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));

        var all = await TimeAsync(ctx, new ArchiveQuery());
        var bySize = await TimeAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.FileSize, Descending: true));
        var byDate = await TimeAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.LastWriteTime));
        var byCount = await TimeAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.ImageCount, Descending: true));
        var searchOne = await TimeAsync(ctx, new ArchiveQuery(SearchText: "00123"));
        var searchMany = await TimeAsync(ctx, new ArchiveQuery(SearchText: "書籍"));
        var searchAnd = await TimeAsync(ctx, new ArchiveQuery(SearchText: "書籍 00123 zip"));
        var folder = await TimeAsync(ctx, new ArchiveQuery(FolderId: 2));
        var root = await TimeAsync(ctx, new ArchiveQuery(FolderId: 1, SearchText: "9", SortKey: ArchiveSortKey.FileSize));

        TestContext.Current.SendDiagnosticMessage(
            $"10,000件: 全件 {all.Elapsed.TotalMilliseconds:F0}ms / サイズ順 {bySize.Elapsed.TotalMilliseconds:F0}ms / " +
            $"日時順 {byDate.Elapsed.TotalMilliseconds:F0}ms / 枚数順 {byCount.Elapsed.TotalMilliseconds:F0}ms / " +
            $"検索(1件) {searchOne.Elapsed.TotalMilliseconds:F0}ms / 検索(全件一致) {searchMany.Elapsed.TotalMilliseconds:F0}ms / " +
            $"検索(AND) {searchAnd.Elapsed.TotalMilliseconds:F0}ms / フォルダ {folder.Elapsed.TotalMilliseconds:F0}ms / " +
            $"起点+検索+ソート {root.Elapsed.TotalMilliseconds:F0}ms");

        Assert.Equal(10_000, all.Result.TotalCount);
        Assert.Equal(10_000, all.Result.Items.Count);
        Assert.Equal("書籍_00000.zip", all.Result.Items[0].FileName);
        Assert.Equal(10_000, bySize.Result.Items.Count);
        Assert.True(bySize.Result.Items[0].FileSize >= bySize.Result.Items[1].FileSize);
        Assert.True(byDate.Result.Items[0].LastWriteTime <= byDate.Result.Items[1].LastWriteTime);
        Assert.Equal(299, byCount.Result.Items[0].ImageCount);
        Assert.Equal("書籍_00123.zip", Assert.Single(searchOne.Result.Items).FileName);
        Assert.Equal(10_000, searchMany.Result.TotalCount);
        Assert.Equal("書籍_00123.zip", Assert.Single(searchAnd.Result.Items).FileName);
        Assert.Equal(100, folder.Result.TotalCount);
        Assert.Equal(10_000, (await ctx.Library.QueryArchivesAsync(new ArchiveQuery(FolderId: 1), Ct)).TotalCount);
        Assert.True(root.Result.TotalCount > 0);

        foreach (var elapsed in new[]
        {
            all.Elapsed, bySize.Elapsed, byDate.Elapsed, byCount.Elapsed, searchOne.Elapsed,
            searchMany.Elapsed, searchAnd.Elapsed, folder.Elapsed, root.Elapsed,
        })
        {
            Assert.True(elapsed < Limit, $"応答が遅すぎます: {elapsed}");
        }
    }
}
