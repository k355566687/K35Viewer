using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Tests.Services;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>
/// ZIP再構築の性能の測定（開発環境設計書 13.3: 圧縮レベルの初期値を決める根拠）。
/// 測定値は診断メッセージに出力する。速度そのものは検証せず、結果が正しいことだけを確認する。
/// </summary>
[Collection(MemoryMeasurementGroup.Name)]
public class ArchiveWriterPerformanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "writerperf-" + Guid.NewGuid().ToString("N"));

    public ArchiveWriterPerformanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 圧縮レベルごとの再構築の処理時間と容量を測る_圧縮が効く合成画像()
    {
        // 実際のコミックの1頁に近い大きさ（2400x3400）のJPEGを150頁。全頁が同じ内容では圧縮が極端に効くため、頁ごとに末尾を変える。
        // 合成した縞模様のため、実際の写真やスキャン画像よりも圧縮が効く（参考値）
        var page = TestImages.JpegPattern(2400, 3400);
        Measure("合成画像(圧縮が効く)", i =>
        {
            var data = (byte[])page.Clone();
            data[^1] = (byte)i;
            return data;
        });
    }

    [Fact]
    public void 圧縮レベルごとの再構築の処理時間と容量を測る_圧縮がほとんど効かない画像()
    {
        // 実際のJPEG / PNG（圧縮済みの画像）は、ZIPでさらに圧縮してもほとんど小さくならない。乱数のデータでそれを模す（1頁 300KB）
        var random = new Random(12345);
        Measure("圧縮済み相当(乱数)", _ =>
        {
            var data = new byte[300 * 1024];
            random.NextBytes(data);
            return data;
        });
    }

    private void Measure(string label, Func<int, byte[]> createPage)
    {
        const int pages = 150;
        var source = Path.Combine(_directory, "source.zip");
        using (var stream = File.Create(source))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            for (var i = 0; i < pages; i++)
            {
                using var output = zip.CreateEntry($"{i + 1:D4}.jpg", CompressionLevel.Optimal).Open();
                output.Write(createPage(i));
            }
        }

        var sourceMb = new FileInfo(source).Length / 1024.0 / 1024.0;
        var excluded = new HashSet<string>(Enumerable.Range(1, 10).Select(i => $"{i * 10:D4}.jpg"));
        var lines = new List<string>();
        // 圧縮レベルを固定した場合の比較と、既定（Optimal。ほとんど圧縮されていないエントリはそのまま保存）
        var variants = new (string Name, ArchiveWriter Writer)[]
        {
            ("NoCompression", new ArchiveWriter(CompressionLevel.NoCompression, storeBarelyCompressed: false)),
            ("Fastest", new ArchiveWriter(CompressionLevel.Fastest, storeBarelyCompressed: false)),
            ("Optimal", new ArchiveWriter(CompressionLevel.Optimal, storeBarelyCompressed: false)),
            ("既定", new ArchiveWriter()),
        };
        foreach (var (name, writer) in variants)
        {
            var destination = Path.Combine(_directory, $"out-{name}.k35tmp");

            var rebuildWatch = Stopwatch.StartNew();
            var built = writer.Rebuild(source, destination, excluded, TestContext.Current.CancellationToken);
            rebuildWatch.Stop();
            Assert.True(built.Succeeded);

            var verifyWatch = Stopwatch.StartNew();
            var verified = writer.Verify(destination, built.Entries!, TestContext.Current.CancellationToken);
            verifyWatch.Stop();
            Assert.True(verified.Succeeded);

            var outMb = new FileInfo(destination).Length / 1024.0 / 1024.0;
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: 作成 {rebuildWatch.ElapsedMilliseconds}ms / 検証 {verifyWatch.ElapsedMilliseconds}ms / {outMb:F1}MB"));
        }

        TestContext.Current.SendDiagnosticMessage(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ZIP再構築[{label}]: 元 {sourceMb:F1}MB({pages}頁・10頁削除) / {string.Join(" / ", lines)}"));
    }
}
