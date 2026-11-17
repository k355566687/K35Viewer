namespace K35Viewer.Infrastructure.Archives;

/// <summary>アーカイブの形式（中身の形式。ファイルの拡張子ではない）。</summary>
public enum ArchiveFormat
{
    /// <summary>判別できなかった（壊れている、対応していない形式など）。</summary>
    Unknown,

    /// <summary>ZIP / CBZ。読み書きできる（画像の削除予約で、作り直せる）。</summary>
    Zip,

    /// <summary>RAR / CBR。閲覧だけ（書き込みの形式が公開されていないため）。</summary>
    Rar,

    /// <summary>7z / CB7。閲覧だけ。</summary>
    SevenZip,
}

/// <summary>
/// 対応しているアーカイブの形式と拡張子（要件定義 19.3、GitHub の Issue #3）。
/// 形式は、拡張子ではなく、ファイルの先頭の署名で決める。「.cbr」という名前の ZIP や、「.cbz」という名前の RAR は、よくあるため。
/// </summary>
public static class ArchiveFormats
{
    /// <summary>スキャンの対象にする拡張子（小文字。区別しない）。</summary>
    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7" };

    /// <summary>拡張子（「.zip」の形）が、対応しているアーカイブのものか。</summary>
    public static bool IsSupportedExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) && SupportedExtensions.Contains(extension);

    /// <summary>拡張子（「.zip」の形）から、DB の extension の値（「zip」など、先頭の点のない小文字）にする。対応していなければ「zip」。</summary>
    public static string ToDatabaseExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return IsSupportedExtension("." + normalized) ? normalized : "zip";
    }

    /// <summary>
    /// ファイルの形式を調べる。先頭の数バイトの署名で決め、読めなかった（開けない、短すぎる、署名が分からない）ときは、拡張子で決める。
    /// 拡張子でも決まらなければ <see cref="ArchiveFormat.Unknown"/>。
    /// </summary>
    public static ArchiveFormat Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None);
            Span<byte> head = stackalloc byte[8];
            var read = stream.Read(head);
            var fromSignature = FromSignature(head[..read]);
            if (fromSignature != ArchiveFormat.Unknown)
            {
                return fromSignature;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 開けない・読めない場合は、拡張子で決める（実際に開くときに、理由が分かる）
        }

        return FromExtension(Path.GetExtension(path));
    }

    /// <summary>先頭のバイト列（8バイトまで）から、形式を決める。決められなければ <see cref="ArchiveFormat.Unknown"/>。</summary>
    internal static ArchiveFormat FromSignature(ReadOnlySpan<byte> head)
    {
        // ZIP: "PK" + 03 04（通常）、05 06（空）、07 08（分割）
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B
            && ((head[2] == 0x03 && head[3] == 0x04) || (head[2] == 0x05 && head[3] == 0x06) || (head[2] == 0x07 && head[3] == 0x08)))
        {
            return ArchiveFormat.Zip;
        }

        // RAR: "Rar!" 1A 07 00（RAR 4 まで）、"Rar!" 1A 07 01 00（RAR 5）
        if (head.Length >= 7 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21
            && head[4] == 0x1A && head[5] == 0x07 && (head[6] == 0x00 || head[6] == 0x01))
        {
            return ArchiveFormat.Rar;
        }

        // 7z: '7' 'z' BC AF 27 1C
        if (head.Length >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
        {
            return ArchiveFormat.SevenZip;
        }

        return ArchiveFormat.Unknown;
    }

    /// <summary>拡張子（「.rar」の形）から、形式を決める。</summary>
    internal static ArchiveFormat FromExtension(string? extension) => (extension ?? string.Empty).ToLowerInvariant() switch
    {
        ".zip" or ".cbz" => ArchiveFormat.Zip,
        ".rar" or ".cbr" => ArchiveFormat.Rar,
        ".7z" or ".cb7" => ArchiveFormat.SevenZip,
        _ => ArchiveFormat.Unknown,
    };
}

/// <summary>アーカイブの中の、画像として扱うエントリの判定（形式によらず共通）。</summary>
public static class ArchiveImageFilter
{
    // GIF は最初の1枚を静止画として扱う。WebP は、Windows の WebP 拡張機能がない環境では読めない
    // （枚数には数えるが、サムネイルとビューアには「読めません」と出る）
    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <summary>
    /// アーカイブの中のエントリの名前（区切りは、/ でも \ でもよい）が、JPEG / PNG / GIF / WebP の画像か。
    /// フォルダ（末尾が区切り）と、macOS のメタデータ（__MACOSX、._*）は除く。
    /// </summary>
    public static bool IsSupportedImage(string entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return false;
        }

        var name = entryName.Replace('\\', '/');
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        if (leaf.Length == 0)
        {
            return false; // フォルダ
        }

        if (name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("._", StringComparison.Ordinal))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(leaf), StringComparer.OrdinalIgnoreCase);
    }
}
