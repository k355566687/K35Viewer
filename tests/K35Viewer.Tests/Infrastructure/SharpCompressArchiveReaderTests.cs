using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>RAR / 7z（CBR / CB7）の読み取り（閲覧だけ。GitHub の Issue #3）と、形式の判定。</summary>
public sealed class SharpCompressArchiveReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "sc-" + Guid.NewGuid().ToString("N"));

    private readonly ArchiveReader _reader = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SharpCompressArchiveReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestFileOperationsHelper.DeleteDirectory(_root);
    }

    /// <summary>テスト用の 7z（Fixtures\archives）を、別の名前で、作業フォルダへ写す。</summary>
    private string Fixture(string fixtureName, string asFileName)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", fixtureName);
        var destination = Path.Combine(_root, asFileName);
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private static (byte Red, byte Green, byte Blue) Pixel(byte[] image) => TestImages.PixelAt(image, 2, 2);

    // ---- 形式の判定 ----

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0 }, ArchiveFormat.Zip)] // 空の ZIP
    [InlineData(new byte[] { 0x50, 0x4B, 0x07, 0x08, 0, 0, 0, 0 }, ArchiveFormat.Zip)] // 分割された ZIP
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0 }, ArchiveFormat.Rar)] // RAR 4 まで
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, ArchiveFormat.Rar)] // RAR 5
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 }, ArchiveFormat.SevenZip)]
    [InlineData(new byte[] { 0x50, 0x4B }, ArchiveFormat.Unknown)] // 短すぎる
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x02, 0 }, ArchiveFormat.Unknown)] // 知らない版
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, ArchiveFormat.Unknown)] // PNG
    [InlineData(new byte[0], ArchiveFormat.Unknown)]
    public void ファイルの先頭の署名から_形式を決める(byte[] head, ArchiveFormat expected)
    {
        Assert.Equal(expected, ArchiveFormats.FromSignature(head));
    }

    [Fact]
    public void 形式は_拡張子ではなく_中身の署名で決める()
    {
        var zipNamedCbr = TestZips.Create(Path.Combine(_root, "disguised.cbr"), ("1.jpg", TestImages.Jpeg(10, 10)));
        var sevenNamedCbz = Fixture("nonsolid.7z", "disguised.cbz");
        var rarNamedZip = TestRars.CreateStored(Path.Combine(_root, "disguised.zip"), ("1.jpg", TestImages.Jpeg(10, 10)));

        Assert.Equal(ArchiveFormat.Zip, ArchiveFormats.Detect(zipNamedCbr)); // よくある: 名前は CBR で、中身は ZIP
        Assert.Equal(ArchiveFormat.SevenZip, ArchiveFormats.Detect(sevenNamedCbz));
        Assert.Equal(ArchiveFormat.Rar, ArchiveFormats.Detect(rarNamedZip));
    }

    [Theory]
    [InlineData("a.zip", ArchiveFormat.Zip)]
    [InlineData("a.CBZ", ArchiveFormat.Zip)]
    [InlineData("a.rar", ArchiveFormat.Rar)]
    [InlineData("a.cbr", ArchiveFormat.Rar)]
    [InlineData("a.7z", ArchiveFormat.SevenZip)]
    [InlineData("a.cb7", ArchiveFormat.SevenZip)]
    [InlineData("a.txt", ArchiveFormat.Unknown)]
    [InlineData("a", ArchiveFormat.Unknown)]
    public void ファイルがない_または署名が分からないときは_拡張子で決める(string fileName, ArchiveFormat expected)
    {
        // ファイルがない（開けない）場合も、例外にせず、拡張子で決める（実際に開くときに、理由が分かる）
        Assert.Equal(expected, ArchiveFormats.Detect(Path.Combine(_root, "none", fileName)));

        var garbage = Path.Combine(_root, fileName);
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(expected, ArchiveFormats.Detect(garbage));
    }

    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".CBZ", true)]
    [InlineData(".rar", true)]
    [InlineData(".cbr", true)]
    [InlineData(".7z", true)]
    [InlineData(".cb7", true)]
    [InlineData(".txt", false)]
    [InlineData(".tar", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 対応している拡張子(string? extension, bool expected)
    {
        Assert.Equal(expected, ArchiveFormats.IsSupportedExtension(extension));
    }

    [Theory]
    [InlineData(".zip", "zip")]
    [InlineData(".CBZ", "cbz")]
    [InlineData(".rar", "rar")]
    [InlineData(".Cbr", "cbr")]
    [InlineData(".7Z", "7z")]
    [InlineData(".cb7", "cb7")]
    [InlineData(".txt", "zip")] // 対応していなければ、DB の制約に合う値にする
    [InlineData("", "zip")]
    [InlineData(null, "zip")]
    public void DBに入れる拡張子(string? extension, string expected)
    {
        Assert.Equal(expected, ArchiveFormats.ToDatabaseExtension(extension));
    }

    [Theory]
    [InlineData("001.jpg", true)]
    [InlineData("sub/dir/002.JPEG", true)]
    [InlineData("sub\\dir\\003.PNG", true)] // RAR は、区切りが \ のことがある
    [InlineData("readme.txt", false)]
    [InlineData("image.webp", false)]
    [InlineData("noextension", false)]
    [InlineData("folder/", false)]
    [InlineData("folder\\", false)]
    [InlineData("__MACOSX/._001.jpg", false)]
    [InlineData("__macosx/001.jpg", false)]
    [InlineData("sub/._001.jpg", false)]
    [InlineData("", false)]
    public void 画像として扱うエントリの判定は_形式によらず共通(string name, bool expected)
    {
        Assert.Equal(expected, ArchiveImageFilter.IsSupportedImage(name));
    }

    // ---- 7z ----

    [Theory]
    [InlineData("nonsolid.7z")]
    [InlineData("solid.7z")]
    public void 解析すると_画像だけを数え_表紙の候補は自然順になる(string fixture)
    {
        var path = Fixture(fixture, "book.7z");

        var analysis = _reader.Analyze(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Ready, analysis.Status);
        Assert.Equal(3, analysis.ImageCount); // readme.txt と、__MACOSX の中は数えない
        Assert.Equal(["001.jpg", "002.png", "010.jpg"], analysis.CoverCandidates);
    }

    [Theory]
    [InlineData("nonsolid.7z", false)]
    [InlineData("solid.7z", true)]
    public void 開くと_自然順の画像を読め_ソリッドかどうかが分かり_書き込めない(string fixture, bool solid)
    {
        var path = Fixture(fixture, "book.7z");

        var result = _reader.Open(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Ready, result.Status);
        using var archive = result.Archive!;
        Assert.Equal(["001.jpg", "002.png", "010.jpg"], archive.Images.Select(i => i.FullName));
        Assert.Equal(solid, archive.IsSolid);
        Assert.False(archive.CanModify); // RAR / 7z は、閲覧だけ

        // 順番に関係なく、読める（後ろから読んでも）
        var third = archive.ReadEntry("010.jpg", 1_000_000, Ct);
        var first = archive.ReadEntry("001.jpg", 1_000_000, Ct);
        var second = archive.ReadEntry("002.png", 1_000_000, Ct);
        Assert.Equal((20, 30), TestImages.SizeOf(first));
        Assert.Equal((20, 30), TestImages.SizeOf(second));
        Assert.True(Pixel(first).Red > 150, "1枚目は、赤い画像");
        Assert.True(Pixel(second).Green > 150, "2枚目は、緑の画像");
        Assert.True(Pixel(third).Blue > 150, "3枚目は、青い画像");
    }

    [Fact]
    public void 開かずに1枚だけ読む_サムネイル用の読み方でも_読める()
    {
        var path = Fixture("solid.7z", "book.7z");

        var data = _reader.ReadEntry(path, "002.png", 1_000_000, Ct);

        Assert.Equal((20, 30), TestImages.SizeOf(data));
        _ = Assert.Throws<FileNotFoundException>(() => _reader.ReadEntry(path, "none.jpg", 1_000_000, Ct));
    }

    [Fact]
    public void 読み込みの上限を超えるエントリは_読まずに断る()
    {
        var path = Fixture("nonsolid.7z", "book.7z");
        var result = _reader.Open(path, Ct);
        using var archive = result.Archive!;

        _ = Assert.Throws<InvalidDataException>(() => archive.ReadEntry("001.jpg", maxBytes: 10, Ct));
        _ = Assert.Throws<InvalidDataException>(() => _reader.ReadEntry(path, "001.jpg", maxBytes: 10, Ct));
    }

    [Fact]
    public void 閉じたあとは読めず_二重に閉じても問題ない()
    {
        var path = Fixture("nonsolid.7z", "book.7z");
        var archive = _reader.Open(path, Ct).Archive!;
        archive.Dispose();
        archive.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => archive.ReadEntry("001.jpg", 1_000_000, Ct));
        File.Delete(path); // 閉じたら、ファイルを手放している（削除できる）
    }

    [Fact]
    public void 開いている間も_他のプログラムがファイルを名前変更できる状態を妨げない()
    {
        var path = Fixture("nonsolid.7z", "book.7z");
        using var archive = _reader.Open(path, Ct).Archive!;

        // 共有を許可して開いているため、名前変更（ライブラリの操作）ができる
        var renamed = Path.Combine(_root, "renamed.7z");
        File.Move(path, renamed);

        Assert.True(File.Exists(renamed));
    }

    // ---- 画像がない・暗号化・壊れている・ない ----

    [Fact]
    public void 画像がないアーカイブは_画像なしになる()
    {
        var path = Fixture("noimages.7z", "book.7z");

        Assert.Equal(ArchiveAnalysisStatus.Empty, _reader.Analyze(path, Ct).Status);
        Assert.Equal(ArchiveAnalysisStatus.Empty, _reader.Open(path, Ct).Status);
    }

    [Theory]
    [InlineData("encrypted-data.7z")] // データだけ暗号化（ファイル名は見える）
    [InlineData("encrypted-headers.7z")] // ファイル名も暗号化（一覧を読むのにパスワードが要る）
    public void 暗号化されたアーカイブは_扱えないと分かる(string fixture)
    {
        var path = Fixture(fixture, "book.7z");

        var analysis = _reader.Analyze(path, Ct);
        var opened = _reader.Open(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Unsupported, analysis.Status);
        Assert.Equal("ARCHIVE_ENCRYPTED", analysis.ErrorCode);
        Assert.Equal(ArchiveAnalysisStatus.Unsupported, opened.Status);
        Assert.Null(opened.Archive);
    }

    [Theory]
    [InlineData("book.7z")]
    [InlineData("book.rar")]
    [InlineData("book.cbr")]
    [InlineData("book.cb7")]
    public void ZIPでも7zでもRARでもない中身は_壊れていると分かる(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "これは、アーカイブではありません。" + new string('x', 200));

        var analysis = _reader.Analyze(path, Ct);
        var opened = _reader.Open(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Broken, analysis.Status);
        Assert.Equal("ARCHIVE_BROKEN", analysis.ErrorCode);
        Assert.Equal(ArchiveAnalysisStatus.Broken, opened.Status);
        Assert.Null(opened.Archive);
    }

    [Fact]
    public void 途中で切れた7zは_壊れていると分かる()
    {
        var path = Fixture("nonsolid.7z", "cut.7z");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        var analysis = _reader.Analyze(path, Ct);

        Assert.NotEqual(ArchiveAnalysisStatus.Ready, analysis.Status);
        Assert.Contains(analysis.Status, new[] { ArchiveAnalysisStatus.Broken, ArchiveAnalysisStatus.Error });
        Assert.Null(_reader.Open(path, Ct).Archive);
    }

    [Fact]
    public void ファイルがなければ_見つからないという状態になる()
    {
        var path = Path.Combine(_root, "missing.7z");

        var analysis = _reader.Analyze(path, Ct);
        var opened = _reader.Open(path, Ct);

        Assert.Equal(ArchiveAnalysisStatus.Error, analysis.Status);
        Assert.Equal("FILE_NOT_FOUND", analysis.ErrorCode);
        Assert.Equal(ArchiveAnalysisStatus.Error, opened.Status);
        Assert.Equal("FILE_NOT_FOUND", opened.ErrorCode);
    }

    // ---- 名前と中身が違う ----

    [Fact]
    public void 名前がCBRで中身がZIPなら_ZIPとして読め_書き込める()
    {
        var path = TestZips.Create(Path.Combine(_root, "actually-zip.cbr"), ("1.jpg", TestImages.Jpeg(20, 30)), ("2.jpg", TestImages.Jpeg(20, 30)));

        var analysis = _reader.Analyze(path, Ct);
        using var archive = _reader.Open(path, Ct).Archive!;

        Assert.Equal(ArchiveAnalysisStatus.Ready, analysis.Status);
        Assert.Equal(2, analysis.ImageCount);
        Assert.True(archive.CanModify); // ZIP なので、削除予約も使える
        Assert.False(archive.IsSolid);
    }

    [Fact]
    public void 名前がCBZで中身が7zなら_7zとして読み_書き込めない()
    {
        var path = Fixture("nonsolid.7z", "actually-7z.cbz");

        using var archive = _reader.Open(path, Ct).Archive!;

        Assert.False(archive.CanModify);
        Assert.Equal(3, archive.Images.Count);
    }

    // ---- RAR（テストの中で作った RAR5。無圧縮） ----

    [Fact]
    public void RARを解析し_開いて読める()
    {
        var path = TestRars.CreateStored(
            Path.Combine(_root, "book.rar"),
            ("001.jpg", TestImages.Jpeg(20, 30, 200, 40, 40)),
            ("sub/010.png", TestImages.Png(20, 30, 40, 200, 40)),
            ("readme.txt", System.Text.Encoding.UTF8.GetBytes("画像ではない")),
            ("002.jpg", TestImages.Jpeg(20, 30, 40, 40, 200)));

        var analysis = _reader.Analyze(path, Ct);
        using var archive = _reader.Open(path, Ct).Archive!;

        Assert.Equal(ArchiveAnalysisStatus.Ready, analysis.Status);
        Assert.Equal(3, analysis.ImageCount);
        // 名前の区切りは / （RAR5 は、/ を使う。RAR 4 の \ は、/ にそろえる）。自然順: 001, 002, sub/010
        Assert.Equal(["001.jpg", "002.jpg", "sub/010.png"], archive.Images.Select(i => i.FullName));
        Assert.False(archive.CanModify);
        Assert.False(archive.IsSolid);
        Assert.True(Pixel(archive.ReadEntry("001.jpg", 1_000_000, Ct)).Red > 150);
        Assert.True(Pixel(archive.ReadEntry("sub/010.png", 1_000_000, Ct)).Green > 150);
        Assert.True(Pixel(_reader.ReadEntry(path, "002.jpg", 1_000_000, Ct)).Blue > 150);
    }

    [Fact]
    public void 日本語や記号を含む名前のRARも_読める()
    {
        var path = TestRars.CreateStored(
            Path.Combine(_root, "こち亀特別漫画「ありがとうPHS！の巻」 ｜Y!mobile.rar"),
            ("第１話/001.jpg", TestImages.Jpeg(20, 30)),
            ("第１話/002.jpg", TestImages.Jpeg(20, 30)));

        using var archive = _reader.Open(path, Ct).Archive!;

        Assert.Equal(["第１話/001.jpg", "第１話/002.jpg"], archive.Images.Select(i => i.FullName));
        Assert.Equal((20, 30), TestImages.SizeOf(archive.ReadEntry("第１話/002.jpg", 1_000_000, Ct)));
    }

    [Fact]
    public void 画像のないRARは_画像なしになる()
    {
        var path = TestRars.CreateStored(Path.Combine(_root, "text.rar"), ("readme.txt", [1, 2, 3]));

        Assert.Equal(ArchiveAnalysisStatus.Empty, _reader.Analyze(path, Ct).Status);
    }
}

/// <summary>テストの後始末（作ったばかりのフォルダを、ウイルス対策ソフトなどが掴んでいることがあるため、再試行する）。</summary>
internal static class TestFileOperationsHelper
{
    public static void DeleteDirectory(string path)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }
}
