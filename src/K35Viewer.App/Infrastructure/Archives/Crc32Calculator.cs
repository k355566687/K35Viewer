namespace K35Viewer.Infrastructure.Archives;

/// <summary>
/// ZIPで使われるCRC-32（IEEE 802.3、反転多項式 0xEDB88320）の計算。
/// 作成したZIPの内容が、元のエントリと一致するかの検証に使う。
/// </summary>
internal static class Crc32Calculator
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>これまでの値 <paramref name="crc"/>（最初は 0）に、データを加えたCRC-32を返す。</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var value = ~crc;
        foreach (var b in data)
        {
            value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
        }

        return ~value;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
