using K35Viewer.Domain;

namespace K35Viewer.Tests.Services;

public class ArchiveQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 検索・ソートの確認用データを作ってスキャンする。
    /// 名前順(大文字小文字無視): 100%_done, Alpha, beta, Delta one, Epsilon two, Gamma
    /// </summary>
    private static async Task<LibraryTestContext> CreateContextAsync()
    {
        var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile("Alpha.zip", 10, Utc(2026, 1, 1));
        ctx.CreateFile("beta.cbz", 30, Utc(2026, 3, 1));
        ctx.CreateFile("Gamma.zip", 20, Utc(2026, 2, 1));
        ctx.CreateFile("100%_done.zip", 1, Utc(2025, 12, 1));
        ctx.CreateFile(@"sub\Delta one.zip", 40, Utc(2026, 4, 1));
        ctx.CreateFile(@"sub\deep\Epsilon two.zip", 5, Utc(2026, 5, 1));
        var result = await ctx.Library.AddFolderAsync(ctx.Workspace, false, Ct);
        await ctx.Scan.ScanAsync(result.LibraryFolderId!.Value, ScanType.Initial, Ct);
        return ctx;
    }

    private static async Task<string[]> NamesAsync(LibraryTestContext ctx, ArchiveQuery query)
    {
        var result = await ctx.Library.QueryArchivesAsync(query, Ct);
        return result.Items.Select(i => i.FileName).ToArray();
    }

    [Fact]
    public async Task QueryArchivesAsync_条件なしなら全件をファイル名順_大文字小文字無視で返す()
    {
        using var ctx = await CreateContextAsync();

        var result = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(), Ct);

        Assert.Equal(6, result.TotalCount);
        Assert.Equal(
            ["100%_done.zip", "Alpha.zip", "beta.cbz", "Delta one.zip", "Epsilon two.zip", "Gamma.zip"],
            result.Items.Select(i => i.FileName).ToArray());
    }

    [Fact]
    public async Task QueryArchivesAsync_降順にできる()
    {
        using var ctx = await CreateContextAsync();

        var names = await NamesAsync(ctx, new ArchiveQuery(Descending: true));

        Assert.Equal(
            ["Gamma.zip", "Epsilon two.zip", "Delta one.zip", "beta.cbz", "Alpha.zip", "100%_done.zip"],
            names);
    }

    [Fact]
    public async Task QueryArchivesAsync_ファイルサイズ順に並べ替えられる()
    {
        using var ctx = await CreateContextAsync();

        var asc = await NamesAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.FileSize));
        var desc = await NamesAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.FileSize, Descending: true));

        Assert.Equal(["100%_done.zip", "Epsilon two.zip", "Alpha.zip", "Gamma.zip", "beta.cbz", "Delta one.zip"], asc);
        Assert.Equal(asc.Reverse().ToArray(), desc);
    }

    [Fact]
    public async Task QueryArchivesAsync_更新日時順に並べ替えられる()
    {
        using var ctx = await CreateContextAsync();

        var desc = await NamesAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.LastWriteTime, Descending: true));

        Assert.Equal(["Epsilon two.zip", "Delta one.zip", "beta.cbz", "Gamma.zip", "Alpha.zip", "100%_done.zip"], desc);
    }

    [Fact]
    public async Task QueryArchivesAsync_画像枚数順では未解析を0枚として扱う()
    {
        using var ctx = await CreateContextAsync();
        await ctx.ExecuteAsync("UPDATE archives SET image_count = 3 WHERE file_name = 'Alpha.zip'");
        await ctx.ExecuteAsync("UPDATE archives SET image_count = 12 WHERE file_name = 'beta.cbz'");

        var asc = await NamesAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.ImageCount));
        var desc = await NamesAsync(ctx, new ArchiveQuery(SortKey: ArchiveSortKey.ImageCount, Descending: true));

        Assert.Equal(["Alpha.zip", "beta.cbz"], asc[^2..]);
        Assert.Equal(["beta.cbz", "Alpha.zip"], desc[..2]);
    }

    [Fact]
    public async Task QueryArchivesAsync_フォルダを指定すると自身と配下のすべてを対象にする()
    {
        using var ctx = await CreateContextAsync();
        var sub = await ctx.ScalarAsync("SELECT id FROM folders WHERE relative_path = 'sub'");
        var deep = await ctx.ScalarAsync("SELECT id FROM folders WHERE relative_path = 'sub\\deep'");
        var root = await ctx.ScalarAsync("SELECT id FROM folders WHERE relative_path = ''");

        Assert.Equal(["Delta one.zip", "Epsilon two.zip"], await NamesAsync(ctx, new ArchiveQuery(FolderId: sub)));
        Assert.Equal(["Epsilon two.zip"], await NamesAsync(ctx, new ArchiveQuery(FolderId: deep)));
        Assert.Equal(6, (await NamesAsync(ctx, new ArchiveQuery(FolderId: root))).Length);
    }

    [Theory]
    [InlineData("alpha", new[] { "Alpha.zip" })] // 大文字小文字を区別しない
    [InlineData("one", new[] { "100%_done.zip", "Delta one.zip" })] // 「done」の途中にも一致する(部分一致)
    [InlineData("delta one", new[] { "Delta one.zip" })] // 空白区切りはAND
    [InlineData("  delta   one  ", new[] { "Delta one.zip" })]
    [InlineData("delta\u3000one", new[] { "Delta one.zip" })] // 全角空白も区切りとして扱う
    [InlineData("delta two", new string[0])] // 別々のファイルに含まれる語はAND一致しない
    [InlineData("%", new[] { "100%_done.zip" })] // ワイルドカードではなく文字そのもの
    [InlineData("_", new[] { "100%_done.zip" })]
    [InlineData("\\", new string[0])]
    [InlineData(".cbz", new[] { "beta.cbz" })]
    [InlineData("'; DROP TABLE archives; --", new string[0])]
    public async Task QueryArchivesAsync_ファイル名の部分一致で絞り込む(string search, string[] expected)
    {
        using var ctx = await CreateContextAsync();

        var names = await NamesAsync(ctx, new ArchiveQuery(SearchText: search));

        Assert.Equal(expected, names);
        Assert.Equal(6, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives")); // 入力がSQLとして実行されていない
    }

    [Fact]
    public async Task QueryArchivesAsync_フォルダ指定と検索を組み合わせられる()
    {
        using var ctx = await CreateContextAsync();
        var sub = await ctx.ScalarAsync("SELECT id FROM folders WHERE relative_path = 'sub'");

        Assert.Equal(["Epsilon two.zip"], await NamesAsync(ctx, new ArchiveQuery(FolderId: sub, SearchText: "two")));
        Assert.Empty(await NamesAsync(ctx, new ArchiveQuery(FolderId: sub, SearchText: "alpha")));
    }

    [Fact]
    public async Task QueryArchivesAsync_不在のアーカイブは一覧に含めない()
    {
        using var ctx = await CreateContextAsync();
        File.Delete(ctx.PathOf("Alpha.zip"));
        var libraryId = await ctx.ScalarAsync("SELECT id FROM library_folders");
        await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);

        var result = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(), Ct);

        Assert.Equal(5, result.TotalCount);
        Assert.DoesNotContain(result.Items, i => i.FileName == "Alpha.zip");
    }

    [Fact]
    public async Task QueryArchivesAsync_ページ指定しても総件数は条件に一致する全件を返す()
    {
        using var ctx = await CreateContextAsync();

        var page = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(Offset: 2, Limit: 2), Ct);
        var tail = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(Offset: 4), Ct);

        Assert.Equal(6, page.TotalCount);
        Assert.Equal(["beta.cbz", "Delta one.zip"], page.Items.Select(i => i.FileName).ToArray());
        Assert.Equal(["Epsilon two.zip", "Gamma.zip"], tail.Items.Select(i => i.FileName).ToArray());
    }

    [Fact]
    public async Task QueryArchivesAsync_保存場所とフルパスと更新日時を返す()
    {
        using var ctx = await CreateContextAsync();

        var result = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(SearchText: "epsilon"), Ct);

        var item = Assert.Single(result.Items);
        Assert.Equal(ctx.PathOf(@"sub\deep"), item.FolderPath);
        Assert.Equal(ctx.PathOf(@"sub\deep\Epsilon two.zip"), item.FullPath);
        Assert.Equal(5, item.FileSize);
        Assert.Equal(UnixTime.ToUnixSeconds(new DateTimeOffset(Utc(2026, 5, 1))), item.LastWriteTime);
        Assert.Null(item.ImageCount);
        Assert.Equal("PENDING", item.AnalysisStatus);
    }
}
