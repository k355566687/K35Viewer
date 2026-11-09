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
