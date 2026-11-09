using System.Windows.Media.Imaging;
using K35Viewer.Infrastructure.Viewer;

namespace K35Viewer.Services;

/// <summary>1ページぶんの読み込み結果。読み込めなかった場合は Image が null で、Error に理由が入る。</summary>
public sealed record LoadedPage(int Index, BitmapSource? Image, string? Error);

/// <summary>
/// ビューアの頁画像の読み込みと、現在の頁の前後だけの先読み（UC-06: 全画像を同時に展開しない）。
/// 保持するのは現在の頁を中心にした前後数頁だけで、それより遠い頁は破棄する（戻ったら読み直す）。
/// 破棄した頁の読み込みが途中であれば中止する（先頭から末尾へ飛ぶ操作などで、無駄な展開を続けないため）。
/// 同じ頁を同時に何度要求されても、読み込みは1回だけ行う。
/// </summary>
public sealed class PageImageLoader(ViewerSession session, ViewerImageDecoder decoder) : IDisposable
{
    /// <summary>現在の頁より前に保持する頁数（戻る操作のため）。</summary>
    public const int Behind = 1;

    /// <summary>現在の頁より後に先読みする頁数（読み進める操作のため）。</summary>
    public const int Ahead = 2;

    private readonly object _lock = new();
    private readonly Dictionary<int, PageEntry> _pages = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>保持している頁数。</summary>
    public int CachedCount
    {
        get
        {
            lock (_lock)
            {
                return _pages.Count;
            }
        }
    }

    /// <summary>
    /// 指定した頁の読み込み結果を返す。読み込み中・読み込み済みのものがあれば、それを使う。
    /// 返した頁が、その後の <see cref="Prefetch"/> で範囲外になって破棄された場合、読み込み中だったものは
    /// キャンセルされる（待っている側は <see cref="OperationCanceledException"/> を受け取る）。
    /// </summary>
    public Task<LoadedPage> GetAsync(int index)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetOrStart(index).Task;
        }
    }

    /// <summary>
    /// 現在の頁（center）の前後の頁を先読みし、範囲外の頁を破棄する。
    /// 先読みは待たずに背景で進む。
    /// </summary>
    public void Prefetch(int center)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var first = Math.Max(0, center - Behind);
            var last = Math.Min(session.Count - 1, center + Ahead);

            foreach (var index in _pages.Keys.Where(i => i < first || i > last).ToList())
            {
                _pages.Remove(index, out var evicted);
                evicted?.Cancel();
            }

            for (var index = first; index <= last; index++)
            {
                _ = GetOrStart(index);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pages.Clear();
        }

        _cts.Cancel();
        _cts.Dispose();
    }

    // 呼び出し元がlockを保持していること
    private PageEntry GetOrStart(int index)
    {
        if (!_pages.TryGetValue(index, out var entry))
        {
            // 全体のキャンセル（Dispose）と、この頁だけのキャンセル（破棄）の両方で止められるようにする
            var pageCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            entry = new PageEntry(LoadAsync(index, pageCts), pageCts);
            _pages[index] = entry;
        }

        return entry;
    }

    private async Task<LoadedPage> LoadAsync(int index, CancellationTokenSource pageCts)
    {
        try
        {
            var ct = pageCts.Token;
            var data = await session.ReadImageAsync(index, ct);
            var image = await Task.Run(() => decoder.Decode(data), ct);
            return new LoadedPage(index, image, null);
        }
        catch (Exception ex) when (ex is ImageDecodeException or InvalidDataException or IOException
            or NotSupportedException or UnauthorizedAccessException)
        {
            // 壊れた画像や、読み出せないエントリ。結果として保持し(何度も読み直さない)、閲覧は続けられるようにする
            return new LoadedPage(index, null, ex is ImageDecodeException ? "画像を読み込めません" : ex.Message);
        }
        finally
        {
            pageCts.Dispose();
        }
    }

    private sealed class PageEntry(Task<LoadedPage> task, CancellationTokenSource cts)
    {
        public Task<LoadedPage> Task { get; } = task;

        /// <summary>読み込みが終わっていなければ中止する。終わっていれば何もしない。</summary>
        public void Cancel()
        {
            if (Task.IsCompleted)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 読み込みが終わって、すでに片付けられた
            }
        }
    }
}
