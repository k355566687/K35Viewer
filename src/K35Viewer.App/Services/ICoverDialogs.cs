using K35Viewer.Domain;

namespace K35Viewer.Services;

/// <summary>表紙の切り抜きの画面（GitHub の Issue #15）。ViewModel から WPF のダイアログを直接呼ばないための境界。</summary>
public interface ICoverDialogs
{
    /// <summary>
    /// 画像を出して、表紙にする範囲を選んでもらう。キャンセルされた場合は null。
    /// 画像を読み込めない場合は <see cref="InvalidDataException"/>。
    /// </summary>
    CoverCrop? PickCrop(byte[] imageData, string entryName);
}
