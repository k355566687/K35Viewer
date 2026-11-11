using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Tools;

/// <summary>
/// 画面の確認（スクリーンショット）用の、架空のサンプルライブラリを作る道具。通常のテストでは実行しない。
/// 環境変数 K35_SAMPLE_ROOT に出力先を指定したときだけ動く（既存のフォルダは作り直す）。
/// 出力: 〈出力先〉\library（アーカイブ）と 〈出力先〉\data（アプリのデータ。K35VIEWER_DATA_ROOT に指定して起動する）。
/// </summary>
public class SampleLibraryGenerator
{
    private const string RootVariable = "K35_SAMPLE_ROOT";

    private static readonly (string Folder, string Title, int Pages)[] Archives =
    [
        (@"漫画\長編", "星の降る街 第01巻", 24),
        (@"漫画\長編", "星の降る街 第02巻", 22),
        (@"漫画\長編", "星の降る街 第03巻", 26),
        (@"漫画\長編", "星の降る街 第10巻", 20),
        (@"漫画\長編", "海辺のアトリエ 上巻", 30),
        (@"漫画\長編", "海辺のアトリエ 下巻", 28),
        (@"漫画\短編集", "夏の終わりの短編集", 18),
        (@"漫画\短編集", "とても長いタイトルの短編集 ～放課後の図書室で見つけた古い地図と七つの謎～", 16),
        (@"漫画\短編集", "Weekend Sketches", 12),
        (@"画集", "風景スケッチ 2024", 40),
        (@"画集", "風景スケッチ 2025", 36),
        (@"画集", "Color Studies", 15),
        (@"資料\参考", "背景資料 建物編", 50),
        (@"資料\参考", "背景資料 自然編", 45),
        (@"資料", "ポーズ集 vol.1", 60),
        (@"資料", "ポーズ集 vol.2", 55),
        ("", "未整理のスキャン", 8),
        ("", "旅行の写真 京都", 33),
    ];

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x3B, 0x6E, 0xA5), Color.FromRgb(0xA5, 0x4B, 0x3B), Color.FromRgb(0x3B, 0x8C, 0x5A),
        Color.FromRgb(0x8C, 0x6A, 0x2E), Color.FromRgb(0x6A, 0x4B, 0x9E), Color.FromRgb(0x2E, 0x7F, 0x86),
        Color.FromRgb(0x9E, 0x3B, 0x6E), Color.FromRgb(0x55, 0x55, 0x60),
    ];

    [Fact]
    public async Task サンプルライブラリを作る()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"環境変数 {RootVariable} に出力先を指定したときだけ実行します");

        var ct = TestContext.Current.CancellationToken;
        root = Path.GetFullPath(root!);
        var library = Path.Combine(root, "library");
        var data = Path.Combine(root, "data");
        foreach (var directory in new[] { library, data })
        {
            TestFileOperations.DeleteDirectory(directory);
            _ = Directory.CreateDirectory(directory);
        }

        // アーカイブ（頁ごとに色と番号の入った画像）
        var index = 0;
        var baseTime = new DateTime(2025, 4, 1, 21, 0, 0, DateTimeKind.Local);
        foreach (var (folder, title, pages) in Archives)
        {
            var path = Path.Combine(library, folder, title + (index % 5 == 4 ? ".cbz" : ".zip"));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var color = Palette[index % Palette.Length];
            CreateArchive(path, title, pages, color);
            File.SetLastWriteTime(path, baseTime.AddDays(index * 9).AddHours(index));
            index++;
        }

        // 一覧で状態を見分けるためのもの: 壊れたZIP、画像のないZIP、Shift_JISの名前のZIP
        File.WriteAllBytes(Path.Combine(library, "資料", "壊れたアーカイブ.zip"), Encoding.ASCII.GetBytes("this is not a zip file"));
        using (var stream = File.Create(Path.Combine(library, "資料", "メモだけ.zip")))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var output = zip.CreateEntry("readme.txt").Open();
            output.Write(Encoding.UTF8.GetBytes("画像はありません"));
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var stream = File.Create(Path.Combine(library, "漫画", "短編集", "古いツールで作った本.zip")))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.GetEncoding(932)))
        {
            for (var page = 1; page <= 6; page++)
            {
                using var output = zip.CreateEntry($"ページ{page:D2}.jpg").Open();
                output.Write(Page("古いツールで作った本", page, 6, Palette[7]));
            }
        }

        // アプリと同じ部品で、登録・スキャン・サムネイルの作成まで行う
        var paths = new AppPaths(data);
        paths.EnsureDirectories();
        var factory = new SqliteConnectionFactory(paths.DatabaseFile);
        await new DatabaseInitializer(factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(ct);
        using var writer = new DatabaseWriter(factory);
        var fileSystem = new FileSystemService();
        var libraries = new LibraryRepository(factory);
        var archives = new ArchiveRepository(factory);
        var history = new HistoryRepository();
        var cache = new ThumbnailCacheStore(paths.ThumbnailsDirectory);
        var notifications = new FakeNotificationService();
        var libraryService = new LibraryService(
            libraries, archives, history, writer, fileSystem, cache, notifications, NullLogger<LibraryService>.Instance);
        using var scan = new ScanService(
            fileSystem, libraries, archives, new ScanRepository(), history, writer, notifications, NullLogger<ScanService>.Instance);
        var thumbnails = new ThumbnailService(
            new ArchiveReader(), new ThumbnailGenerator(), cache, fileSystem, archives, new ThumbnailRepository(factory), writer,
            NullLogger<ThumbnailService>.Instance);

        var added = await libraryService.AddFolderAsync(library, confirmMerge: true, ct);
        _ = await scan.ScanAsync(added.LibraryFolderId!.Value, ScanType.Initial, ct);
        _ = await thumbnails.ProcessPendingAsync(ct: ct);

        var count = (await archives.QueryAsync(new ArchiveQuery(), ct)).TotalCount;
        TestContext.Current.SendDiagnosticMessage($"サンプルライブラリ: {library} / データ: {data} / アーカイブ {count}件");
        Assert.Equal(Archives.Length + 3, count);
    }

    private static void CreateArchive(string path, string title, int pages, Color color)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        for (var page = 1; page <= pages; page++)
        {
            using var output = zip.CreateEntry($"{page:D3}.jpg", CompressionLevel.NoCompression).Open();
            output.Write(Page(title, page, pages, color));
        }
    }

    /// <summary>頁の画像（縦長、色の帯・タイトル・頁番号）。WPFの描画で作る。</summary>
    private static byte[] Page(string title, int page, int pages, Color color)
    {
        const int width = 600;
        const int height = 850;
        byte[]? result = null;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xEA)), null, new Rect(0, 0, width, height));
                var shade = (byte)Math.Clamp(40 + (page * 7 % 60), 0, 255);
                var band = Color.FromRgb(
                    (byte)Math.Min(255, color.R + shade / 3), (byte)Math.Min(255, color.G + shade / 3), (byte)Math.Min(255, color.B + shade / 3));
                context.DrawRectangle(new SolidColorBrush(band), null, new Rect(0, 0, width, page == 1 ? height * 0.62 : 120));

                var typeface = new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var heading = new FormattedText(
                    page == 1 ? title : $"{page} / {pages}",
                    System.Globalization.CultureInfo.GetCultureInfo("ja-JP"),
                    FlowDirection.LeftToRight,
                    typeface,
                    page == 1 ? 40 : 36,
                    Brushes.White,
                    1.0) { MaxTextWidth = width - 80, TextAlignment = TextAlignment.Center };
                context.DrawText(heading, new Point(40, page == 1 ? 180 : 38));

                if (page > 1)
                {
                    // 本文らしい罫線
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xC9, 0xC2, 0xB4)), 3);
                    for (var line = 0; line < 12; line++)
                    {
                        var y = 190 + (line * 52);
                        var length = 80 + ((page * 37 + line * 53) % 400);
                        context.DrawLine(pen, new Point(60, y), new Point(60 + length, y));
                    }
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            result = buffer.ToArray();
        });

        return result!;
    }
}
