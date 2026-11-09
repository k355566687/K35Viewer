using K35Viewer.Infrastructure.Archives;

namespace K35Viewer.Services;

/// <summary>
/// 現在開いているアーカイブの閲覧状態(内部アーキテクチャ設計書 5.5)。画像の一覧(自然順)と現在位置を持ち、
/// アーカイブを開いたままにして画像を読み出す。閉じる(Dispose)とアーカイブのファイルを手放す。
/// 位置の操作は画面のスレッドから行う想定で、画像の読み出しだけが複数スレッドから呼ばれる。
/// </summary>
public sealed class ViewerSession : IDisposable
{
    /// <summary>1枚の画像として読み込む最大サイズ。これを超える画像は読み込まない(メモリを守るため)。</summary>
    public const long MaxImageBytes = 128L * 1024 * 1024;

    private readonly IOpenedArchive _archive;
    private bool _disposed;

    internal ViewerSession(long archiveId, string archiveName, string archivePath, IOpenedArchive archive, int initialIndex)
    {
        ArchiveId = archiveId;
        ArchiveName = archiveName;
        ArchivePath = archivePath;
        _archive = archive;
        Entries = archive.Images;
        CurrentIndex = initialIndex;
    }

    public long ArchiveId { get; }

    public string ArchiveName { get; }

    public string ArchivePath { get; }

    /// <summary>画像エントリ。自然順。</summary>
    public IReadOnlyList<ArchiveImageEntry> Entries { get; }

    public int Count => Entries.Count;

    /// <summary>現在の画像の位置（0始まり）。</summary>
    public int CurrentIndex { get; private set; }

    public ArchiveImageEntry CurrentEntry => Entries[CurrentIndex];

    public bool CanMoveNext => CurrentIndex < Count - 1;

    public bool CanMovePrevious => CurrentIndex > 0;

    /// <summary>指定した位置へ移動する。範囲外、または現在位置と同じなら移動せず false。</summary>
    public bool MoveTo(int index)
    {
        if (index < 0 || index >= Count || index == CurrentIndex)
        {
            return false;
        }

        CurrentIndex = index;
        return true;
    }

    /// <summary>次の画像へ。最後の画像なら移動せず false（先頭へは戻らない）。</summary>
    public bool MoveNext() => CanMoveNext && MoveTo(CurrentIndex + 1);

    public bool MovePrevious() => CanMovePrevious && MoveTo(CurrentIndex - 1);

    public bool MoveFirst() => MoveTo(0);

    public bool MoveLast() => MoveTo(Count - 1);

    /// <summary>指定した位置の画像のデータを、背景スレッドで読み出す。</summary>
    public Task<byte[]> ReadImageAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "画像の位置が範囲外です");
        }

        var name = Entries[index].FullName;
        return Task.Run(() => _archive.ReadEntry(name, MaxImageBytes, ct), ct);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();
    }
}
