namespace K35Viewer.Domain;

/// <summary>
/// 自然順の文字列比較。連続する数字は数値として比べ(2 &lt; 10)、それ以外は大文字小文字を区別せず序数で比べる。
/// ZIP内の画像の並び順に使う("img2.jpg" は "img10.jpg" より前)。
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var result = CompareDigitRuns(x.AsSpan(startX, i - startX), y.AsSpan(startY, j - startY));
                if (result != 0)
                {
                    return result;
                }
            }
            else
            {
                var result = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (result != 0)
                {
                    return result;
                }

                i++;
                j++;
            }
        }

        // 片方が先に尽きた場合は短い方を前にする。それでも等しければ元の文字列で順序を決める(安定した全順序にするため)
        var remaining = (x.Length - i).CompareTo(y.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(x, y);
    }

    /// <summary>先頭の0を除いた数値の大小で比べる。桁数が多い方が大きい。数値が等しければ0の少ない方を前にする。</summary>
    private static int CompareDigitRuns(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        var trimmedX = x.TrimStart('0');
        var trimmedY = y.TrimStart('0');

        if (trimmedX.Length != trimmedY.Length)
        {
            return trimmedX.Length.CompareTo(trimmedY.Length);
        }

        var result = trimmedX.CompareTo(trimmedY, StringComparison.Ordinal);
        return result != 0 ? result : x.Length.CompareTo(y.Length);
    }
}
