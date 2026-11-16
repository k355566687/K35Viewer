using System.Globalization;
using System.Reflection;

namespace K35Viewer.Infrastructure.Database;

/// <summary>1つのスキーマ変更。Versionは適用後の schema_version になる。</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>
    /// SQL に、この行（先頭など、どこでもよい）があると、この移行の間だけ外部キーを切る。テーブルを作り直す移行（SQLite 公式の手順）に使う。
    /// 移行の終わりに、<c>PRAGMA foreign_key_check</c> で整合を確かめ、問題があれば、取り消す。
    /// </summary>
    public const string ForeignKeysOffMarker = "-- k35:foreign-keys=off";

    private const string ResourcePrefix = "Migrations.";
    private const string ResourceSuffix = ".sql";

    /// <summary>
    /// アセンブリに埋め込まれた「NNNN_名前.sql」を、バージョン昇順で読み込む。
    /// </summary>
    /// <summary>この移行の間、外部キーを切るか（<see cref="ForeignKeysOffMarker"/> の行があるか）。</summary>
    public bool DisablesForeignKeys => Sql.Contains(ForeignKeysOffMarker, StringComparison.Ordinal);

    public static IReadOnlyList<Migration> LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var migrations = new List<Migration>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];
            var separator = name.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0
                || !int.TryParse(name.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version <= 0)
            {
                throw new InvalidOperationException($"マイグレーション名が不正です: {resourceName}");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"マイグレーションを読み込めません: {resourceName}");
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(version, name, reader.ReadToEnd()));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 1; i < migrations.Count; i++)
        {
            if (migrations[i].Version == migrations[i - 1].Version)
            {
                throw new InvalidOperationException($"マイグレーションのバージョンが重複しています: {migrations[i].Version}");
            }
        }

        return migrations;
    }
}
