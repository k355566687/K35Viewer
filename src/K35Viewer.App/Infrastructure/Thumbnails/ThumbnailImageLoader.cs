using System.Windows.Media;

namespace K35Viewer.Infrastructure.Thumbnails;

/// <summary>
/// ライブラリの一覧に表示するサムネイル画像（キャッシュのファイル）の遅延読み込みとメモリ上のキャッシュ（LRU）。
/// 1万件分の画像を保持し続けるとメモリを使い切るため、保持する枚数に上限を設ける
/// （スクロールで見えなくなった画像は破棄し、戻ってきたらキャッシュのファイルから読み直す）。
/// 実際の処理は <see cref="LazyImageLoader{TKey}"/>。キーはキャッシュルートからの相対パス。
/// </summary>
public sealed class ThumbnailImageLoader : IDisposable
{
    public const int DefaultCapacity = 400;
    private const int WorkerCount = 4;

    private readonly LazyImageLoader<string> _loader;

    /// <param name="store">サムネイルファイルの保存先。</param>
    /// <param name="capacity">メモリ上に保持する画像の最大枚数。</param>
    /// <param name="context">読み込み完了の通知を送る先（UIスレッドのコンテキスト）。省略時は現在のコンテキスト。</param>
    public ThumbnailImageLoader(ThumbnailCacheStore store, int capacity = DefaultCapacity, SynchronizationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _loader = new LazyImageLoader<string>(store.Read, capacity, WorkerCount, StringComparer.OrdinalIgnoreCase, context);
    }

    /// <summary>メモリ上に保持している画像の枚数。</summary>
    public int CachedCount => _loader.CachedCount;

    /// <summary>
    /// 画像を返す。メモリ上にあればすぐ返す。なければ null を返して背景で読み込みを始め、
    /// 読み込めたら onLoaded を（UIスレッドで）呼ぶ。呼ばれたら、再度 TryGet して画像を取得する。
    /// 読み込めなかったファイルは、Invalidate されるまで再試行しない。
    /// </summary>
    public ImageSource? TryGet(string relativePath, Action onLoaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return _loader.TryGet(relativePath, onLoaded);
    }

    /// <summary>
    /// 指定したファイルのメモリ上の画像と失敗記録を破棄する。サムネイルが再生成されて
    /// 同じパスの中身が変わったときに呼ぶ。
    /// </summary>
    public void Invalidate(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        _loader.Invalidate(relativePath);
    }

    public void Dispose() => _loader.Dispose();
}
