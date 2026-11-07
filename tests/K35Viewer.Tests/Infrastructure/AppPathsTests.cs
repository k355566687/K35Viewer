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
}
