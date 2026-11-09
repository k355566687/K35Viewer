using System.Collections.Concurrent;
using System.Windows.Media;
using K35Viewer.Infrastructure.Thumbnails;

namespace K35Viewer.Services;

/// <summary>
/// ビューアの一覧表示に出す、アーカイブ内の各画像のサムネイル（UC-07）。
/// 画面に出た画像だけを、背景で生成する（遅延生成）。生成したサムネイル（JPEG）は、
/// アーカイブを閉じるまでメモリに保持し、同じ画像を再び読み込まない。
/// ライブラリ用の表紙サムネイルと違い、ファイルには保存しない。
/// </summary>
public sealed class PageThumbnailSource : IDisposable
{
    /// <summary>メモリ上に保持する、デコード済みの画像の最大枚数。</summary>
    public const int DefaultCapacity = 300;

    private const int WorkerCount = 3;

    private readonly ViewerSession _session;
    private readonly ThumbnailGenerator _generator;

    // 生成済みのサムネイル（JPEG、1枚あたり十数KB）。デコード済みの画像より桁違いに小さいため、
    // 全頁分を保持しても問題ない。画面から外れて戻った画像を、ZIPから読み直さずに済む。
    private readonly ConcurrentDictionary<int, byte[]> _generated = new();
    private readonly LazyImageLoader<int> _loader;

    public PageThumbnailSource(
        ViewerSession session,
        ThumbnailGenerator generator,
        int capacity = DefaultCapacity,
        SynchronizationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(generator);

        _session = session;
        _generator = generator;
        _loader = new LazyImageLoader<int>(Produce, capacity, WorkerCount, comparer: null, context);
    }

    /// <summary>生成済み（バイト列を保持している）サムネイルの数。</summary>
    public int GeneratedCount => _generated.Count;

    /// <summary>メモリ上に保持している、デコード済みの画像の枚数。</summary>
    public int CachedCount => _loader.CachedCount;

    /// <summary>
    /// サムネイル画像を返す。メモリ上になければ null を返して背景で生成を始め、
    /// 生成できたら onLoaded を、できなければ onFailed を（UIスレッドで）呼ぶ。
    /// </summary>
    public ImageSource? TryGet(int index, Action onLoaded, Action? onFailed = null) =>
        _loader.TryGet(index, onLoaded, onFailed);

    /// <summary>画像を読み込めないと分かっているか（壊れた画像など）。</summary>
    public bool IsFailed(int index) => _loader.IsFailed(index);

    public void Dispose() => _loader.Dispose();

    // 背景スレッドから呼ばれる。ZIPの読み出しは ViewerSession 側で直列化されている
    private byte[] Produce(int index)
    {
        if (_generated.TryGetValue(index, out var existing))
        {
            return existing;
        }

        var thumbnail = _generator.Generate(_session.ReadImage(index));
        _generated[index] = thumbnail.Data;
        return thumbnail.Data;
    }
}
