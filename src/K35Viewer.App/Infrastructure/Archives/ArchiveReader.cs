using System.IO.Compression;
using K35Viewer.Domain;

namespace K35Viewer.Infrastructure.Archives;

/// <summary>ZIP / CBZ の読み取り境界。テストで差し替えられるよう、インターフェースにしている。</summary>
public interface IArchiveReader
{
    /// <summary>アーカイブを開き、対応画像の数と表紙候補を返す。読めない場合も例外ではなく結果の状態で返す。</summary>
    ArchiveAnalysis Analyze(string archivePath, CancellationToken ct = default);

    /// <summary>
    /// アーカイブ内の1エントリを全部メモリへ読み込む。maxBytes を超える場合は例外を投げる。
    /// ZIP Streamの寿命に依存させないため、呼び出し側へはバイト列で渡す。
    /// </summary>
    byte[] ReadEntry(string archivePath, string entryName, long maxBytes, CancellationToken ct = default);

    /// <summary>
    /// ビューアで閲覧するためにアーカイブを開く。対応画像を自然順に並べ、開いたまま返す。
    /// 画像がない、壊れている、暗号化されている等の場合は、Archive を持たない結果で返す(例外にしない)。
    /// </summary>
    ArchiveOpenResult Open(string archivePath, CancellationToken ct = default);
}

public sealed class ArchiveReader : IArchiveReader
{
    /// <summary>表紙候補として返す最大数。先頭が壊れている場合に、次の画像で代用するための数。</summary>
    public const int MaxCoverCandidates = 3;

    // RAR / 7z は、SharpCompress で読む（閲覧だけ）。形式は、拡張子ではなく、ファイルの先頭の署名で決める
    private readonly SharpCompressArchiveReader _sharpCompress = new();

    private static bool UsesSharpCompress(string archivePath) =>
        ArchiveFormats.Detect(archivePath) is ArchiveFormat.Rar or ArchiveFormat.SevenZip;

    public ArchiveAnalysis Analyze(string archivePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (UsesSharpCompress(archivePath))
        {
            return _sharpCompress.Analyze(archivePath, ct);
        }

        try
        {
            using var zip = OpenRead(archivePath);

            var images = new List<ZipArchiveEntry>();
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (IsSupportedImage(entry))
                {
                    images.Add(entry);
                }
            }

            if (images.Count == 0)
            {
                return new ArchiveAnalysis(ArchiveAnalysisStatus.Empty, 0, []);
            }

            if (images.Exists(e => e.IsEncrypted))
            {
                return new ArchiveAnalysis(
                    ArchiveAnalysisStatus.Unsupported, 0, [], "ARCHIVE_ENCRYPTED", "暗号化されたアーカイブは扱えません");
            }

            var candidates = images
                .Select(e => e.FullName)
                .Order(NaturalStringComparer.Instance)
                .Take(MaxCoverCandidates)
                .ToList();
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Ready, images.Count, candidates);
        }
        catch (InvalidDataException ex)
        {
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Broken, 0, [], "ARCHIVE_BROKEN", ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Error, 0, [], "FILE_NOT_FOUND", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Error, 0, [], "FILE_NOT_FOUND", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Error, 0, [], "ACCESS_DENIED", ex.Message);
        }
        catch (IOException ex)
        {
            return new ArchiveAnalysis(ArchiveAnalysisStatus.Error, 0, [], "IO_ERROR", ex.Message);
        }
    }

    public ArchiveOpenResult Open(string archivePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (UsesSharpCompress(archivePath))
        {
            return _sharpCompress.Open(archivePath, ct);
        }

        FileStream? stream = null;
        ZipArchive? zip = null;
        var handedOver = false; // 開いたアーカイブを呼び出し側へ引き渡したら true(以降は呼び出し側が閉じる)
        try
        {
            // 閲覧中に、他のプログラムがファイルを読み書き・削除できなくなる状態を避けるため、共有を許可して開く
            stream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, ZipEntryNameEncoding.Instance);

            var images = new List<ZipArchiveEntry>();
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (IsSupportedImage(entry))
                {
                    images.Add(entry);
                }
            }

            if (images.Count == 0)
            {
                return new ArchiveOpenResult(ArchiveAnalysisStatus.Empty, null);
            }

            if (images.Exists(e => e.IsEncrypted))
            {
                return new ArchiveOpenResult(
                    ArchiveAnalysisStatus.Unsupported, null, "ARCHIVE_ENCRYPTED", "暗号化されたアーカイブは扱えません");
            }

            var sorted = images
                .Select(e => new ArchiveImageEntry(e.FullName, e.Length))
                .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
                .ToList();

            var opened = new OpenedZip(zip, sorted);
            handedOver = true;
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Ready, opened);
        }
        catch (InvalidDataException ex)
        {
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Broken, null, "ARCHIVE_BROKEN", ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Error, null, "FILE_NOT_FOUND", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Error, null, "FILE_NOT_FOUND", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Error, null, "ACCESS_DENIED", ex.Message);
        }
        catch (IOException ex)
        {
            return new ArchiveOpenResult(ArchiveAnalysisStatus.Error, null, "IO_ERROR", ex.Message);
        }
        finally
        {
            if (!handedOver)
            {
                zip?.Dispose(); // ストリームも一緒に閉じられる
                stream?.Dispose(); // ZipArchive の生成に失敗した場合に備える(二重に閉じても問題ない)
            }
        }
    }

    public byte[] ReadEntry(string archivePath, string entryName, long maxBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        if (UsesSharpCompress(archivePath))
        {
            return _sharpCompress.ReadEntry(archivePath, entryName, maxBytes, ct);
        }

        using var zip = OpenRead(archivePath);
        var entry = zip.GetEntry(entryName)
            ?? throw new FileNotFoundException($"アーカイブ内にエントリがありません: {entryName}");
        return ReadEntryContent(entry, maxBytes, ct);
    }

    /// <summary>
    /// 読み取り用に開く。UTF-8 のフラグがないエントリ名は、UTF-8 として正しくなければ Shift_JIS として読む（<see cref="ZipEntryNameEncoding"/>）。
    /// アプリの中でZIPを読むときは、必ずこれ（または同じ文字コードの指定）を使う。開き方によって名前が変わると、
    /// 閲覧位置の記録や再構築の削除対象と一致しなくなるため。
    /// </summary>
    internal static ZipArchive OpenRead(string path) => ZipFile.Open(path, ZipArchiveMode.Read, ZipEntryNameEncoding.Instance);

    /// <summary>エントリの内容をメモリへ読み込む。宣言サイズと実際に読んだ量の両方で上限を守る。</summary>
    internal static byte[] ReadEntryContent(ZipArchiveEntry entry, long maxBytes, CancellationToken ct)
    {
        // 宣言サイズが大きすぎるものは、展開せずに拒否する(圧縮爆弾対策)
        if (entry.Length > maxBytes)
        {
            throw new InvalidDataException($"エントリが大きすぎます: {entry.Length} バイト");
        }

        using var source = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            // 宣言サイズが偽られていても、実際に読んだ量で上限を守る
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("エントリが宣言サイズより大きく、上限を超えました");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>JPEG / PNG / GIF / WebP のファイルエントリか。フォルダ、macOSのメタデータ(__MACOSX、._*)は除く（形式によらず共通の判定）。</summary>
    private static bool IsSupportedImage(ZipArchiveEntry entry) =>
        !string.IsNullOrEmpty(entry.Name) && ArchiveImageFilter.IsSupportedImage(entry.FullName);
}
