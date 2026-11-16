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
    private readonly HashSet<int> _pendingDelete = [];
    private bool _disposed;

    internal ViewerSession(
        long archiveId,
        string archiveName,
        string archivePath,
        IOpenedArchive archive,
        int initialIndex,
        ArchiveFileState? openedFileState = null)
    {
        ArchiveId = archiveId;
        ArchiveName = archiveName;
        ArchivePath = archivePath;
        _archive = archive;
        Entries = archive.Images;
        CurrentIndex = initialIndex;
        OpenedFileState = openedFileState;
    }

    /// <summary>
    /// アーカイブを開く直前のファイルのサイズと更新日時。閲覧中・更新前にファイルが外部で変更されていないかの確認に使う。
    /// 取得できなかった場合は null（その場合、外部変更の確認はできない）。
    /// </summary>
    public ArchiveFileState? OpenedFileState { get; }

    /// <summary>
    /// 削除予約を反映するためのZIP更新の依頼を作る（UC-11）。削除予約がなければ null。
    /// 更新後に開き直す位置（現在の画像、予約済みなら次または前の未削除の画像）も含める。
    /// </summary>
    public ArchiveUpdateRequest? CreateUpdateRequest()
    {
        if (_pendingDelete.Count == 0)
        {
            return null;
        }

        string? resumeEntry = null;
        int? resumeIndex = null;
        if (_pendingDelete.Count < Count)
        {
            var target = !_pendingDelete.Contains(CurrentIndex)
                ? CurrentIndex
                : FindNext(CurrentIndex) is var next and >= 0 ? next : FindPrevious(CurrentIndex);
            resumeEntry = Entries[target].FullName;
            resumeIndex = target - _pendingDelete.Count(index => index < target); // 削除後の、画像の位置
        }

        return new ArchiveUpdateRequest(
            ArchiveId,
            ArchiveName,
            ArchivePath,
            OpenedFileState,
            [.. PendingDeleteEntries],
            Count - _pendingDelete.Count,
            resumeEntry,
            resumeIndex);
    }

    /// <summary>
    /// このアーカイブの画像を、削除できるか（削除予約を確定して、ZIP を作り直せるか）。RAR / 7z は、閲覧だけで、false。
    /// </summary>
    public bool CanModify => _archive.CanModify;

    /// <summary>ソリッド形式の RAR / 7z か。後ろのページほど、読み出しが遅い。</summary>
    public bool IsSolid => _archive.IsSolid;

    public long ArchiveId { get; }

    public string ArchiveName { get; }

    public string ArchivePath { get; }

    /// <summary>画像エントリ。自然順。</summary>
    public IReadOnlyList<ArchiveImageEntry> Entries { get; }

    public int Count => Entries.Count;

    /// <summary>現在の画像の位置（0始まり）。</summary>
    public int CurrentIndex { get; private set; }

    public ArchiveImageEntry CurrentEntry => Entries[CurrentIndex];

    /// <summary>削除予約されている画像の数。</summary>
    public int PendingDeleteCount => _pendingDelete.Count;

    /// <summary>
    /// 削除予約されている画像のエントリ名（自然順）。ZIPの再構築（Phase 8）で除外する対象。
    /// DBへは保存せず、アーカイブを離れるときに確定または破棄する（内部アーキテクチャ設計書 5.5）。
    /// </summary>
    public IReadOnlyList<string> PendingDeleteEntries => [.. _pendingDelete.Order().Select(index => Entries[index].FullName)];

    /// <summary>削除予約されている画像の位置（昇順）。</summary>
    public IReadOnlyList<int> PendingDeleteIndices => [.. _pendingDelete.Order()];

    /// <summary>次に移動できる（削除予約されていない）画像が、現在位置より後ろにあるか。</summary>
    public bool CanMoveNext => FindNext(CurrentIndex) >= 0;

    /// <summary>次に移動できる（削除予約されていない）画像が、現在位置より前にあるか。</summary>
    public bool CanMovePrevious => FindPrevious(CurrentIndex) >= 0;

    public bool IsPendingDelete(int index) => _pendingDelete.Contains(index);

    /// <summary>
    /// 指定した画像の削除予約を、追加（<paramref name="pending"/> が true）または解除する。範囲外の位置は無視する。
    /// ZIP本体は変更しない。実際に状態が変わった画像の数を返す。
    /// </summary>
    public int SetPendingDelete(IEnumerable<int> indices, bool pending)
    {
        var changed = 0;
        foreach (var index in indices)
        {
            if (index < 0 || index >= Count)
            {
                continue;
            }

            if (pending ? _pendingDelete.Add(index) : _pendingDelete.Remove(index))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>すべての削除予約を破棄する。</summary>
    public void ClearPendingDelete() => _pendingDelete.Clear();

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

    /// <summary>
    /// 次の画像へ。削除予約された画像は飛ばす。後ろに移動先がなければ移動せず false（先頭へは戻らない）。
    /// </summary>
    public bool MoveNext()
    {
        var index = FindNext(CurrentIndex);
        return index >= 0 && MoveTo(index);
    }

    /// <summary>前の画像へ。削除予約された画像は飛ばす。</summary>
    public bool MovePrevious()
    {
        var index = FindPrevious(CurrentIndex);
        return index >= 0 && MoveTo(index);
    }

    /// <summary>削除予約されていない最初の画像へ。</summary>
    public bool MoveFirst()
    {
        var index = FindNext(-1);
        return index >= 0 && MoveTo(index);
    }

    /// <summary>削除予約されていない最後の画像へ。</summary>
    public bool MoveLast()
    {
        var index = FindPrevious(Count);
        return index >= 0 && MoveTo(index);
    }

    // 指定位置より後ろで、削除予約されていない最初の位置。なければ -1
    private int FindNext(int from)
    {
        for (var index = from + 1; index < Count; index++)
        {
            if (!_pendingDelete.Contains(index))
            {
                return index;
            }
        }

        return -1;
    }

    // 指定位置より前で、削除予約されていない最後の位置。なければ -1
    private int FindPrevious(int from)
    {
        for (var index = from - 1; index >= 0; index--)
        {
            if (!_pendingDelete.Contains(index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// 指定した位置の画像のデータを読み出す（呼び出したスレッドで同期的に行う）。
    /// 背景スレッドで動くサムネイル生成から使う。閉じたあとは <see cref="ObjectDisposedException"/>。
    /// </summary>
    public byte[] ReadImage(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "画像の位置が範囲外です");
        }

        return _archive.ReadEntry(Entries[index].FullName, MaxImageBytes);
    }

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
