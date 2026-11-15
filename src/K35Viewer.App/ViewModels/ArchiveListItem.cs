using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;

namespace K35Viewer.ViewModels;

/// <summary>ファイルサイズの表示用書式(1024単位)。</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} B");
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:N1} {Units[unit]}");
    }
}

/// <summary>
/// 一覧の1行(詳細表示の1行、サムネイル表示の1枚)。
/// 表示用の文字列は、画面に表示される行だけがバインド時に組み立てる(1万件を一括で整形しない)。
/// サムネイル画像は、行が画面に出てバインドされたときに初めて読み込みを始める(遅延読み込み)。
/// 画像そのものは保持せず、ローダーのLRUキャッシュに任せる(1万件分を保持するとメモリを使い切るため)。
/// </summary>
public sealed class ArchiveListItem(ArchiveSummary summary, ThumbnailImageLoader? loader = null) : ObservableObject
{
    private ArchiveSummary _summary = summary;
    private bool _awaitingThumbnail;
    private bool _isSelected;

    public long Id => _summary.Id;

    public string FileName => _summary.FileName;

    /// <summary>アーカイブのフルパス（保存場所とファイル名）。</summary>
    public string FullPath => _summary.FullPath;

    /// <summary>選択されているか。サムネイル表示で、選択を示す枠と背景に使う。詳細表示の選択は、一覧の選択と同期する。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>スクリーンリーダー等に伝える状態。色だけに頼らない。</summary>
    public string StatusText => _isSelected ? "選択中" : string.Empty;

    /// <summary>保存場所(アーカイブを格納しているフォルダ)のフルパス。操作（移動先の初期フォルダなど）に使う。</summary>
    public string Location => _summary.FolderPath;

    /// <summary>
    /// 一覧に表示する保存場所。管理フォルダの名前から始まる相対パス（例: 「Archive\漫画\2025」）。
    /// 全行で同じになりやすい管理フォルダまでのパスを省き、違いが見える部分を残す。フルパスはツールチップで示す。
    /// </summary>
    public string LocationText
    {
        get
        {
            var root = _summary.LibraryRootPath;
            if (string.IsNullOrEmpty(root))
            {
                return _summary.FolderPath;
            }

            var rootName = PathNormalizer.GetDisplayName(root);
            var relative = Path.GetRelativePath(root, _summary.FolderPath);
            if (relative == ".")
            {
                return rootName;
            }

            // 管理フォルダの外（通常は起きない）は、フルパスのまま
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? _summary.FolderPath
                : rootName + Path.DirectorySeparatorChar + relative;
        }
    }

    public string? ThumbnailPath => _summary.ThumbnailPath;

    /// <summary>
    /// 画像枚数。未解析は「－」、解析できなかったものは状態を表す語(UC-02: 壊れたZIPをライブラリ上で識別できるようにする)。
    /// </summary>
    public string ImageCountText => _summary.AnalysisStatus switch
    {
        "BROKEN" => "破損",
        "UNSUPPORTED" => "非対応",
        "ERROR" => "読込失敗",
        _ => _summary.ImageCount is { } count ? count.ToString("N0", CultureInfo.CurrentCulture) : "－",
    };

    /// <summary>
    /// 開けないことが、一覧の状態から分かっているか（壊れている、画像がない、暗号化されている）。
    /// 前後のアーカイブへ移るとき、これらは飛ばす。ファイルがないなどは、開いてみないと分からない。
    /// </summary>
    public bool IsKnownUnopenable => _summary.AnalysisStatus is "BROKEN" or "EMPTY" or "UNSUPPORTED";

    public string FileSizeText => ByteSizeFormatter.Format(_summary.FileSize);

    public string LastWriteTimeText => UnixTime.FromUnixSeconds(_summary.LastWriteTime)
        .ToLocalTime()
        .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);

    /// <summary>サムネイル画像がない間に、枠の中へ表示する状態の説明。画像があるときは空。</summary>
    public string PlaceholderText => _summary.ThumbnailPath is not null
        ? string.Empty
        : _summary.AnalysisStatus switch
        {
            "EMPTY" => "画像なし",
            "BROKEN" => "破損した\nアーカイブ",
            "UNSUPPORTED" => "暗号化\n(非対応)",
            "ERROR" => "読み込み\n失敗",
            "READY" when string.Equals(_summary.ThumbnailStatus, "FAILED", StringComparison.Ordinal) => "表紙を\n読めません",
            _ => "作成待ち",
        };

    /// <summary>
    /// サムネイル画像。メモリ上になければ null を返して背景で読み込みを始め、読み込めたら変更を通知する。
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            var path = _summary.ThumbnailPath;
            if (path is null || loader is null || _awaitingThumbnail)
            {
                return null;
            }

            // 完了通知が TryGet の戻りより先に届いても取りこぼさないよう、先に「読み込み待ち」にしておく
            _awaitingThumbnail = true;
            var image = loader.TryGet(path, OnThumbnailLoaded);
            if (image is not null)
            {
                _awaitingThumbnail = false;
            }

            return image;
        }
    }

    /// <summary>解析・サムネイル生成の結果を、この行へ反映する(一覧を取得し直さずに表示を更新する)。</summary>
    public void Apply(ArchiveProcessed update)
    {
        ArgumentNullException.ThrowIfNull(update);

        _summary = _summary with
        {
            AnalysisStatus = update.AnalysisStatus,
            ImageCount = update.ImageCount,
            ThumbnailPath = update.ThumbnailPath,
            ThumbnailStatus = update.ThumbnailStatus,
        };

        // 再生成では同じパスの中身が変わるため、メモリ上の古い画像を捨てて読み直す
        if (update.ThumbnailPath is { } path)
        {
            loader?.Invalidate(path);
        }

        _awaitingThumbnail = false;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(ThumbnailPath));
        OnPropertyChanged(nameof(PlaceholderText));
        OnPropertyChanged(nameof(ImageCountText));
    }

    private void OnThumbnailLoaded()
    {
        _awaitingThumbnail = false;
        OnPropertyChanged(nameof(Thumbnail));
    }
}

/// <summary>サムネイル表示の1行分(横に並ぶ数枚)。行単位で仮想化することで、1万件でも画面に見える行だけを生成する。</summary>
public sealed record ThumbnailRow(IReadOnlyList<ArchiveListItem> Items);
