using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Thumbnails;

namespace K35Viewer.Tests.Infrastructure;

public class NaturalStringComparerTests
{
    private static string[] Sort(params string[] names) =>
        [.. names.Order(NaturalStringComparer.Instance)];

    [Fact]
    public void Compare_連続する数字は数値として比べる()
    {
        Assert.Equal(
            ["img1.jpg", "img2.jpg", "Img03.jpg", "img10.jpg", "img100.jpg"],
            Sort("img10.jpg", "img2.jpg", "img100.jpg", "img1.jpg", "Img03.jpg"));
    }

    [Fact]
    public void Compare_フォルダを含むパスでも各階層の数字を数値として比べる()
    {
        Assert.Equal(
            ["ch1/002.png", "ch1/10.png", "ch2/1.png"],
            Sort("ch2/1.png", "ch1/10.png", "ch1/002.png"));
    }

    [Fact]
    public void Compare_日本語を含む名前でも数字を数値として比べる()
    {
        Assert.Equal(["第1巻", "第2巻", "第10巻"], Sort("第10巻", "第2巻", "第1巻"));
    }

    [Fact]
    public void Compare_数値が同じなら先頭の0が少ない方を前にし_順序は常に一貫する()
    {
        Assert.Equal(["a1", "a01", "a001"], Sort("a001", "a1", "a01"));
        Assert.True(NaturalStringComparer.Instance.Compare("a1", "a01") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a01", "a1") > 0);
    }

    [Fact]
    public void Compare_大文字小文字は区別せず_同一の文字列は0を返す()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("Page1.JPG", "Page1.JPG"));
        Assert.True(NaturalStringComparer.Instance.Compare("a2", "B1") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("A", "a") != 0); // 並びを安定させるため、最後は序数で決める
    }

    [Fact]
    public void Compare_短い名前が前に来る()
    {
        Assert.Equal(["a", "a1", "a1b"], Sort("a1b", "a1", "a"));
    }

    [Fact]
    public void Compare_どんな順序で与えても同じ並びになる()
    {
        string[] names = ["z10", "z9", "a", "A2", "a10", "a1", "b01", "b1", "第3話", "第12話", "10", "9", "1a", "1"];
        var expected = Sort(names);
        var random = new Random(12345);

        for (var i = 0; i < 20; i++)
        {
            var shuffled = names.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, Sort(shuffled));
        }
    }
}

public class ThumbnailGeneratorTests
{
    private static readonly ThumbnailGenerator Generator = new();

    [Theory]
    [InlineData(1000, 600, 300, 180)]
    [InlineData(600, 1000, 180, 300)]
    [InlineData(4000, 3000, 300, 225)]
    [InlineData(300, 300, 300, 300)]
    [InlineData(100, 50, 100, 50)] // 小さい画像は拡大しない
    [InlineData(10000, 10, 300, 1)] // 極端な縦横比でも0にならない
    public void FitInside_長辺を収め_縦横比を保つ(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ThumbnailGenerator.FitInside(width, height));
    }

    [Fact]
    public void Generate_横長のPNGを長辺300pxのJPEGにする()
    {
        var thumbnail = Generator.Generate(TestImages.Png(1000, 600));

        Assert.Equal(300, thumbnail.Width);
        Assert.Equal(180, thumbnail.Height);
        Assert.Equal((300, 180), TestImages.SizeOf(thumbnail.Data));
        Assert.Equal([0xFF, 0xD8], thumbnail.Data[..2]); // JPEGの先頭
    }

    [Fact]
    public void Generate_縦長のJPEGも長辺300pxにする()
    {
        var thumbnail = Generator.Generate(TestImages.Jpeg(600, 1000));

        Assert.Equal((180, 300), (thumbnail.Width, thumbnail.Height));
        Assert.Equal((180, 300), TestImages.SizeOf(thumbnail.Data));
    }

    [Fact]
    public void Generate_小さい画像は拡大しない()
    {
        var thumbnail = Generator.Generate(TestImages.Png(100, 50));

        Assert.Equal((100, 50), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void Generate_色が保たれる()
    {
        var thumbnail = Generator.Generate(TestImages.Png(600, 600, red: 200, green: 40, blue: 40));

        var (red, green, blue) = TestImages.PixelAt(thumbnail.Data, 150, 150);
        Assert.InRange(red, 190, 210);
        Assert.InRange(green, 30, 50);
        Assert.InRange(blue, 30, 50);
    }

    [Fact]
    public void Generate_透過部分は白背景に合成する()
    {
        var thumbnail = Generator.Generate(TestImages.PngHalfTransparent(600, 400));

        var (transparentRed, transparentGreen, transparentBlue) = TestImages.PixelAt(thumbnail.Data, 20, 100);
        Assert.InRange(transparentRed, 245, 255); // 黒ではなく白になる
        Assert.InRange(transparentGreen, 245, 255);
        Assert.InRange(transparentBlue, 245, 255);

        var (opaqueRed, opaqueGreen, _) = TestImages.PixelAt(thumbnail.Data, 280, 100);
        Assert.InRange(opaqueRed, 235, 255);
        Assert.InRange(opaqueGreen, 0, 25);
    }

    [Fact]
    public void Generate_半透明は白と混ざった色になる()
    {
        var thumbnail = Generator.Generate(TestImages.Png(400, 400, red: 0, green: 0, blue: 0, alpha: 128));

        var (red, _, _) = TestImages.PixelAt(thumbnail.Data, 100, 100);
        Assert.InRange(red, 115, 140); // 約50%の灰色
    }

    [Fact]
    public void Generate_壊れた画像と空のデータは_ThumbnailGenerationExceptionになる()
    {
        Assert.Throws<ThumbnailGenerationException>(() => Generator.Generate(TestImages.Corrupt()));
        Assert.Throws<ThumbnailGenerationException>(() => Generator.Generate([]));
        Assert.Throws<ThumbnailGenerationException>(() => Generator.Generate(System.Text.Encoding.UTF8.GetBytes("これは画像ではありません")));
    }
}

public class ThumbnailCacheStoreTests
{
    [Theory]
    [InlineData(5L, "0000\\archive-5.jpg")]
    [InlineData(999L, "0000\\archive-999.jpg")]
    [InlineData(1000L, "0001\\archive-1000.jpg")]
    [InlineData(12345L, "0012\\archive-12345.jpg")]
    public void GetRelativePath_ArchiveIDを基準に1000件ごとのフォルダへ分ける(long archiveId, string expected)
    {
        Assert.Equal(expected, ThumbnailCacheStore.GetRelativePath(archiveId));
    }

    [Fact]
    public void Save_Read_Exists_Delete_が動き_一時ファイルを残さない()
    {
        var root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ThumbnailCacheStore(root);

            var relative = store.Save(12345, [1, 2, 3]);
            Assert.True(store.Exists(relative));
            Assert.Equal([1, 2, 3], store.Read(relative));

            store.Save(12345, [9, 9]); // 上書き
            Assert.Equal([9, 9], store.Read(relative));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.Resolve(relative))!, "*.tmp"));

            store.Delete(relative);
            Assert.False(store.Exists(relative));
            store.Delete(relative); // 存在しなくても失敗しない
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

public class ArchiveReaderTests : IDisposable
{
    private static readonly ArchiveReader Reader = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", Guid.NewGuid().ToString("N"));

    public ArchiveReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Analyze_対応画像を数え_表紙候補を自然順の先頭から返す()
    {
        var jpeg = TestImages.Jpeg(10, 10);
        var png = TestImages.Png(10, 10);
        var path = TestZips.Create(
            PathOf("book.zip"),
            ("img10.jpg", jpeg), ("img2.JPG", jpeg), ("img1.png", png), ("img3.jpeg", jpeg), ("img11.png", png));

        var result = Reader.Analyze(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Ready, result.Status);
        Assert.Equal(5, result.ImageCount);
        Assert.Equal(["img1.png", "img2.JPG", "img3.jpeg"], result.CoverCandidates);
    }

    [Fact]
    public void Analyze_画像以外_フォルダ_macOSのメタデータは数えない()
    {
        var jpeg = TestImages.Jpeg(10, 10);
        var path = TestZips.Create(
            PathOf("mixed.cbz"),
            ("folder/", []), ("folder/page2.jpg", jpeg), ("folder/page1.jpg", jpeg),
            ("readme.txt", [1]), ("cover.gif", [1]), ("__MACOSX/folder/._page1.jpg", jpeg), ("folder/._page2.jpg", jpeg));

        var result = Reader.Analyze(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Ready, result.Status);
        Assert.Equal(2, result.ImageCount);
        Assert.Equal(["folder/page1.jpg", "folder/page2.jpg"], result.CoverCandidates);
    }

    [Fact]
    public void Analyze_画像がないアーカイブは_Emptyになる()
    {
        var path = TestZips.Create(PathOf("text.zip"), ("a.txt", [1, 2]), ("b.gif", [1]));

        var result = Reader.Analyze(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Empty, result.Status);
        Assert.Equal(0, result.ImageCount);
        Assert.Empty(result.CoverCandidates);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Analyze_ZIPとして読めないファイルは_Brokenになる(int size)
    {
        var path = PathOf("broken.zip");
        File.WriteAllBytes(path, new byte[size]);

        var result = Reader.Analyze(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Broken, result.Status);
        Assert.Equal("ARCHIVE_BROKEN", result.ErrorCode);
    }

    [Fact]
    public void Analyze_途中で切れたZIPは_Brokenになる()
    {
        var path = TestZips.Create(PathOf("cut.zip"), ("a.jpg", TestImages.Jpeg(50, 50)), ("b.jpg", TestImages.Jpeg(50, 50)));
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Equal(ArchiveAnalysisStatus.Broken, Reader.Analyze(path, Ct).Status);
    }

    [Fact]
    public void Analyze_存在しないファイルは_FILE_NOT_FOUNDのErrorになる()
    {
        var result = Reader.Analyze(PathOf("missing.zip"), Ct);

        Assert.Equal(ArchiveAnalysisStatus.Error, result.Status);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Analyze_暗号化されたアーカイブは_Unsupportedになる()
    {
        var path = TestZips.CreateEncryptedLooking(PathOf("secret.zip"), ("a.jpg", TestImages.Jpeg(10, 10)));

        var result = Reader.Analyze(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Unsupported, result.Status);
        Assert.Equal("ARCHIVE_ENCRYPTED", result.ErrorCode);
    }

    [Fact]
    public void ReadEntry_指定したエントリの内容を返す()
    {
        var jpeg = TestImages.Jpeg(20, 10);
        var path = TestZips.Create(PathOf("book.zip"), ("dir/a.jpg", jpeg), ("b.jpg", [1]));

        Assert.Equal(jpeg, Reader.ReadEntry(path, "dir/a.jpg", maxBytes: 1_000_000, Ct));
    }

    [Fact]
    public void ReadEntry_存在しないエントリと上限超過は例外になる()
    {
        var path = TestZips.Create(PathOf("book.zip"), ("a.jpg", new byte[5000]));

        Assert.Throws<FileNotFoundException>(() => Reader.ReadEntry(path, "none.jpg", 1_000_000, Ct));
        Assert.Throws<InvalidDataException>(() => Reader.ReadEntry(path, "a.jpg", maxBytes: 1000, Ct));
    }
}
