using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using K35Viewer.Services;

namespace K35Viewer.ViewModels;

/// <summary>
/// ビューアの一覧表示の1枚（アーカイブ内の画像1枚）。サムネイルは、行が画面に出てバインドされたときに
/// 初めて生成を始める（遅延生成）。画像そのものは保持せず、<see cref="PageThumbnailSource"/> のLRUキャッシュに任せる。
/// </summary>
public sealed partial class PageItem(int index, string entryName, PageThumbnailSource? thumbnails) : ObservableObject
{
    private bool _awaitingThumbnail;
    private bool _failed;

    /// <summary>アーカイブ内での位置（0始まり、自然順）。</summary>
    public int Index { get; } = index;

    /// <summary>アーカイブ内のパス。</summary>
    public string EntryName { get; } = entryName;

    /// <summary>フォルダを除いたファイル名。</summary>
    public string DisplayName { get; } = entryName[(entryName.LastIndexOf('/') + 1)..];

    /// <summary>「42」の形式の頁番号（1始まり）。</summary>
    public string PageNumberText => (Index + 1).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>選択されているか（複数選択できる。削除予約などの操作の対象になる）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isSelected;

    /// <summary>現在位置（1枚表示で表示する画像）か。</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>スクリーンリーダー等に伝える状態。色だけに頼らない。</summary>
    public string StatusText => IsSelected ? "選択中" : string.Empty;

    /// <summary>サムネイルがない間に、枠の中へ表示する説明。</summary>
    public string PlaceholderText => _failed ? "読み込めません" : "読み込み中…";

    /// <summary>サムネイル画像。メモリ上になければ null を返して背景で生成を始め、できたら変更を通知する。</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (thumbnails is null || _awaitingThumbnail || _failed)
            {
                return null;
            }

            // 完了通知が TryGet の戻りより先に届いても取りこぼさないよう、先に「待ち」にしておく
            _awaitingThumbnail = true;
            var image = thumbnails.TryGet(Index, OnLoaded, OnFailed);
            if (image is not null)
            {
                _awaitingThumbnail = false;
            }

            return image;
        }
    }

    private void OnLoaded()
    {
        _awaitingThumbnail = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    private void OnFailed()
    {
        _awaitingThumbnail = false;
        _failed = true;
        OnPropertyChanged(nameof(PlaceholderText));
    }
}

/// <summary>一覧表示の1行分（横に並ぶ数枚）。行単位で仮想化することで、頁数が多くても画面に見える行だけを生成する。</summary>
public sealed record PageRow(IReadOnlyList<PageItem> Items);
