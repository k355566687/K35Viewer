namespace K35Viewer.Infrastructure.FileSystem;

/// <summary>
/// パスの比較用正規化。Windowsは大文字小文字を区別しないため、フルパス化・末尾区切り除去のうえ大文字に統一する。
/// DBの normalized_path にこの値を保存する。
/// </summary>
public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        if (full.Length > rootLength)
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full.ToUpperInvariant();
    }

    /// <summary>normalizedPath が ancestor 自身、またはその配下であれば true(いずれも正規化済みの値)。</summary>
    public static bool IsSameOrDescendant(string normalizedPath, string normalizedAncestor)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        ArgumentNullException.ThrowIfNull(normalizedAncestor);

        if (string.Equals(normalizedPath, normalizedAncestor, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = normalizedAncestor.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedAncestor
            : normalizedAncestor + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>画面表示用のフォルダ名。ドライブ直下などフォルダ名がない場合はパス全体を返す。</summary>
    public static string GetDisplayName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
