using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using K35Viewer.Infrastructure.Archives;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>ZIPの再構築（削除対象を除いたコピー）と検証。元のZIPは変更しない。</summary>
public class ArchiveWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "writer-" + Guid.NewGuid().ToString("N"));

    public ArchiveWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static readonly DateTimeOffset Stamp = new(2021, 3, 4, 5, 6, 8, TimeSpan.Zero);

    /// <summary>エントリごとに異なる外部属性（Unixのファイルモード）。保たれることの確認用。</summary>
    private static int AttributesOf(string entryName) => entryName.EndsWith('/') ? 0x41ED << 16 : (entryName.Length % 2 == 0 ? 0x81A4 : 0x81ED) << 16;

    /// <summary>フォルダ・画像・画像以外・更新日時を含むZIPを作る。</summary>
    private string CreateSource(string name = "source.zip", Encoding? entryNameEncoding = null)
    {
        var path = PathOf(name);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding);

        void Add(string entryName, byte[]? data, DateTimeOffset time)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = time;
            entry.ExternalAttributes = AttributesOf(entryName);
            if (entryName == "readme.txt")
            {
                entry.Comment = "読む前のメモ";
            }

            if (data is not null)
            {
                using var output = entry.Open();
                output.Write(data);
            }
        }

        Add("pages/", null, Stamp);
        Add("pages/1.jpg", TestImages.Jpeg(120, 80), Stamp.AddHours(1));
        Add("pages/2.jpg", TestImages.Jpeg(130, 80), Stamp.AddHours(2));
        Add("pages/3.png", TestImages.Png(140, 80), Stamp.AddHours(3));
        Add("readme.txt", Encoding.UTF8.GetBytes("読んでください"), Stamp.AddHours(4));
        return path;
    }

    private static List<(string Name, byte[] Data, DateTimeOffset Time)> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (e.FullName, buffer.ToArray(), e.LastWriteTime);
        })];
    }

    private static List<(string Name, int Attributes, string Comment)> ReadMetadata(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Select(e => (e.FullName, e.ExternalAttributes, e.Comment))];
    }

    [Fact]
    public void Rebuild_指定したエントリだけを除き_他のエントリを順序と内容と更新日時を保ってコピーする()
    {
        var source = CreateSource();
        var before = Hash(source);
        var destination = PathOf("out.k35tmp");

        var result = new ArchiveWriter().Rebuild(source, destination, new HashSet<string> { "pages/2.jpg" }, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.RemovedCount);
        var expected = Read(source).Where(e => e.Name != "pages/2.jpg").ToList();
        var actual = Read(destination);
        Assert.Equal(expected.Select(e => e.Name), actual.Select(e => e.Name)); // フォルダを含め、順序も同じ
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Data, actual[i].Data);
            Assert.Equal(expected[i].Time, actual[i].Time);
        }

        // 外部属性とエントリのコメントも保たれる
        Assert.Equal(
            ReadMetadata(source).Where(e => e.Name != "pages/2.jpg"),
            ReadMetadata(destination));
        Assert.Contains(ReadMetadata(destination), e => e.Name == "readme.txt" && e.Comment == "読む前のメモ");

        Assert.Equal(before, Hash(source)); // 元のZIPは変更しない
        Assert.Equal(
            expected.Select(e => (e.Name, (long)e.Data.Length)),
            result.Entries!.Select(e => (e.FullName, e.Length)));
        Assert.True(new ArchiveWriter().Verify(destination, result.Entries!, Ct).Succeeded);
    }

    [Fact]
    public void Rebuild_ほとんど圧縮されていないエントリは圧縮し直さず_圧縮の効くエントリは圧縮する()
    {
        var source = PathOf("mixed.zip");
        var random = new byte[100 * 1024];
        new Random(1).NextBytes(random); // 圧縮済みの画像のように、圧縮が効かないデータ
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 25_000)));
        using (var stream = File.Create(source))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in new[] { ("random.jpg", random), ("text.txt", text), ("other.jpg", random) })
            {
                using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                output.Write(data);
            }
        }

        var destination = PathOf("out.k35tmp");
        var result = new ArchiveWriter().Rebuild(source, destination, new HashSet<string> { "other.jpg" }, Ct);

        Assert.True(result.Succeeded);
        using var rebuilt = ZipFile.OpenRead(destination);
        var image = rebuilt.GetEntry("random.jpg")!;
        var compressible = rebuilt.GetEntry("text.txt")!;
        Assert.Equal(image.Length, image.CompressedLength); // 圧縮せずに保存（時間をかけて再圧縮しない）
        Assert.True(compressible.CompressedLength < compressible.Length / 10, "圧縮の効くエントリは圧縮されていません。");
        Assert.Equal(random, Read(destination).First(e => e.Name == "random.jpg").Data);
    }

    [Fact]
    public void Rebuild_すべての画像を除いても_フォルダと画像以外は残る()
    {
        var source = CreateSource();
        var destination = PathOf("out.k35tmp");

        var result = new ArchiveWriter().Rebuild(
            source, destination, new HashSet<string> { "pages/1.jpg", "pages/2.jpg", "pages/3.png" }, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["pages/", "readme.txt"], Read(destination).Select(e => e.Name));
        Assert.Equal(3, result.RemovedCount);
    }

    [Fact]
    public void Rebuild_削除対象がアーカイブにない場合は失敗する()
    {
        var source = CreateSource();

        var result = new ArchiveWriter().Rebuild(source, PathOf("out.k35tmp"), new HashSet<string> { "pages/9.jpg" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("ENTRY_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Rebuild_出力先がすでにある場合は_上書きせずに失敗する()
    {
        var source = CreateSource();
        var destination = PathOf("out.k35tmp");
        File.WriteAllText(destination, "既存のファイル");

        var result = new ArchiveWriter().Rebuild(source, destination, new HashSet<string> { "pages/1.jpg" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("IO_ERROR", result.ErrorCode);
        Assert.Equal("既存のファイル", File.ReadAllText(destination));
    }

    [Fact]
    public void Rebuild_元のファイルがなければ_FILE_NOT_FOUNDで失敗する()
    {
        var result = new ArchiveWriter().Rebuild(PathOf("none.zip"), PathOf("out.k35tmp"), new HashSet<string>(), Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Rebuild_ZIPではないファイルは_ARCHIVE_BROKENで失敗する()
    {
        var source = PathOf("broken.zip");
        File.WriteAllBytes(source, new byte[200]);

        var result = new ArchiveWriter().Rebuild(source, PathOf("out.k35tmp"), new HashSet<string> { "a" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("ARCHIVE_BROKEN", result.ErrorCode);
    }

    [Fact]
    public void Rebuild_キャンセルされると_例外で中断し_元のZIPは変更されない()
    {
        var source = CreateSource();
        var before = Hash(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = Assert.Throws<OperationCanceledException>(
            () => new ArchiveWriter().Rebuild(source, PathOf("out.k35tmp"), new HashSet<string> { "pages/1.jpg" }, cts.Token));

        Assert.Equal(before, Hash(source));
    }

    /// <summary>UTF-8 のフラグを付けずに、指定した文字コードでエントリ名を保存したZIPを作る（古いツールで作られたZIPを模す）。</summary>
    private string CreateLegacyZip(string name, Encoding encoding, params string[] entryNames)
    {
        var path = PathOf(name);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, encoding);
        foreach (var entryName in entryNames)
        {
            using var output = zip.CreateEntry(entryName).Open();
            output.Write(TestImages.Jpeg(50, 50));
        }

        return path;
    }

    private static Encoding ShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    [Fact]
    public void Rebuild_ShiftJISのエントリ名のアーカイブも_名前を保って再構築でき_新しいZIPではUTF8で保存される()
    {
        var source = CreateLegacyZip("cp932.zip", ShiftJis(), "第1話/ページ１.jpg", "第1話/ページ２.jpg", "表紙.jpg");
        var destination = PathOf("out.k35tmp");

        var result = new ArchiveWriter().Rebuild(source, destination, new HashSet<string> { "第1話/ページ２.jpg" }, Ct);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(["第1話/ページ１.jpg", "表紙.jpg"], Read(destination).Select(e => e.Name));
        Assert.True(new ArchiveWriter().Verify(destination, result.Entries!, Ct).Succeeded);

        // 新しいZIPは UTF-8 のフラグ付きで保存され、文字コードを指定しない他のツールでも同じ名前に読める
        using var plain = ZipFile.OpenRead(destination);
        Assert.Equal(["第1話/ページ１.jpg", "表紙.jpg"], plain.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void Rebuild_UTF8でもShiftJISでも読めない名前のアーカイブは_名前を壊さないよう扱わない()
    {
        // 0x81 0x20: UTF-8 では不正、Shift_JIS では2バイト文字の1バイト目のあとに使えないバイトが続く（どちらでも読めない）
        var source = CreateLegacyZip("broken-names.zip", Encoding.Latin1, "\u0081 .jpg");

        var result = new ArchiveWriter().Rebuild(source, PathOf("out.k35tmp"), new HashSet<string>(), Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("UNSUPPORTED_ENTRY_NAME", result.ErrorCode);
    }

    [Fact]
    public void ArchiveReader_ShiftJISのエントリ名を正しく読み_自然順に並べて画像を読み出せる()
    {
        var source = CreateLegacyZip("cp932.zip", ShiftJis(), "ページ10.jpg", "ページ2.jpg", "あとがき.jpg");
        var reader = new ArchiveReader();

        var analysis = reader.Analyze(source, Ct);
        var opened = reader.Open(source, Ct);
        using var archive = opened.Archive!;

        Assert.Equal(K35Viewer.Domain.ArchiveAnalysisStatus.Ready, analysis.Status);
        Assert.Equal(3, analysis.ImageCount);
        Assert.Equal(["あとがき.jpg", "ページ2.jpg", "ページ10.jpg"], archive.Images.Select(i => i.FullName));
        Assert.NotEmpty(archive.ReadEntry("ページ2.jpg", 1_000_000, Ct));
        Assert.NotEmpty(reader.ReadEntry(source, "あとがき.jpg", 1_000_000, Ct));
        Assert.Equal(analysis.CoverCandidates[0], archive.Images[0].FullName); // 解析と閲覧で名前が一致する
    }

    [Theory]
    [InlineData("plain-ascii.jpg")]
    [InlineData("日本語.jpg")]
    [InlineData("한국어.png")]
    public void ZipEntryNameEncoding_UTF8として正しい名前は_UTF8のまま読む(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);

        Assert.Equal(name, ZipEntryNameEncoding.Instance.GetString(bytes));
        Assert.Equal(name.Length, ZipEntryNameEncoding.Instance.GetCharCount(bytes, 0, bytes.Length));
    }

    [Fact]
    public void ZipEntryNameEncoding_UTF8として不正な名前は_ShiftJISとして読む()
    {
        var bytes = ShiftJis().GetBytes("漫画/第１巻.jpg");

        Assert.Equal("漫画/第１巻.jpg", ZipEntryNameEncoding.Instance.GetString(bytes));
        var chars = new char[ZipEntryNameEncoding.Instance.GetMaxCharCount(bytes.Length)];
        var count = ZipEntryNameEncoding.Instance.GetChars(bytes, 0, bytes.Length, chars, 0);
        Assert.Equal("漫画/第１巻.jpg", new string(chars, 0, count));
    }

    [Fact]
    public void Rebuild_UTF8の日本語のエントリ名は_そのまま保たれる()
    {
        var source = PathOf("utf8.zip");
        using (var stream = File.Create(source))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var name in new[] { "第1話/ページ１.jpg", "第1話/ページ２.jpg" })
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write(TestImages.Jpeg(50, 50));
            }
        }

        var destination = PathOf("out.k35tmp");
        var result = new ArchiveWriter().Rebuild(source, destination, new HashSet<string> { "第1話/ページ２.jpg" }, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["第1話/ページ１.jpg"], Read(destination).Select(e => e.Name));
    }

    [Fact]
    public void Verify_途中で壊れたZIPや_内容が異なるZIPは失敗する()
    {
        var source = CreateSource();
        var destination = PathOf("out.k35tmp");
        var built = new ArchiveWriter().Rebuild(source, destination, new HashSet<string> { "pages/1.jpg" }, Ct);
        Assert.True(built.Succeeded);

        // 期待するエントリと一致しない
        var wrong = built.Entries!.Take(built.Entries!.Count - 1).ToList();
        var mismatch = new ArchiveWriter().Verify(destination, wrong, Ct);
        Assert.False(mismatch.Succeeded);
        Assert.Equal("VERIFY_FAILED", mismatch.ErrorCode);

        var different = built.Entries!.Select(e => e with { Length = e.Length + 1 }).ToList();
        Assert.Equal("VERIFY_FAILED", new ArchiveWriter().Verify(destination, different, Ct).ErrorCode);

        // 末尾が欠けた（途中までしか書けなかった）ZIP
        var truncated = PathOf("truncated.k35tmp");
        var bytes = File.ReadAllBytes(destination);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        var broken = new ArchiveWriter().Verify(truncated, built.Entries!, Ct);
        Assert.False(broken.Succeeded);
        Assert.Equal("ARCHIVE_BROKEN", broken.ErrorCode);
    }

    [Fact]
    public void Verify_データが1バイト書き換わっていると_サイズが同じでもCRCの不一致で失敗する()
    {
        var source = CreateSource();
        var destination = PathOf("out.k35tmp");
        var built = new ArchiveWriter(CompressionLevel.NoCompression).Rebuild(
            source, destination, new HashSet<string> { "pages/1.jpg" }, Ct);
        Assert.True(built.Succeeded);
        Assert.True(new ArchiveWriter().Verify(destination, built.Entries!, Ct).Succeeded);

        // 圧縮なしで保存した「readme.txt」の本文を、1バイトだけ書き換える（展開はでき、長さも同じ）
        var bytes = File.ReadAllBytes(destination);
        var index = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("読んでください"));
        Assert.True(index > 0);
        bytes[index] ^= 0xFF;
        var corrupted = PathOf("corrupted.k35tmp");
        File.WriteAllBytes(corrupted, bytes);

        var result = new ArchiveWriter().Verify(corrupted, built.Entries!, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("VERIFY_FAILED", result.ErrorCode);
    }

    [Theory]
    [InlineData("", 0u)]
    [InlineData("hello", 0x3610A686u)]
    [InlineData("123456789", 0xCBF43926u)]
    public void Crc32Calculator_ZIPと同じCRC32を計算し_分割して加えても同じ結果になる(string text, uint expected)
    {
        var data = Encoding.ASCII.GetBytes(text);

        Assert.Equal(expected, Crc32Calculator.Update(0, data));

        var half = data.Length / 2;
        var split = Crc32Calculator.Update(Crc32Calculator.Update(0, data.AsSpan(0, half)), data.AsSpan(half));
        Assert.Equal(expected, split);
    }

    [Theory]
    [InlineData(112, "DISK_FULL")]
    [InlineData(39, "DISK_FULL")]
    [InlineData(32, "IO_ERROR")]
    public void ClassifyIOException_ディスク容量不足とそれ以外を区別する(int win32Error, string expected)
    {
        var exception = new IOException("テスト", unchecked((int)(0x80070000 | (uint)win32Error)));

        Assert.Equal(expected, ArchiveWriter.ClassifyIOException(exception));
    }
}
