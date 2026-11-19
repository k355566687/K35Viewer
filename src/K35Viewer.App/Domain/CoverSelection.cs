namespace K35Viewer.Domain;

/// <summary>
/// 表紙の切り抜きの範囲（GitHub の Issue #15）。元の画像の幅・高さに対する割合（0〜1）で持つ
/// （画像の大きさによらず、同じ範囲を表せる）。左上が (X, Y)。
/// </summary>
public sealed record CoverCrop(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// 範囲として正しいか。値が有限で、幅と高さが 0 より大きく、画像の中に収まっている
    /// （計算の誤差のため、右端・下端は、わずかにはみ出すことを許す）。
    /// </summary>
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && X >= 0 && Y >= 0 && X < 1 && Y < 1
        && Width > 0 && Height > 0 && Width <= 1 && Height <= 1
        && X + Width <= 1 + Tolerance && Y + Height <= 1 + Tolerance;

    private const double Tolerance = 1e-6;

    /// <summary>
    /// 画像の画素での範囲（左上と大きさ）。画像の外に出ないよう、また 1 画素以上になるように丸める。
    /// </summary>
    public (int X, int Y, int Width, int Height) ToPixels(int imageWidth, int imageHeight)
    {
        var x = Math.Clamp((int)Math.Round(X * imageWidth), 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Round(Y * imageHeight), 0, imageHeight - 1);
        var width = Math.Clamp((int)Math.Round(Width * imageWidth), 1, imageWidth - x);
        var height = Math.Clamp((int)Math.Round(Height * imageHeight), 1, imageHeight - y);
        return (x, y, width, height);
    }
}

/// <summary>
/// 利用者が選んだ表紙（GitHub の Issue #15）。Entry はアーカイブの中の画像の名前（閲覧位置と同じ形）。
/// Crop が null なら、画像の全体を使う。
/// </summary>
public sealed record CoverSelection(string Entry, CoverCrop? Crop);
