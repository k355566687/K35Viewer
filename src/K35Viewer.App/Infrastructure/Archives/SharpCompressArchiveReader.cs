using K35Viewer.Domain;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace K35Viewer.Infrastructure.Archives;

/// <summary>
/// RAR / 7z（CBR / CB7）の読み取り（SharpCompress。GitHub の Issue #3）。<b>閲覧だけ</b>で、書き込みはしない
/// （RAR は、書き込みの形式が公開されていない）。ZIP と同じ判定（画像の拡張子、自然順、暗号化、壊れている）を、同じ形で返す。
/// 展開してファイルに書き出さず、メモリ上で読む。エントリの名前は、区切りを / にそろえる。
/// </summary>
internal sealed class SharpCompressArchiveReader : IArchiveReader
{
    private static readonly ReaderOptions OpenOptions = new() { LeaveStreamOpen = false };

    public ArchiveAnalysis Analyze(string archivePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        try
        {
            using var stream = OpenStream(archivePath);
            using var archive = ArchiveFactory.Open(stream, OpenOptions);
            var scan = Scan(archive, ct);
            if (scan.Error is { } error)
            {
                return new ArchiveAnalysis(error.Status, 0, [], error.Code, error.Message);
            }

            var candidates = scan.Images
                .Select(i => i.Name)
                .Order(NaturalStringComparer.Instance)
                .Take(ArchiveReader.MaxCoverCandidates)
                .ToList();
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Ready, scan.Images.Count, candidates);
        }
        catch (Exception ex) when (Classify(ex) is { } failure)
        {
            return new ArchiveAnalysis(failure.Status, 0, [], failure.Code, failure.Message);
        }
    }

    public ArchiveOpenResult Open(string archivePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        FileStream? stream = null;
        IArchive? archive = null;
        var handedOver = false; // 開いたアーカイブを呼び出し側へ引き渡したら true（以降は呼び出し側が閉じる）
        try
        {
            stream = OpenStream(archivePath);
            archive = ArchiveFactory.Open(stream, OpenOptions);
            var scan = Scan(archive, ct);
            if (scan.Error is { } error)
            {
                return new ArchiveOpenResult(error.Status, null, error.Code, error.Message);
            }

            var sorted = scan.Images
                .OrderBy(i => i.Name, NaturalStringComparer.Instance)
                .ToList();
            var entries = new Dictionary<string, IArchiveEntry>(StringComparer.Ordinal);
            foreach (var image in sorted)
            {
                _ = entries.TryAdd(image.Name, image.Entry); // 同じ名前が重複していたら、最初のものを使う
            }

            var images = sorted
                .Select(i => i.Name)
                .Distinct(StringComparer.Ordinal)
                .Select(name => new ArchiveImageEntry(name, entries[name].Size))
                .ToList();
            var opened = new OpenedSharpArchive(archive, entries, images, archive.IsSolid);
            handedOver = true;
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Ready, opened);
        }
        catch (Exception ex) when (Classify(ex) is { } failure)
        {
            return new ArchiveOpenResult(failure.Status, null, failure.Code, failure.Message);
        }
        finally
        {
            if (!handedOver)
            {
                archive?.Dispose();
                stream?.Dispose();
            }
        }
    }

    public byte[] ReadEntry(string archivePath, string entryName, long maxBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        using var stream = OpenStream(archivePath);
        using var archive = ArchiveFactory.Open(stream, OpenOptions);
        var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && Normalize(e.Key) == entryName)
            ?? throw new FileNotFoundException($"アーカイブ内にエントリがありません: {entryName}");
        return ReadEntryContent(entry, maxBytes, ct);
    }

    /// <summary>エントリの名前の区切りを / にそろえる（ZIP の名前と同じ形。閲覧位置の記録の照合に使う）。</summary>
    internal static string Normalize(string? key) => (key ?? string.Empty).Replace('\\', '/');

    /// <summary>
    /// 閲覧中に、他のプログラムがファイルを読み書き・削除できなくなる状態を避けるため、共有を許可して開く。
    /// SharpCompress は、シークできるストリームを使う。
    /// </summary>
    private static FileStream OpenStream(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.RandomAccess);

    /// <summary>エントリの内容をメモリへ読み込む。宣言サイズと、実際に読んだ量の両方で上限を守る（圧縮爆弾対策）。</summary>
    internal static byte[] ReadEntryContent(IArchiveEntry entry, long maxBytes, CancellationToken ct)
    {
        if (entry.Size > maxBytes)
        {
            throw new InvalidDataException($"エントリが大きすぎます: {entry.Size} バイト");
        }

        using var source = entry.OpenEntryStream();
        using var buffer = new MemoryStream((int)Math.Clamp(entry.Size, 0, int.MaxValue));
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("エントリが宣言サイズより大きく、上限を超えました");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed record Failure(ArchiveAnalysisStatus Status, string? Code, string? Message);

    private sealed record ImageEntry(string Name, IArchiveEntry Entry);

    private sealed record ScanResult(IReadOnlyList<ImageEntry> Images, Failure? Error);

    /// <summary>画像のエントリを集める。画像がない、暗号化されている、分割されている場合は、Error に理由を入れる。</summary>
    private static ScanResult Scan(IArchive archive, CancellationToken ct)
    {
        var images = new List<ImageEntry>();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                continue;
            }

            var name = Normalize(entry.Key);
            if (ArchiveImageFilter.IsSupportedImage(name))
            {
                images.Add(new ImageEntry(name, entry));
            }
        }

        if (images.Count == 0)
        {
            return new ScanResult([], new Failure(ArchiveAnalysisStatus.Empty, null, null)); // ZIP と同じく、コードもメッセージも付けない
        }

        if (images.Exists(i => i.Entry.IsEncrypted))
        {
            return new ScanResult([], new Failure(ArchiveAnalysisStatus.Unsupported, "ARCHIVE_ENCRYPTED", "暗号化されたアーカイブは扱えません"));
        }

        if (images.Exists(i => i.Entry.IsSplitAfter) || !archive.IsComplete)
        {
            return new ScanResult([], new Failure(ArchiveAnalysisStatus.Unsupported, "ARCHIVE_MULTIVOLUME", "分割されたアーカイブは扱えません"));
        }

        return new ScanResult(images, null);
    }

    /// <summary>
    /// 例外を、アーカイブの状態（壊れている、暗号化されている、分割されている、読めない）に分類する。
    /// 想定外の例外（プログラムの誤りなど）は、null を返して、そのまま投げ直させる。
    /// </summary>
    private static Failure? Classify(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => new Failure(ArchiveAnalysisStatus.Error, "FILE_NOT_FOUND", ex.Message),
        UnauthorizedAccessException => new Failure(ArchiveAnalysisStatus.Error, "ACCESS_DENIED", ex.Message),
        CryptographicException => new Failure(ArchiveAnalysisStatus.Unsupported, "ARCHIVE_ENCRYPTED", "暗号化されたアーカイブは扱えません"),
        MultiVolumeExtractionException or MultipartStreamRequiredException =>
            new Failure(ArchiveAnalysisStatus.Unsupported, "ARCHIVE_MULTIVOLUME", "分割されたアーカイブは扱えません"),
        // SharpCompress は、壊れたファイルや、アーカイブではないファイルで、専用の例外ではなく、InvalidOperationException
        // （「形式を判別できない」「ヘッダーの位置が不正」など）や、範囲外・変換の失敗を投げることがある。出どころの分からないファイルを
        // 開くため、これらも「壊れている」として扱う（この関数を使う場所は、SharpCompress の呼び出しだけを囲んでいる）
        SharpCompressException or InvalidDataException or EndOfStreamException or NotSupportedException
            or InvalidOperationException or FormatException or IndexOutOfRangeException or ArgumentException or OverflowException =>
            new Failure(ArchiveAnalysisStatus.Broken, "ARCHIVE_BROKEN", ex.Message),
        IOException => new Failure(ArchiveAnalysisStatus.Error, "IO_ERROR", ex.Message),
        _ => null,
    };
}

/// <summary>
/// 開いたままの RAR / 7z。読み取りは内部で直列化する（SharpCompress のアーカイブは、複数のスレッドから同時に読めないため）。
/// ソリッド形式（全体を1つの塊に圧縮したもの）は、1枚だけ取り出すにも、手前を読み進める必要があるため、後ろのページほど遅い
/// （測定: 28MB・120枚の 7z で、最後のページに約2〜3秒）。<see cref="IsSolid"/> で、利用者に知らせる。
/// </summary>
internal sealed class OpenedSharpArchive(
    IArchive archive,
    IReadOnlyDictionary<string, IArchiveEntry> entries,
    IReadOnlyList<ArchiveImageEntry> images,
    bool isSolid) : IOpenedArchive
{
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyList<ArchiveImageEntry> Images { get; } = images;

    public bool CanModify => false;

    public bool IsSolid { get; } = isSolid;

    public byte[] ReadEntry(string entryName, long maxBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var entry = entries.TryGetValue(entryName, out var found)
                ? found
                : throw new FileNotFoundException($"アーカイブ内にエントリがありません: {entryName}");
            return SharpCompressArchiveReader.ReadEntryContent(entry, maxBytes, ct);
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
            archive.Dispose();
        }
    }
}
