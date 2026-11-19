using K35Viewer.Domain;

namespace K35Viewer.ViewModels;

/// <summary>切り抜きの枠の、大きさを変えるときにつかむ所（四隅と、辺の真ん中）。</summary>
public enum CropHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// 表紙の切り抜きの枠（GitHub の Issue #15）。座標は、画面に出している画像の画素（画像の左上が原点）。
/// 枠の縦横比は自由（<see cref="LockAspect"/> が true なら、ライブラリのカードと同じ <see cref="AspectRatio"/>）。
/// 枠は画像の外には出ない。縦横比がカードと違う範囲は、サムネイルもその形になり、カードの余った所には背景が見える。
/// 画面の操作（ドラッグ、キー）から呼ぶ。描画はしない（画面に依存しないため、テストできる）。
/// </summary>
public sealed class CoverCropEditor
{
    /// <summary>ライブラリのサムネイルのカード（164 × 220）の縦横比（幅 / 高さ）。</summary>
    public const double AspectRatio = 164.0 / 220.0;

    /// <summary>枠の幅・高さの下限の、画像の幅・高さに対する割合（小さすぎて、つかめなくならないように）。</summary>
    public const double MinSizeRatio = 0.05;

    private bool _lockAspect;

    public CoverCropEditor(double imageWidth, double imageHeight, CoverCrop? initial = null, bool lockAspect = false)
    {
        if (!double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth < 1 || imageHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "画像の大きさが正しくありません");
        }

        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        _lockAspect = lockAspect;
        Reset();
        if (initial is { IsValid: true })
        {
            Place(initial.X * imageWidth, initial.Y * imageHeight, initial.Width * imageWidth, initial.Height * imageHeight);
            if (lockAspect)
            {
                ConformToAspect();
            }
        }
    }

    public double ImageWidth { get; }

    public double ImageHeight { get; }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    /// <summary>
    /// true なら、枠をカードと同じ縦横比に保つ。true にしたときは、今の枠の中心と幅を保って（入らなければ縮めて）、縦横比を合わせる。
    /// </summary>
    public bool LockAspect
    {
        get => _lockAspect;
        set
        {
            if (_lockAspect == value)
            {
                return;
            }

            _lockAspect = value;
            if (value)
            {
                ConformToAspect();
            }
        }
    }

    /// <summary>枠の幅の下限。</summary>
    public double MinWidth => Math.Max(1, ImageWidth * MinSizeRatio);

    /// <summary>枠の高さの下限。</summary>
    public double MinHeight => Math.Max(1, ImageHeight * MinSizeRatio);

    /// <summary>
    /// 枠を、はじめの大きさにする。縦横比が自由なら、画像の全体。カードの縦横比なら、画像に収まる、いちばん大きな枠を、真ん中に置く。
    /// </summary>
    public void Reset()
    {
        if (!_lockAspect)
        {
            Place(0, 0, ImageWidth, ImageHeight);
            return;
        }

        var width = Math.Min(ImageWidth, ImageHeight * AspectRatio);
        var height = width / AspectRatio;
        Place((ImageWidth - width) / 2, (ImageHeight - height) / 2, width, height);
    }

    /// <summary>今の枠（ドラッグを始めたときの枠を覚えて、<see cref="Restore"/> で戻すために使う）。</summary>
    public (double X, double Y, double Width, double Height) Rect => (X, Y, Width, Height);

    /// <summary>
    /// 枠を、覚えておいた枠に戻す。ドラッグの間は、始めたときの枠に戻してから、始めからの移動量を当てる
    /// （端で止まったあと、マウスを戻したときに、枠がずれないように）。
    /// </summary>
    public void Restore((double X, double Y, double Width, double Height) rect) => Place(rect.X, rect.Y, rect.Width, rect.Height);
    /// <summary>枠を動かす（画像の外へは出ない）。</summary>
    public void Move(double dx, double dy)
    {
        if (double.IsFinite(dx) && double.IsFinite(dy))
        {
            Place(X + dx, Y + dy, Width, Height);
        }
    }

    /// <summary>
    /// つかんだ所を動かして、大きさを変える。反対の辺（角なら、反対の角）は動かない。画像の外へは広げず、下限より小さくはしない。
    /// カードの縦横比のときは、比を保つ（角では、横と縦の動きのうち、大きくなる方に合わせる。辺では、反対の向きの大きさを、中心を保って合わせる）。
    /// </summary>
    public void Resize(CropHandle handle, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            return;
        }

        if (_lockAspect)
        {
            ResizeLocked(handle, dx, dy);
            return;
        }

        var left = X;
        var top = Y;
        var right = X + Width;
        var bottom = Y + Height;
        if (handle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft)
        {
            left = Math.Clamp(left + dx, 0, right - MinWidth);
        }

        if (handle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight)
        {
            right = Math.Clamp(right + dx, left + MinWidth, ImageWidth);
        }

        if (handle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight)
        {
            top = Math.Clamp(top + dy, 0, bottom - MinHeight);
        }

        if (handle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight)
        {
            bottom = Math.Clamp(bottom + dy, top + MinHeight, ImageHeight);
        }

        Place(left, top, right - left, bottom - top);
    }

    /// <summary>中心を保って、縦横比を変えずに、大きさを変える（factor が 1 より大きければ広げる）。</summary>
    public void Scale(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        // 画像に収まり、下限を下回らない倍率に収める（縦横比を崩さないため、幅と高さを同じ倍率にする）
        var max = Math.Min(ImageWidth / Width, ImageHeight / Height);
        var min = Math.Max(MinWidth / Width, MinHeight / Height);
        factor = Math.Clamp(factor, Math.Min(min, max), max);
        var centerX = X + (Width / 2);
        var centerY = Y + (Height / 2);
        var width = Width * factor;
        var height = Height * factor;
        Place(centerX - (width / 2), centerY - (height / 2), width, height);
    }

    /// <summary>画像に対する割合での範囲（DB に保存する形）。</summary>
    public CoverCrop ToCrop()
    {
        var x = Math.Clamp(X / ImageWidth, 0, 1);
        var y = Math.Clamp(Y / ImageHeight, 0, 1);
        var width = Math.Clamp(Width / ImageWidth, double.Epsilon, 1 - x);
        var height = Math.Clamp(Height / ImageHeight, double.Epsilon, 1 - y);
        return new CoverCrop(x, y, width, height);
    }

    private void ResizeLocked(CropHandle handle, double dx, double dy)
    {
        var minWidth = Math.Min(Math.Max(MinWidth, MinHeight * AspectRatio), Math.Min(ImageWidth, ImageHeight * AspectRatio));
        switch (handle)
        {
            case CropHandle.Left or CropHandle.Right:
            {
                // 横を変え、高さは、中心を保って合わせる
                var growLeft = handle == CropHandle.Left;
                var anchorX = growLeft ? X + Width : X;
                var room = growLeft ? anchorX : ImageWidth - anchorX;
                var width = Math.Clamp(Width + (growLeft ? -dx : dx), minWidth, Math.Max(minWidth, Math.Min(room, ImageHeight * AspectRatio)));
                var height = width / AspectRatio;
                Place(growLeft ? anchorX - width : anchorX, Y + (Height / 2) - (height / 2), width, height);
                return;
            }

            case CropHandle.Top or CropHandle.Bottom:
            {
                // 縦を変え、幅は、中心を保って合わせる
                var growTop = handle == CropHandle.Top;
                var anchorY = growTop ? Y + Height : Y;
                var room = (growTop ? anchorY : ImageHeight - anchorY) * AspectRatio;
                var width = Math.Clamp((Height + (growTop ? -dy : dy)) * AspectRatio, minWidth, Math.Max(minWidth, Math.Min(room, ImageWidth)));
                var height = width / AspectRatio;
                Place(X + (Width / 2) - (width / 2), growTop ? anchorY - height : anchorY, width, height);
                return;
            }

            default:
            {
                var left = handle is CropHandle.TopLeft or CropHandle.BottomLeft;
                var top = handle is CropHandle.TopLeft or CropHandle.TopRight;
                var anchorX = left ? X + Width : X;
                var anchorY = top ? Y + Height : Y;
                var byWidth = Width + (left ? -dx : dx);
                var byHeight = (Height + (top ? -dy : dy)) * AspectRatio;
                var roomX = left ? anchorX : ImageWidth - anchorX;
                var roomY = (top ? anchorY : ImageHeight - anchorY) * AspectRatio;
                var width = Math.Clamp(Math.Max(byWidth, byHeight), minWidth, Math.Max(minWidth, Math.Min(roomX, roomY)));
                var height = width / AspectRatio;
                Place(left ? anchorX - width : anchorX, top ? anchorY - height : anchorY, width, height);
                return;
            }
        }
    }

    // 中心と幅を保って、カードの縦横比にする（画像に入らなければ、縮める）
    private void ConformToAspect()
    {
        var centerX = X + (Width / 2);
        var centerY = Y + (Height / 2);
        var width = Math.Min(Width, Math.Min(ImageWidth, ImageHeight * AspectRatio));
        var height = width / AspectRatio;
        Place(centerX - (width / 2), centerY - (height / 2), width, height);
    }

    // 大きさを範囲に収めてから、枠が画像の中に入るよう、位置を寄せる
    private void Place(double x, double y, double width, double height)
    {
        Width = Math.Clamp(width, Math.Min(MinWidth, ImageWidth), ImageWidth);
        Height = Math.Clamp(height, Math.Min(MinHeight, ImageHeight), ImageHeight);
        X = Math.Clamp(x, 0, ImageWidth - Width);
        Y = Math.Clamp(y, 0, ImageHeight - Height);
    }
}
