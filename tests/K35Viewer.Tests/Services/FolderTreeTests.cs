using K35Viewer.Domain;

namespace K35Viewer.Tests.Services;

public class FolderTreeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AddAndScanAsync(LibraryTestContext ctx, string path)
    {
        // 親フォルダの追加では統合の確認が求められるため、常に承認済みとして追加する
        var result = await ctx.Library.AddFolderAsync(path, confirmMerge: true, Ct);
        await ctx.Scan.ScanAsync(result.LibraryFolderId!.Value, ScanType.Initial, Ct);
        return result.LibraryFolderId.Value;
    }

    [Fact]
    public async Task GetFolderTreeAsync_登録フォルダを起点に実際のフォルダ構造をツリーにする()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"漫画\2026\a.zip");
        ctx.CreateFile(@"漫画\2025\b.zip");
        ctx.CreateFile(@"写真集\c.zip");
        ctx.CreateFile("top.zip");
        await AddAndScanAsync(ctx, ctx.Workspace);

        var roots = await ctx.Library.GetFolderTreeAsync(Ct);

        var root = Assert.Single(roots);
        Assert.True(root.IsLibraryRoot);
        Assert.False(root.IsUnavailable);
        Assert.Equal(ctx.Workspace, root.Name); // 起点はフルパスで表示する
        Assert.Equal(["写真集", "漫画"], root.Children.Select(c => c.Name)); // 同階層は名前順
        var manga = root.Children.Single(c => c.Name == "漫画");
        Assert.Equal(["2025", "2026"], manga.Children.Select(c => c.Name)); // 同階層は名前順
        Assert.All(manga.Children, c => Assert.False(c.IsLibraryRoot));
        Assert.Equal(ctx.PathOf(@"漫画\2026"), manga.Children[1].FullPath);
    }

    [Fact]
    public async Task GetFolderTreeAsync_複数の登録フォルダはパス順に並ぶ()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"B-lib\b.zip");
        ctx.CreateFile(@"a-lib\a.zip");
        await AddAndScanAsync(ctx, ctx.PathOf("B-lib"));
        await AddAndScanAsync(ctx, ctx.PathOf("a-lib"));

        var roots = await ctx.Library.GetFolderTreeAsync(Ct);

        Assert.Equal([ctx.PathOf("a-lib"), ctx.PathOf("B-lib")], roots.Select(r => r.Name));
    }

    [Fact]
    public async Task GetFolderTreeAsync_見つからなくなったフォルダは配下ごとツリーから消える()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"keep\a.zip");
        ctx.CreateFile(@"gone\x.zip");
        ctx.CreateFile(@"gone\inner\y.zip");
        var id = await AddAndScanAsync(ctx, ctx.Workspace);
        Directory.Delete(ctx.PathOf("gone"), recursive: true);
        await ctx.Scan.ScanAsync(id, ScanType.Manual, Ct);

        var roots = await ctx.Library.GetFolderTreeAsync(Ct);

        var root = Assert.Single(roots);
        Assert.Equal(["keep"], root.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task GetFolderTreeAsync_登録フォルダに接続できなくても起点と配下を残し_接続なしと示す()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"sub\a.zip");
        var id = await AddAndScanAsync(ctx, ctx.Workspace);
        Directory.Delete(ctx.Workspace, recursive: true); // 切断を模す
        await ctx.Scan.ScanAsync(id, ScanType.Startup, Ct);

        var roots = await ctx.Library.GetFolderTreeAsync(Ct);

        var root = Assert.Single(roots);
        Assert.True(root.IsUnavailable);
        Assert.Equal(["sub"], root.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task GetFolderTreeAsync_親フォルダ追加で統合した後は_子の登録フォルダが親の配下に並ぶ()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateFile(@"parent\mid\child\a.zip");
        await AddAndScanAsync(ctx, ctx.PathOf(@"parent\mid\child"));
        await AddAndScanAsync(ctx, ctx.PathOf("parent"));

        var roots = await ctx.Library.GetFolderTreeAsync(Ct);

        var root = Assert.Single(roots);
        Assert.Equal(ctx.PathOf("parent"), root.Name);
        var mid = Assert.Single(root.Children);
        Assert.Equal("mid", mid.Name);
        Assert.Equal(["child"], mid.Children.Select(c => c.Name));
        Assert.False(mid.Children[0].IsLibraryRoot);
    }
}
