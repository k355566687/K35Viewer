using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Tests.Infrastructure;

public class AppPathsTests
{
    [Fact]
    public void EnsureDirectories_必要なディレクトリを作成する()
    {
        var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);

            paths.EnsureDirectories();
            paths.EnsureDirectories(); // 2回呼んでも失敗しない

            Assert.True(Directory.Exists(paths.ThumbnailsDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.TempDirectory));
            Assert.Equal(Path.Combine(root, "library.db"), paths.DatabaseFile);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 保存先の指定がなければ_ローカルのアプリデータの下を使う(string? value)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K35Viewer");

        Assert.Equal(expected, AppPaths.FromEnvironment(value).Root);
    }

    [Fact]
    public void 保存先を指定すると_その場所の下にDBなどを置く()
    {
        var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "sample-root");

        var paths = AppPaths.FromEnvironment("  " + root + "  ");

        Assert.Equal(root, paths.Root);
        Assert.Equal(Path.Combine(root, "library.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine(root, "thumbnails"), paths.ThumbnailsDirectory);
        Assert.False(Directory.Exists(root)); // 作るのは EnsureDirectories を呼んだとき
    }
}
