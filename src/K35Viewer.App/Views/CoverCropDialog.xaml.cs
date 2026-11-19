using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using K35Viewer.Domain;
using K35Viewer.ViewModels;

namespace K35Viewer.Views;

/// <summary>
/// 表紙の切り抜きの画面（GitHub の Issue #15）。画像を出し、カードと同じ縦横比の枠を重ねる。
/// 枠の計算は <see cref="CoverCropEditor"/> が行い、ここでは、表示と、マウス・キーの操作を受けるだけ。
/// </summary>
public partial class CoverCropDialog : Window
{
    /// <summary>画像を出す領域の、最大の大きさ（画面の単位）。</summary>
    private const double MaxDisplayWidth = 860;
    private const double MaxDisplayHeight = 620;

    /// <summary>キーで動かす量（画像を出している領域の幅に対する割合）。Shift で細かく動かす。</summary>
    private const double KeyStep = 0.02;
    private const double FineKeyStep = 0.005;

    private readonly BitmapSource _image;
    private readonly CoverCropEditor _editor;
    private readonly double _scale;
    private CropHandle? _resizeHandle;
    private bool _moving;
    private Point _startPoint;
    private (double X, double Y, double Width, double Height) _startRect;

    public CoverCropDialog(BitmapSource image, string entryName)
    {
        ArgumentNullException.ThrowIfNull(image);
        InitializeComponent();
        _image = image;
        EntryText.Text = entryName;
        Title = "表紙を切り抜く";

        _scale = Math.Min(1.0, Math.Min(MaxDisplayWidth / image.PixelWidth, MaxDisplayHeight / image.PixelHeight));
        var displayWidth = image.PixelWidth * _scale;
        var displayHeight = image.PixelHeight * _scale;
        Surface.Width = displayWidth;
        Surface.Height = displayHeight;
        Picture.Source = image;
        Picture.Width = displayWidth;
        Picture.Height = displayHeight;

        _editor = new CoverCropEditor(image.PixelWidth, image.PixelHeight);
        UpdateVisuals();
    }


    /// <summary>選んだ範囲。キャンセルされた場合は null。</summary>
    public CoverCrop? Crop { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Crop = _editor.ToCrop();
        DialogResult = true;
    }

    private void OnLockAspectChanged(object sender, RoutedEventArgs e)
    {
        _editor.LockAspect = LockAspectBox.IsChecked == true;
        UpdateVisuals();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _editor.Reset();
        UpdateVisuals();
        _ = Surface.Focus();
    }

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = Surface.Focus();
        _resizeHandle = e.OriginalSource is Rectangle { Tag: string handle } ? Enum.Parse<CropHandle>(handle) : null;
        _moving = _resizeHandle is null && ReferenceEquals(e.OriginalSource, Frame);
        if (_resizeHandle is null && !_moving)
        {
            return;
        }

        _startPoint = e.GetPosition(Surface);
        _startRect = _editor.Rect;
        _ = Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeHandle is null && !_moving)
        {
            return;
        }

        var point = e.GetPosition(Surface);
        var dx = (point.X - _startPoint.X) / _scale;
        var dy = (point.Y - _startPoint.Y) / _scale;
        _editor.Restore(_startRect);
        if (_resizeHandle is { } handle)
        {
            _editor.Resize(handle, dx, dy);
        }
        else
        {
            _editor.Move(dx, dy);
        }

        UpdateVisuals();
    }

    private void OnSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeHandle is not null || _moving)
        {
            Surface.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnSurfaceLostMouseCapture(object sender, MouseEventArgs e)
    {
        _resizeHandle = null;
        _moving = false;
    }

    // 矢印キーで動かし、+ / − で大きさを変える（Shift で細かく）
    private void OnSurfaceKeyDown(object sender, KeyEventArgs e)
    {
        var step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FineKeyStep : KeyStep) * _editor.ImageWidth;
        switch (e.Key)
        {
            case Key.Left:
                _editor.Move(-step, 0);
                break;
            case Key.Right:
                _editor.Move(step, 0);
                break;
            case Key.Up:
                _editor.Move(0, -step);
                break;
            case Key.Down:
                _editor.Move(0, step);
                break;
            case Key.OemPlus or Key.Add:
                _editor.Scale(1.05);
                break;
            case Key.OemMinus or Key.Subtract:
                _editor.Scale(1 / 1.05);
                break;
            default:
                return;
        }

        UpdateVisuals();
        e.Handled = true;
    }

    private void UpdateVisuals()
    {
        var x = _editor.X * _scale;
        var y = _editor.Y * _scale;
        var width = _editor.Width * _scale;
        var height = _editor.Height * _scale;

        Canvas.SetLeft(Frame, x);
        Canvas.SetTop(Frame, y);
        Frame.Width = width;
        Frame.Height = height;

        PlaceHandle(TopLeftHandle, x, y);
        PlaceHandle(TopRightHandle, x + width, y);
        PlaceHandle(BottomLeftHandle, x, y + height);
        PlaceHandle(BottomRightHandle, x + width, y + height);
        PlaceHandle(TopHandle, x + (width / 2), y);
        PlaceHandle(BottomHandle, x + (width / 2), y + height);
        PlaceHandle(LeftHandle, x, y + (height / 2));
        PlaceHandle(RightHandle, x + width, y + (height / 2));

        Mask.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Surface.Width, Surface.Height)),
            new RectangleGeometry(new Rect(x, y, width, height)));

        var (px, py, pw, ph) = _editor.ToCrop().ToPixels(_image.PixelWidth, _image.PixelHeight);
        Preview.Source = new CroppedBitmap(_image, new Int32Rect(px, py, pw, ph));
    }

    private static void PlaceHandle(FrameworkElement handle, double centerX, double centerY)
    {
        Canvas.SetLeft(handle, centerX - (handle.Width / 2));
        Canvas.SetTop(handle, centerY - (handle.Height / 2));
    }
}
