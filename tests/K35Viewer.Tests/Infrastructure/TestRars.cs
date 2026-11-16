namespace K35Viewer.Tests.Infrastructure;

/// <summary>
/// テスト用の RAR（RAR5）を作る。RAR を作る道具（WinRAR）がなくても、CI で RAR の読み取りを確かめられるようにするため、
/// 公開されている RAR 5.0 の形式（無圧縮、ソリッドでない）を、自分で書く。
/// ヘッダーは、「CRC32、ヘッダーの大きさ、種類、フラグ…」の並びで、数値は、可変長（7ビットずつ、続きがあれば上位ビットが 1）。
/// </summary>
internal static class TestRars
{
    private static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private const int MainHeader = 1;
    private const int FileHeader = 2;
    private const int EndHeader = 5;
    private const int HasDataArea = 0x0002;
    private const int FileHasCrc = 0x0004;

    /// <summary>指定したエントリを、無圧縮で持つ RAR5 を作る。</summary>
    public static string CreateStored(string path, params (string Name, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new MemoryStream();
        stream.Write(Signature);

        // 書庫の先頭のヘッダー（書庫のフラグは 0）
        WriteBlock(stream, MainHeader, 0, null, Vint(0), null);

        foreach (var (name, data) in entries)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            var specific = new List<byte>();
            specific.AddRange(Vint(FileHasCrc)); // ファイルのフラグ: CRC32 がある
            specific.AddRange(Vint((ulong)data.Length)); // 展開後の大きさ
            specific.AddRange(Vint(0)); // 属性
            specific.AddRange(BitConverter.GetBytes(Crc32(data))); // データの CRC32
            specific.AddRange(Vint(0)); // 圧縮の情報: 版 0、ソリッドでない、方式 0（無圧縮）
            specific.AddRange(Vint(0)); // 作った OS（Windows）
            specific.AddRange(Vint((ulong)nameBytes.Length));
            specific.AddRange(nameBytes);
            WriteBlock(stream, FileHeader, HasDataArea, (ulong)data.Length, [.. specific], data);
        }

        // 書庫の終わり
        WriteBlock(stream, EndHeader, 0, null, Vint(0), null);

        File.WriteAllBytes(path, stream.ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    /// <summary>ブロック（CRC32、ヘッダーの大きさ、ヘッダー本体、データ）を書く。</summary>
    private static void WriteBlock(Stream stream, int type, int flags, ulong? dataSize, byte[] specific, byte[]? data)
    {
        var body = new List<byte>();
        body.AddRange(Vint((ulong)type));
        body.AddRange(Vint((ulong)flags));
        if ((flags & HasDataArea) != 0)
        {
            body.AddRange(Vint(dataSize ?? 0));
        }

        body.AddRange(specific);

        // ヘッダーの大きさは、「種類」から、ヘッダーの終わりまで。CRC32 は、「ヘッダーの大きさ」から、ヘッダーの終わりまで
        var sized = new List<byte>();
        sized.AddRange(Vint((ulong)body.Count));
        sized.AddRange(body);
        stream.Write(BitConverter.GetBytes(Crc32([.. sized])));
        stream.Write([.. sized]);
        if (data is not null)
        {
            stream.Write(data);
        }
    }

    /// <summary>可変長の整数（7ビットずつ、下位から。続きがあれば、上位ビットを 1 にする）。</summary>
    private static byte[] Vint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }
        while (value != 0);
        return [.. bytes];
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
