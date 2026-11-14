using System.Globalization;

namespace K35Viewer.Infrastructure.Thumbnails;

/// <summary>
/// サムネイルのファイル実体の保存・取得・削除。DBの thumbnail_cache はメタデータだけを持ち、画像はここへ置く。
/// ファイル名は Archive ID を基準にし(移動・名前変更でも維持される)、1フォルダのファイル数が増えすぎないよう、
/// ID 1,000 件ごとのサブフォルダに分ける。
/// </summary>
public sealed class ThumbnailCacheStore(string rootDirectory)
{
    public string RootDirectory { get; } = rootDirectory;

    /// <summary>Archive ID に対応する、キャッシュルートからの相対パス(例: 0012\archive-12345.jpg)。</summary>
    public static string GetRelativePath(long archiveId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{archiveId / 1000:D4}{Path.DirectorySeparatorChar}archive-{archiveId}.jpg");

    public string Resolve(string relativePath) => Path.Combine(RootDirectory, relativePath);

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    /// <summary>
    /// サムネイルを保存し、相対パスを返す。一時ファイルへ書いてから置き換えるため、
    /// 書き込み中にアプリが終了しても、壊れたファイルは残らない。
    /// </summary>
    public string Save(long archiveId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var relativePath = GetRelativePath(archiveId);
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return relativePath;
    }

    public byte[] Read(string relativePath) => File.ReadAllBytes(Resolve(relativePath));

    /// <summary>キャッシュに保存しているサムネイルの合計の大きさ（バイト）。フォルダがなければ 0。</summary>
    public long GetTotalBytes()
    {
        long total = 0;
        foreach (var file in EnumerateCacheFiles())
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 数えられないファイルは、含めない
            }
        }

        return total;
    }

    /// <summary>
    /// キャッシュのサムネイルをすべて削除する（要件定義 24.5）。削除できたファイルの数を返す。
    /// 削除するのは、このクラスが作るファイル（サブフォルダの中の archive-*.jpg と、書き込み途中の一時ファイル）だけで、
    /// 同じフォルダにあるほかのファイルには触れない。削除できないファイルがあっても、残りの削除を続ける。
    /// </summary>
    public int Clear()
    {
        var deleted = 0;
        foreach (var file in EnumerateCacheFiles().ToList())
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 削除できないファイルは残す（次に作り直すときに上書きされる）
            }
        }

        foreach (var directory in SafeEnumerateDirectories())
        {
            try
            {
                // 空のフォルダだけ削除される（中身が残っていれば例外になる）
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 空でない、または使用中のフォルダは残す
            }
        }

        return deleted;
    }

    private IEnumerable<string> EnumerateCacheFiles() =>
        SafeEnumerateDirectories().SelectMany(directory =>
        {
            try
            {
                return Directory.EnumerateFiles(directory, "archive-*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        });

    private List<string> SafeEnumerateDirectories()
    {
        try
        {
            return Directory.Exists(RootDirectory)
                ? [.. Directory.EnumerateDirectories(RootDirectory)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>ファイルを削除する。存在しない場合は何もしない。</summary>
    public void Delete(string relativePath) => TryDelete(Resolve(relativePath));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 削除できなくても処理は続ける(次回の生成時に上書きされる)
        }
    }
}
