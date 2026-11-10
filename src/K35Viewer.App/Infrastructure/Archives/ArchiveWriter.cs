using System.IO.Compression;

namespace K35Viewer.Infrastructure.Archives;

/// <summary>書き出したエントリ1件分の記録（名前、展開後のサイズ、元のエントリのCRC32）。検証で、元のエントリと突き合わせるために使う。</summary>
public sealed record WrittenEntry(string FullName, long Length, uint Crc32);

/// <summary>
/// ZIPの再構築・検証の結果。失敗は例外ではなく <see cref="ErrorCode"/> で返す
/// （例: ARCHIVE_BROKEN、DISK_FULL、ACCESS_DENIED、IO_ERROR、ENTRY_NOT_FOUND、UNSUPPORTED_ENTRY_NAME）。
/// </summary>
public sealed record ArchiveWriteResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<WrittenEntry>? Entries = null,
    int RemovedCount = 0)
{
    public static ArchiveWriteResult Failure(string code, string message) => new(false, code, message);
}

/// <summary>ZIP再構築の境界。テストで失敗を作れるよう、インターフェースにしている。</summary>
public interface IArchiveWriter
{
    /// <summary>
    /// 元のZIPから、<paramref name="excludedEntries"/> を除いたエントリを新しいZIPへコピーする。
    /// 元のZIPは変更しない。<paramref name="destinationPath"/> は新規作成で、すでにあれば失敗する（上書きしない）。
    /// 失敗・キャンセルの場合、書きかけのファイルが残ることがある（呼び出し側が削除する）。
    /// </summary>
    ArchiveWriteResult Rebuild(
        string sourcePath, string destinationPath, IReadOnlySet<string> excludedEntries, CancellationToken ct = default);

    /// <summary>
    /// 作成したZIPを開き直し、期待どおりのエントリが揃い、すべて展開できることを確認する。
    /// </summary>
    ArchiveWriteResult Verify(string path, IReadOnlyList<WrittenEntry> expected, CancellationToken ct = default);
}

/// <summary>
/// <see cref="System.IO.Compression.ZipArchive"/> を使ったZIP再構築（内部アーキテクチャ設計書 7.3、開発環境設計書 13.2）。
/// 元ZIPを直接更新せず、削除対象以外を新しいZIPへコピーする。
/// </summary>
/// <param name="compressionLevel">圧縮するエントリの圧縮レベル。</param>
/// <param name="storeBarelyCompressed">
/// true なら、元のZIPでほとんど圧縮されていないエントリ（圧縮後が元の <see cref="BarelyCompressedRatio"/> 以上）は、
/// 圧縮し直さずにそのまま保存する。
/// </param>
public sealed class ArchiveWriter(
    CompressionLevel compressionLevel = ArchiveWriter.DefaultCompressionLevel,
    bool storeBarelyCompressed = true) : IArchiveWriter
{
    /// <summary>
    /// 圧縮するエントリの圧縮レベル。圧縮の効くエントリで、容量が元より増えないようにするため、
    /// 最も小さくなる設定にする（<see cref="CompressionLevel.Fastest"/> は元より大きくなることがある。測定結果は内部アーキテクチャ設計書 29章）。
    /// </summary>
    public const CompressionLevel DefaultCompressionLevel = CompressionLevel.Optimal;

    /// <summary>
    /// 元のZIPで、圧縮後の大きさが元の大きさのこの割合以上のエントリは「ほとんど圧縮されていない」とみなす。
    /// JPEG / PNG などの圧縮済みの画像が該当する。再圧縮しても容量はほぼ変わらず、時間だけがかかる（約15倍）ため、そのまま保存する。
    /// 容量が増えるとしても、最大で 1 - この割合（5%）。
    /// </summary>
    public const double BarelyCompressedRatio = 0.95;

    private const int ErrorDiskFull = 112; // ERROR_DISK_FULL
    private const int ErrorHandleDiskFull = 39; // ERROR_HANDLE_DISK_FULL

    public ArchiveWriteResult Rebuild(
        string sourcePath, string destinationPath, IReadOnlySet<string> excludedEntries, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(excludedEntries);

        try
        {
            // 閲覧中のビューアなど、他の読み取りを妨げない共有モードで開く
            using var sourceStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false);

            // 名前を正しく読めていないアーカイブは、書き出すと名前を壊してしまうため扱わない
            if (source.Entries.Any(e => e.FullName.Contains('\uFFFD', StringComparison.Ordinal)))
            {
                return ArchiveWriteResult.Failure(
                    "UNSUPPORTED_ENTRY_NAME", "エントリ名の文字コードを判別できないアーカイブのため、更新できません");
            }

            var written = new List<WrittenEntry>(source.Entries.Count);
            var removed = 0;

            // 新規作成のみ（FileMode.CreateNew）。同名のファイルがあっても上書きしない
            using var destinationStream = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
            using (var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in source.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (excludedEntries.Contains(entry.FullName))
                    {
                        removed++;
                        continue;
                    }

                    Copy(entry, destination, ct);
                    written.Add(new WrittenEntry(entry.FullName, entry.Length, entry.Crc32));
                }
            }

            destinationStream.Flush(flushToDisk: true);

            if (removed != excludedEntries.Count)
            {
                return ArchiveWriteResult.Failure(
                    "ENTRY_NOT_FOUND", "削除対象の画像がアーカイブ内に見つかりません。アーカイブが変更された可能性があります");
            }

            return new ArchiveWriteResult(true, Entries: written, RemovedCount: removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && TryClassify(ex, out var failure))
        {
            return failure;
        }
    }

    public ArchiveWriteResult Verify(string path, IReadOnlyList<WrittenEntry> expected, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expected);

        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count != expected.Count)
            {
                return ArchiveWriteResult.Failure("VERIFY_FAILED", "作成したアーカイブのエントリ数が一致しません");
            }

            // 名前・展開後のサイズ・CRC32を、元のエントリと突き合わせる（同名のエントリがあっても対応できるよう、件数で数える）
            var remaining = expected
                .GroupBy(e => (e.FullName, e.Length, e.Crc32))
                .ToDictionary(g => g.Key, g => g.Count());
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var key = (entry.FullName, entry.Length, entry.Crc32);
                if (!remaining.TryGetValue(key, out var count) || count == 0)
                {
                    return ArchiveWriteResult.Failure("VERIFY_FAILED", "作成したアーカイブの内容が元のアーカイブと一致しません");
                }

                remaining[key] = count - 1;

                // 最後まで展開し、実際のデータのCRC32が記録と一致することを確認する（壊れた圧縮データ、書き込みの欠落を検出するため）
                long total = 0;
                uint crc = 0;
                using var stream = entry.Open();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += read;
                    crc = Crc32Calculator.Update(crc, buffer.AsSpan(0, read));
                }

                if (total != entry.Length || crc != entry.Crc32)
                {
                    return ArchiveWriteResult.Failure("VERIFY_FAILED", "作成したアーカイブのエントリを正しく読み取れません");
                }
            }

            return new ArchiveWriteResult(true, Entries: expected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && TryClassify(ex, out var failure))
        {
            return failure;
        }
    }

    /// <summary>I/O例外を、ディスク容量不足とそれ以外に分類する。</summary>
    internal static string ClassifyIOException(IOException ex)
    {
        var win32 = ex.HResult & 0xFFFF;
        return win32 is ErrorDiskFull or ErrorHandleDiskFull ? "DISK_FULL" : "IO_ERROR";
    }

    private void Copy(ZipArchiveEntry source, ZipArchive destination, CancellationToken ct)
    {
        // フォルダのエントリ（名前が「/」で終わる）は、中身なしで作る
        var isDirectory = source.FullName.EndsWith('/') || source.FullName.EndsWith('\\');
        var target = isDirectory
            ? destination.CreateEntry(source.FullName)
            : destination.CreateEntry(source.FullName, LevelFor(source));

        target.LastWriteTime = source.LastWriteTime;
        target.ExternalAttributes = source.ExternalAttributes;
        if (!string.IsNullOrEmpty(source.Comment))
        {
            target.Comment = source.Comment;
        }

        if (isDirectory)
        {
            return;
        }

        using var input = source.Open();
        using var output = target.Open();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    // ほとんど圧縮されていないエントリは、再圧縮せずに保存する（圧縮済みの画像を、時間をかけて再圧縮しないため）
    private CompressionLevel LevelFor(ZipArchiveEntry source) =>
        storeBarelyCompressed && source.CompressedLength >= source.Length * BarelyCompressedRatio
            ? CompressionLevel.NoCompression
            : compressionLevel;

    private static bool TryClassify(Exception ex, out ArchiveWriteResult failure)
    {
        switch (ex)
        {
            case InvalidDataException or NotSupportedException:
                failure = ArchiveWriteResult.Failure("ARCHIVE_BROKEN", ex.Message);
                return true;
            case FileNotFoundException or DirectoryNotFoundException:
                failure = ArchiveWriteResult.Failure("FILE_NOT_FOUND", ex.Message);
                return true;
            case UnauthorizedAccessException:
                failure = ArchiveWriteResult.Failure("ACCESS_DENIED", ex.Message);
                return true;
            case IOException io:
                failure = ArchiveWriteResult.Failure(ClassifyIOException(io), ex.Message);
                return true;
            default:
                failure = ArchiveWriteResult.Failure("IO_ERROR", ex.Message);
                return false; // 想定外の例外は握りつぶさない
        }
    }
}
