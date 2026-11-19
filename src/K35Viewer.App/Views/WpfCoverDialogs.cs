using System.Windows;
using System.Windows.Media.Imaging;
using K35Viewer.Domain;
using K35Viewer.Services;

namespace K35Viewer.Views;

/// <summary>表紙の切り抜きの画面（GitHub の Issue #15）。</summary>
public sealed class WpfCoverDialogs : ICoverDialogs
{
    /// <summary>切り抜きの画面に出すために、デコードする画像の長辺の上限（ピクセル）。範囲は割合で返すため、元の大きさは要らない。</summary>
    private const int MaxDecodeLongSide = 1600;

    public CoverCrop? PickCrop(byte[] imageData, string entryName)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        var image = Decode(imageData);
        var dialog = new CoverCropDialog(image, entryName);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.Crop : null;
    }

    // サムネイルの作り方（ThumbnailGenerator）と同じく、画像の向きの情報は使わない（範囲が、作るサムネイルとずれないように）
    private static BitmapImage Decode(byte[] data)
    {
        try
        {
            int width, height;
            using (var probe = new MemoryStream(data))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
            }

            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            if (Math.Max(width, height) > MaxDecodeLongSide)
            {
                if (width >= height)
                {
                    image.DecodePixelWidth = MaxDecodeLongSide;
                }
                else
                {
                    image.DecodePixelHeight = MaxDecodeLongSide;
                }
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException
            or ArgumentException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException("画像を読み込めません", ex);
        }
    }
}
