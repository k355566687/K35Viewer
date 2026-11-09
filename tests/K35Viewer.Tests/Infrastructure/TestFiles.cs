using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>テスト用の画像データ(PNG / JPEG)を、WPFのエンコーダーで生成する。</summary>
internal static class TestImages
{
    /// <summary>単色の画像。alpha が 255 未満なら透過(PNGのみ意味を持つ)。</summary>
    public static byte[] Png(int width, int height, byte red = 200, byte green = 40, byte blue = 40, byte alpha = 255) =>
        Encode(new PngBitmapEncoder(), Pixels(width, height, (_, _) => (red, green, blue, alpha)), width, height);

    public static byte[] Jpeg(int width, int height, byte red = 40, byte green = 90, byte blue = 200) =>
        Encode(new JpegBitmapEncoder { QualityLevel = 90 }, Pixels(width, height, (_, _) => (red, green, blue, 255)), width, height);

    /// <summary>
    /// 濃淡と縞のある画像のJPEG。単色よりも実際のページ画像に近く、デコードの負荷も現実的になる。
    /// </summary>
    public static byte[] JpegPattern(int width, int height) =>
        Encode(
            new JpegBitmapEncoder { QualityLevel = 85 },
            Pixels(
                width,
                height,
                (x, y) =>
                {
                    var stripe = ((x / 24) + (y / 24)) % 2 == 0 ? 0 : 40;
                    return ((byte)((x * 255 / width) + stripe), (byte)((y * 255 / height) + stripe), (byte)(((x + y) * 255 / (width + height)) + stripe), (byte)255);
                }),
            width,
            height);

    /// <summary>左半分が完全に透明、右半分が不透明な赤のPNG。</summary>
    public static byte[] PngHalfTransparent(int width, int height) =>
        Encode(
            new PngBitmapEncoder(),
            Pixels(width, height, (x, _) => x < width / 2 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)0, (byte)0, (byte)255)),
            width,
            height);

    /// <summary>画像として読めない(壊れた)データ。</summary>
    public static byte[] Corrupt() => [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03, 0x04];

    /// <summary>JPEG / PNG のバイト列を読み、指定した位置の画素を (R, G, B) で返す。</summary>
    public static (byte Red, byte Green, byte Blue) PixelAt(byte[] image, int x, int y)
    {
        using var stream = new MemoryStream(image);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    public static (int Width, int Height) SizeOf(byte[] image)
    {
        using var stream = new MemoryStream(image);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static byte[] Pixels(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var index = ((y * width) + x) * 4;
                data[index] = b;
                data[index + 1] = g;
                data[index + 2] = r;
                data[index + 3] = a;
            }
        }

        return data;
    }

    private static byte[] Encode(BitmapEncoder encoder, byte[] bgra, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

internal static class TestZips
{
    /// <summary>指定したエントリを持つZIPを作る(エントリ名にフォルダを含められる)。</summary>
    public static string Create(string path, params (string Name, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    /// <summary>
    /// 全エントリに「暗号化」フラグを立てたZIPを作る。.NET標準では暗号化ZIPを作れないため、
    /// セントラルディレクトリの汎用ビットフラグ(bit 0)を書き換えて模す。
    /// </summary>
    public static string CreateEncryptedLooking(string path, params (string Name, byte[] Data)[] entries)
    {
        Create(path, entries);
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 8 < bytes.Length; i++)
        {
            // セントラルディレクトリのファイルヘッダー: 50 4B 01 02、オフセット8が汎用ビットフラグ
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                bytes[i + 8] |= 0x01;
            }
        }

        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }
}
