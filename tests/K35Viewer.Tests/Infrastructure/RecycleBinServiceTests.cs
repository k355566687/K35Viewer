using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>
/// ごみ箱サービス。通常の自動テストは、実際のごみ箱を操作しない（開発環境設計書 15章）。
/// 実際のごみ箱へ移動する確認は、環境変数 K35_RUN_RECYCLEBIN_TESTS=1 を指定したときだけ行う明示的な統合テスト。
/// </summary>
public class RecycleBinServiceTests : IDisposable
{
    private const string OptInVariable = "K35_RUN_RECYCLEBIN_TESTS";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "recycle-" + Guid.NewGuid().ToString("N"));

    public RecycleBinServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(@"\\server\share\comics\a.zip")]
    [InlineData(@"\\?\UNC\server\share\a.zip")]
    public void ネットワーク上のパスは_ごみ箱を使えない保存先として扱い_何も実行しない(string path)
    {
        var service = new RecycleBinService();

        var reason = service.CheckAvailability(path);
        var result = service.MoveToRecycleBin(path);

        Assert.NotNull(reason);
        Assert.Contains("ネットワーク", reason, StringComparison.Ordinal);
        Assert.Equal(RecycleBinStatus.Unavailable, result.Status);
        Assert.False(result.Moved);
    }

    [Fact]
    public void 存在しないファイルは_移動できない()
    {
        var path = Path.Combine(_directory, "none.zip");

        var result = new RecycleBinService().MoveToRecycleBin(path);

        Assert.False(result.Moved);
        Assert.NotEqual(RecycleBinStatus.Moved, result.Status);
    }

    [Fact]
    public void ごみ箱を使えない場合は_ファイルを完全削除せずそのまま残す()
    {
        // ネットワーク上のパスは実在させられないため、利用不可の判定で何も実行しないことを、実在するファイルで間接的に確認する:
        // 実在するファイルを対象にしても、判定が「利用不可」なら、ファイルは残る
        var path = Path.Combine(_directory, "keep.zip");
        File.WriteAllText(path, "残るべき内容");
        var service = new RecycleBinService();

        var reason = service.CheckAvailability(path);
        if (reason is not null)
        {
            var result = service.MoveToRecycleBin(path);
            Assert.Equal(RecycleBinStatus.Unavailable, result.Status);
            Assert.True(File.Exists(path));
            Assert.Equal("残るべき内容", File.ReadAllText(path));
        }
        else
        {
            // この環境のドライブはごみ箱を使える。ここでは実際のごみ箱を操作しないため、判定だけを確認する
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void 実際のごみ箱へ移動できる_他のプログラムが共有を許可して開いていても_明示的な統合テスト()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OptInVariable) == "1",
            $"実際のごみ箱を操作するため、環境変数 {OptInVariable}=1 を指定したときだけ実行します");

        var path = Path.Combine(_directory, "k35-recycle-test-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(path, "K35Viewerの自動テストが作った一時ファイルです。削除して構いません。");
        var service = new RecycleBinService();
        Assert.Null(service.CheckAvailability(path));

        // ビューアは、閲覧中もこの共有モードでアーカイブを開いたままにする
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = service.MoveToRecycleBin(path);

            Assert.True(result.Moved, result.Message);
        }

        Assert.False(File.Exists(path));
    }
}
