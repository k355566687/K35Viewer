using System.IO.Compression;
using K35Viewer.Domain;

namespace K35Viewer.Infrastructure.Archives;

/// <summary>アーカイブ内の画像エントリ。Length は展開後のサイズ。</summary>
public sealed record ArchiveImageEntry(string FullName, long Length);

/// <summary>
/// 開いたままのアーカイブ。ビューアで連続して頁をめくる間、開き直さずに読み取るために使う。
/// 閉じる(Dispose)まで、アーカイブのファイルを開いたままにする。読み取りは内部で直列化されており、
/// 複数のスレッドから呼んでよい。
/// </summary>
public interface IOpenedArchive : IDisposable
{
    /// <summary>対応画像（JPEG / PNG）のエントリ。自然順に並んでいる。</summary>
    IReadOnlyList<ArchiveImageEntry> Images { get; }

    /// <summary>エントリの内容を全部メモリへ読み込む。maxBytes を超える場合は <see cref="InvalidDataException"/>。</summary>
    byte[] ReadEntry(string entryName, long maxBytes, CancellationToken ct = default);

    /// <summary>
    /// このアーカイブを、作り直せるか（画像の削除予約を確定できるか）。ZIP だけ true。RAR / 7z は、閲覧だけ（書き込みの形式が公開されていない）。
    /// </summary>
    bool CanModify { get; }

    /// <summary>
    /// ソリッド形式（全体を1つの塊に圧縮した RAR / 7z）か。1枚だけ取り出すにも、手前を読み進めるため、後ろのページほど遅い。
    /// </summary>
    bool IsSolid { get; }
}

/// <summary>
/// アーカイブを開いた結果。Status が Ready のときだけ Archive があり、呼び出し側が Dispose する。
/// </summary>
public sealed record ArchiveOpenResult(
    ArchiveAnalysisStatus Status,
    IOpenedArchive? Archive,
    string? ErrorCode = null,
    string? ErrorMessage = null);

internal sealed class OpenedZip(ZipArchive zip, IReadOnlyList<ArchiveImageEntry> images) : IOpenedArchive
{
    // ZipArchive は、同じストリームから複数のエントリを同時に読むと壊れるため、読み取りを直列化する
    private readonly object _lock = new();

    public bool CanModify => true;

    public bool IsSolid => false;
    private bool _disposed;

    public IReadOnlyList<ArchiveImageEntry> Images { get; } = images;

    public byte[] ReadEntry(string entryName, long maxBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var entry = zip.GetEntry(entryName)
                ?? throw new FileNotFoundException($"アーカイブ内にエントリがありません: {entryName}");
            return ArchiveReader.ReadEntryContent(entry, maxBytes, ct);
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
            zip.Dispose();
        }
    }
}
