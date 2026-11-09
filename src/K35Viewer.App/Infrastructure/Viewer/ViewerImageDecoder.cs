using System.Windows.Media.Imaging;

namespace K35Viewer.Infrastructure.Viewer;

/// <summary>画像を読み込めなかった(壊れている、未対応の形式など)。</summary>
public sealed class ImageDecodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// ビューアで表示する画像のデコード。WPF標準の画像APIだけを使う。
/// デコード後の画像は凍結しているので、背景スレッドで作ってUIスレッドで使える。
/// 画素数が上限を超える巨大な画像は、デコード時に縮小して読み込む(メモリを使い切らないため)。
/// </summary>
public sealed class ViewerImageDecoder(int maxPixels = ViewerImageDecoder.DefaultMaxPixels)
{
    /// <summary>
    /// デコードする画素数の上限（既定3,600万画素。約144MBに相当）。通常のページ画像(数百万画素)や
    /// 高解像度の写真(2,400万画素)は、そのままの大きさで読み込む。
    /// </summary>
    public const int DefaultMaxPixels = 36_000_000;

    public BitmapSource Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            var (width, height) = ReadSize(data);
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;

            var pixels = (long)width * height;
            if (pixels > maxPixels)
            {
                var scale = Math.Sqrt((double)maxPixels / pixels);
                if (width >= height)
                {
                    image.DecodePixelWidth = Math.Max(1, (int)(width * scale));
                }
                else
                {
                    image.DecodePixelHeight = Math.Max(1, (int)(height * scale));
                }
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException
            or ArgumentException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            // WPFのデコーダーは、壊れた画像で様々な例外を投げる
            throw new ImageDecodeException("画像を読み込めません", ex);
        }
    }

    // デコードせずにヘッダーから大きさだけを読む
    private static (int Width, int Height) ReadSize(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            throw new ImageDecodeException("画像の大きさが不正です");
        }

        return (frame.PixelWidth, frame.PixelHeight);
    }
}
