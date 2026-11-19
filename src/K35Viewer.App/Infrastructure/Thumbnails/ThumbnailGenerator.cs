using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K35Viewer.Infrastructure.Thumbnails;

/// <summary>生成したサムネイル(JPEG)。Width / Height は出力画像の大きさ。</summary>
public sealed record GeneratedThumbnail(byte[] Data, int Width, int Height);

/// <summary>画像を読み込めなかった(壊れている、未対応の形式など)。</summary>
public sealed class ThumbnailGenerationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 画像データからライブラリ用のサムネイル(JPEG)を作る。WPF標準の画像APIだけを使う。
/// 長辺を LongSide に縮小する(拡大はしない)。透過は白背景に合成する。スレッドを問わず呼べる。
/// </summary>
public sealed class ThumbnailGenerator
{
    /// <summary>サムネイルの長辺(ピクセル)。</summary>
    public const int LongSide = 300;

    /// <summary>JPEG品質(1〜100)。</summary>
    public const int JpegQuality = 80;

    /// <summary>
    /// 切り抜くときに、デコードする画像の長辺の上限（ピクセル）。小さな範囲を切り抜くと、元の画像を大きく展開することになるため、
    /// メモリを抑える（上限を超える場合は、切り抜いたあとのサムネイルが LongSide より小さくなる）。
    /// </summary>
    public const int MaxCropDecodeLongSide = 4096;

    /// <summary>
    /// サムネイルを作る。<paramref name="crop"/> を指定すると、その範囲（元の画像に対する割合）を切り抜いてから縮小する
    /// （GitHub の Issue #15）。範囲が正しくなければ、画像の全体を使う。
    /// </summary>
    public GeneratedThumbnail Generate(byte[] imageData, K35Viewer.Domain.CoverCrop? crop = null)
    {
        ArgumentNullException.ThrowIfNull(imageData);

        try
        {
            var (sourceWidth, sourceHeight) = ReadSize(imageData);
            BitmapSource scaled;
            if (crop is { IsValid: true })
            {
                scaled = DecodeCropped(imageData, sourceWidth, sourceHeight, crop);
            }
            else
            {
                var (width, height) = FitInside(sourceWidth, sourceHeight);
                scaled = Decode(imageData, width, height);
            }

            var opaque = CompositeOnWhite(scaled);
            return new GeneratedThumbnail(EncodeJpeg(opaque), opaque.PixelWidth, opaque.PixelHeight);
        }
        catch (ThumbnailGenerationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException
            or ArgumentException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            // WPFのデコーダーは、壊れた画像で上記のような様々な例外を投げる
            throw new ThumbnailGenerationException("画像を読み込めません", ex);
        }
    }

    /// <summary>縦横比を保ち、長辺が LongSide に収まる大きさを返す。元が小さければそのまま(拡大しない)。</summary>
    internal static (int Width, int Height) FitInside(int width, int height)
    {
        var longSide = Math.Max(width, height);
        if (longSide <= LongSide)
        {
            return (width, height);
        }

        var scale = (double)LongSide / longSide;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    // デコードせずにヘッダーから大きさだけを読む(巨大な画像を全展開する前に縮小率を決めるため)
    private static (int Width, int Height) ReadSize(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            throw new ThumbnailGenerationException("画像の大きさが不正です");
        }

        return (frame.PixelWidth, frame.PixelHeight);
    }

    // デコード時に縮小することで、元画像の大きさに関わらずメモリと時間を抑える
    private static BitmapImage Decode(byte[] data, int width, int height)
    {
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        if (width >= height)
        {
            image.DecodePixelWidth = width;
        }
        else
        {
            image.DecodePixelHeight = height;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 切り抜いた範囲の長辺が LongSide になる縮小率で、画像全体をデコードしてから、範囲を切り抜く
    /// （拡大はしない。デコードする大きさは <see cref="MaxCropDecodeLongSide"/> まで）。
    /// </summary>
    private static CroppedBitmap DecodeCropped(byte[] data, int sourceWidth, int sourceHeight, K35Viewer.Domain.CoverCrop crop)
    {
        var (_, _, cropWidth, cropHeight) = crop.ToPixels(sourceWidth, sourceHeight);
        var scale = Math.Min(1.0, (double)LongSide / Math.Max(cropWidth, cropHeight));
        scale = Math.Min(scale, (double)MaxCropDecodeLongSide / Math.Max(sourceWidth, sourceHeight));
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var decoded = Decode(data, width, height);

        // 実際にデコードされた大きさ（丸めで、指定とずれることがある）を基準に、範囲を求め直す
        var (x, y, w, h) = crop.ToPixels(decoded.PixelWidth, decoded.PixelHeight);
        var cropped = new CroppedBitmap(decoded, new System.Windows.Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// 任意の画素形式を Pbgra32 に揃え、白背景に合成した不透明な画像(Bgr32)にする。
    /// 事前乗算済みなので、白への合成は「色 + (255 - α)」で求まる。
    /// </summary>
    private static BitmapSource CompositeOnWhite(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var inverseAlpha = 255 - pixels[i + 3];
            pixels[i] = (byte)Math.Min(255, pixels[i] + inverseAlpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + inverseAlpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + inverseAlpha);
            pixels[i + 3] = 255;
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static byte[] EncodeJpeg(BitmapSource image)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
