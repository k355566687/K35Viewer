using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K35Viewer.Infrastructure.Thumbnails;

/// <summary>
/// 一覧に表示する画像の遅延読み込みと、メモリ上のキャッシュ（LRU）。
/// 画像のバイト列の取得方法は呼び出し側が渡す（サムネイルのファイル、アーカイブ内の画像から生成、など）。
/// 大量の画像を保持し続けるとメモリを使い切るため、保持する枚数に上限を設ける
/// （画面から外れた画像は破棄し、戻ってきたら読み直す）。
/// 読み込みは背景スレッドで行い、画面に出た行の要求を優先する（新しい要求を先に処理する）。
/// </summary>
/// <typeparam name="TKey">画像を識別するキー（ファイルのパス、頁の番号など）。</typeparam>
public sealed class LazyImageLoader<TKey> : IDisposable
    where TKey : notnull
{
    private readonly Func<TKey, byte[]> _readBytes;
    private readonly int _capacity;
    private readonly int _workerCount;
    private readonly SynchronizationContext? _context;

    private readonly object _lock = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, ImageSource Image)>> _cache;
    private readonly LinkedList<(TKey Key, ImageSource Image)> _lru = new();
    private readonly Dictionary<TKey, List<(Action Loaded, Action? Failed)>> _waiting;
    private readonly List<TKey> _pending = []; // 末尾が最新の要求
    private readonly HashSet<TKey> _failed;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private bool _workersStarted;

    /// <param name="readBytes">キーに対応する画像のバイト列を返す。読み込めない場合は例外を投げる（その画像は「失敗」になる）。背景スレッドから呼ばれる。</param>
    /// <param name="capacity">メモリ上に保持する画像の最大枚数。</param>
    /// <param name="workerCount">同時に読み込むスレッド数。</param>
    /// <param name="comparer">キーの比較方法。</param>
    /// <param name="context">読み込み完了の通知を送る先（UIスレッドのコンテキスト）。省略時は現在のコンテキスト。</param>
    public LazyImageLoader(
        Func<TKey, byte[]> readBytes,
        int capacity,
        int workerCount,
        IEqualityComparer<TKey>? comparer = null,
        SynchronizationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(readBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);

        _readBytes = readBytes;
        _capacity = capacity;
        _workerCount = workerCount;
        _context = context ?? SynchronizationContext.Current;
        _cache = new Dictionary<TKey, LinkedListNode<(TKey, ImageSource)>>(comparer);
        _waiting = new Dictionary<TKey, List<(Action, Action?)>>(comparer);
        _failed = new HashSet<TKey>(comparer);
    }

    /// <summary>メモリ上に保持している画像の枚数。</summary>
    public int CachedCount
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>読み込めなかった画像か（Invalidate されるまで再試行しない）。</summary>
    public bool IsFailed(TKey key)
    {
        lock (_lock)
        {
            return _failed.Contains(key);
        }
    }

    /// <summary>
    /// 画像を返す。メモリ上にあればすぐ返す。なければ null を返して背景で読み込みを始め、
    /// 読み込めたら onLoaded を（UIスレッドで）呼ぶ。呼ばれたら、再度 TryGet して画像を取得する。
    /// 読み込めなかった場合は、onFailed があればそれを呼ぶ。読み込めなかった画像は、Invalidate されるまで再試行しない。
    /// </summary>
    public ImageSource? TryGet(TKey key, Action onLoaded, Action? onFailed = null)
    {
        ArgumentNullException.ThrowIfNull(onLoaded);

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }

            if (_failed.Contains(key))
            {
                return null;
            }

            if (_waiting.TryGetValue(key, out var callbacks))
            {
                callbacks.Add((onLoaded, onFailed));
                // 再び要求されたものは、画面に出ている可能性が高いので優先する
                var index = _pending.FindLastIndex(k => _cache.Comparer.Equals(k, key));
                if (index >= 0)
                {
                    _pending.RemoveAt(index);
                    _pending.Add(key);
                }

                return null;
            }

            _waiting[key] = [(onLoaded, onFailed)];
            _pending.Add(key);
            StartWorkers();
        }

        _signal.Release();
        return null;
    }

    /// <summary>
    /// 指定した画像のメモリ上のデータと失敗記録を破棄する。中身が変わった（再生成された）ときに呼ぶ。
    /// </summary>
    public void Invalidate(TKey key)
    {
        lock (_lock)
        {
            if (_cache.Remove(key, out var node))
            {
                _lru.Remove(node);
            }

            _failed.Remove(key);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _signal.Dispose();
    }

    private void StartWorkers()
    {
        if (_workersStarted)
        {
            return;
        }

        _workersStarted = true;
        for (var i = 0; i < _workerCount; i++)
        {
            _ = Task.Run(() => WorkAsync(_cts.Token));
        }
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct);

                TKey key;
                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        continue;
                    }

                    key = _pending[^1];
                    _pending.RemoveAt(_pending.Count - 1);
                }

                var image = TryLoad(key);

                List<(Action Loaded, Action? Failed)>? callbacks;
                lock (_lock)
                {
                    _waiting.Remove(key, out callbacks);
                    if (image is not null)
                    {
                        Add(key, image);
                    }
                    else
                    {
                        _failed.Add(key);
                    }
                }

                if (callbacks is not null)
                {
                    foreach (var (loaded, failed) in callbacks)
                    {
                        if (image is not null)
                        {
                            Notify(loaded);
                        }
                        else if (failed is not null)
                        {
                            Notify(failed);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 終了時
        }
    }

    // 呼び出し元がlockを保持していること
    private void Add(TKey key, ImageSource image)
    {
        if (_cache.Remove(key, out var existing))
        {
            _lru.Remove(existing);
        }

        _cache[key] = _lru.AddFirst((key, image));
        while (_cache.Count > _capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
        }
    }

    private BitmapImage? TryLoad(TKey key)
    {
        try
        {
            using var stream = new MemoryStream(_readBytes(key));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze(); // 凍結すると、背景スレッドで作った画像をUIスレッドで使える
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or ArgumentException or InvalidDataException or ObjectDisposedException
            or ThumbnailGenerationException)
        {
            return null;
        }
    }

    private void Notify(Action callback)
    {
        if (_context is null)
        {
            callback();
        }
        else
        {
            _context.Post(static state => ((Action)state!)(), callback);
        }
    }
}
