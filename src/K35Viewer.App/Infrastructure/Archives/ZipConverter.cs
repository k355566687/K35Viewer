using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace K35Viewer.Infrastructure.Archives;

/// <summary>RAR / 7z から ZIP への変換の境界（GitHub の Issue #20）。テストで失敗を作れるよう、インターフェースにしている。</summary>
public interface IZipConverter
{
    /// <summary>
    /// 変換後に必要な容量の見積もり（全エントリの展開後のサイズの合計）。読めない場合は null。
    /// </summary>
    long? EstimateSize(string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// RAR / 7z の全エントリ（画像以外のファイルとフォルダを含む）を、新しい ZIP へ写す。元のファイルは変更しない。
    /// <paramref name="destinationPath"/> は新規作成で、すでにあれば失敗する（上書きしない）。
    /// 失敗・キャンセルの場合、書きかけのファイルが残ることがある（呼び出し側が削除する）。
    /// 結果の <see cref="ArchiveWriteResult.Entries"/> は、<see cref="IArchiveWriter.Verify"/> での確認に使う。
    /// </summary>
    ArchiveWriteResult Convert(string sourcePath, string destinationPath, CancellationToken ct = default);
}

/// <summary>
/// SharpCompress で RAR / 7z を読み、<see cref="ZipArchive"/> で ZIP を書く。
/// エントリの名前は、区切りを / にそろえて、そのまま使う（閲覧位置の記録と、サムネイルの元の画像の名前が、変換の前後で変わらない）。
/// 名前に ASCII 以外の文字があれば、UTF-8 の印を付けて書く。
/// </summary>
public sealed class ZipConverter : IZipConverter
{
    // ZIP の日時は 1980年〜2107年しか表せない
    private static readonly DateTimeOffset MinZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaxZipTime = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

    // 圧縮済みの形式は、圧縮し直しても小さくならず、時間だけがかかるため、そのまま保存する（ZIP の作り直しと同じ考え）
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".jxl", ".zip", ".rar", ".7z", ".mp4", ".mp3",
    };

    private static readonly ReaderOptions OpenOptions = new() { LeaveStreamOpen = false };

    public long? EstimateSize(string sourcePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        try
        {
            using var stream = OpenSource(sourcePath);
            using var archive = ArchiveFactory.Open(stream, OpenOptions);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.IsDirectory)
                {
                    total += Math.Max(0, entry.Size);
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Classify(ex) is not null)
        {
            return null;
        }
    }

    public ArchiveWriteResult Convert(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            using var sourceStream = OpenSource(sourcePath);
            using var archive = ArchiveFactory.Open(sourceStream, OpenOptions);

            // 暗号化と分割は、読めない部分が出るため、変換しない（全部を写せない ZIP を作らない）
            if (archive.Entries.Any(e => e.IsEncrypted))
            {
                return ArchiveWriteResult.Failure("ARCHIVE_ENCRYPTED", "暗号化されたアーカイブは変換できません");
            }

            if (!archive.IsComplete || archive.Entries.Any(e => e.IsSplitAfter))
            {
                return ArchiveWriteResult.Failure("ARCHIVE_MULTIVOLUME", "分割されたアーカイブは変換できません");
            }

            var written = new List<WrittenEntry>();

            // 新規作成のみ（FileMode.CreateNew）。同名のファイルがあっても上書きしない
            using var destinationStream = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
            using (var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    // ソリッド形式と 7z は、先頭から順に読む（1件ずつ開くと、そのたびに手前から読み直して遅くなるため）
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        ct.ThrowIfCancellationRequested();
                        written.Add(Copy(reader.Entry, reader.OpenEntryStream, destination, ct));
                    }
                }
                else
                {
                    // ソリッドでない RAR は、順に読む方法が使えないため、1件ずつ開く（それぞれ独立に圧縮されていて、速い）
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        written.Add(Copy(entry, entry.OpenEntryStream, destination, ct));
                    }
                }
            }

            destinationStream.Flush(flushToDisk: true);
            return new ArchiveWriteResult(true, Entries: written);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Classify(ex) is { } failure)
        {
            return failure;
        }
    }

    /// <summary>エントリの名前を ZIP の形にする（区切りは /、フォルダは末尾に /）。</summary>
    internal static string ToZipName(string? key, bool isDirectory)
    {
        var name = (key ?? string.Empty).Replace('\\', '/');
        return isDirectory && !name.EndsWith('/') ? name + "/" : name;
    }

    /// <summary>ZIP の日時に収まるようにする（表せない、または不明なら、範囲の端か、今の時刻）。</summary>
    internal static DateTimeOffset ToZipTime(DateTime? time)
    {
        if (time is not { } value)
        {
            return DateTimeOffset.Now;
        }

        DateTimeOffset offset;
        try
        {
            offset = new DateTimeOffset(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Local) : value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return MinZipTime;
        }

        return offset < MinZipTime ? MinZipTime : offset > MaxZipTime ? MaxZipTime : offset;
    }

    private static WrittenEntry Copy(IEntry source, Func<Stream> openSource, ZipArchive destination, CancellationToken ct)
    {
        var name = ToZipName(source.Key, source.IsDirectory);
        if (source.IsDirectory)
        {
            var folder = destination.CreateEntry(name);
            folder.LastWriteTime = ToZipTime(source.LastModifiedTime);
            return new WrittenEntry(name, 0, 0);
        }

        var level = StoredExtensions.Contains(Path.GetExtension(name)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        var target = destination.CreateEntry(name, level);
        target.LastWriteTime = ToZipTime(source.LastModifiedTime);

        long length = 0;
        uint crc = 0;
        using (var input = openSource())
        using (var output = target.Open())
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                crc = Crc32Calculator.Update(crc, buffer.AsSpan(0, read));
                length += read;
            }
        }

        // 宣言されたサイズと、実際に読めた量が違えば、元のアーカイブが壊れている
        if (source.Size >= 0 && source.Size != length)
        {
            throw new InvalidDataException($"エントリの大きさが一致しません: {name}");
        }

        return new WrittenEntry(name, length, crc);
    }

    private static FileStream OpenSource(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);

    private static ArchiveWriteResult? Classify(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => ArchiveWriteResult.Failure("FILE_NOT_FOUND", ex.Message),
        UnauthorizedAccessException => ArchiveWriteResult.Failure("ACCESS_DENIED", ex.Message),
        CryptographicException => ArchiveWriteResult.Failure("ARCHIVE_ENCRYPTED", "暗号化されたアーカイブは変換できません"),
        MultiVolumeExtractionException or MultipartStreamRequiredException =>
            ArchiveWriteResult.Failure("ARCHIVE_MULTIVOLUME", "分割されたアーカイブは変換できません"),
        // SharpCompress は、壊れたファイルで、専用の例外ではない例外も投げる（SharpCompressArchiveReader.Classify と同じ扱い）
        SharpCompressException or InvalidDataException or EndOfStreamException or NotSupportedException
            or InvalidOperationException or FormatException or IndexOutOfRangeException or ArgumentException or OverflowException =>
            ArchiveWriteResult.Failure("ARCHIVE_BROKEN", ex.Message),
        IOException io => ArchiveWriteResult.Failure(ArchiveWriter.ClassifyIOException(io), ex.Message),
        _ => null,
    };
}
