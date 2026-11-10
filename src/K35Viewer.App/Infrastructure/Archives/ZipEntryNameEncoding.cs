using System.Text;

namespace K35Viewer.Infrastructure.Archives;

/// <summary>
/// UTF-8 のフラグがないZIPのエントリ名を読むための文字コード（開発環境設計書 13.4）。
/// 日本語の Windows で作られた古いZIPは、エントリ名を Shift_JIS（CP932）で保存していることが多い。
/// 名前のバイト列が UTF-8 として正しければ UTF-8、そうでなければ CP932 として読む。どちらでも読めなければ、
/// 読めない文字を U+FFFD にして返す（その場合、再構築は名前を壊さないよう中止する）。
/// 書き込みには使わない（新しく作るZIPは、.NETの既定どおり、ASCII以外の名前を UTF-8 とフラグで保存する）。
/// </summary>
internal sealed class ZipEntryNameEncoding : Encoding
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding ShiftJis = CreateShiftJis();

    private ZipEntryNameEncoding()
    {
    }

    public static ZipEntryNameEncoding Instance { get; } = new();

    public override string EncodingName => "UTF-8 / Shift_JIS (ZIP entry names)";

    public override int GetByteCount(char[] chars, int index, int count) => LenientUtf8.GetByteCount(chars, index, count);

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
        LenientUtf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

    public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes, index, count).Length;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        var text = Decode(bytes, byteIndex, byteCount);
        text.CopyTo(0, chars, charIndex, text.Length);
        return text.Length;
    }

    public override string GetString(byte[] bytes, int index, int count) => Decode(bytes, index, count);

    public override int GetMaxByteCount(int charCount) => LenientUtf8.GetMaxByteCount(charCount);

    // CP932 は1バイトで1文字になることがあるため、バイト数より多くはならない（UTF-8 の置換文字も同様）
    public override int GetMaxCharCount(int byteCount) => Math.Max(LenientUtf8.GetMaxCharCount(byteCount), byteCount + 1);

    private static string Decode(byte[] bytes, int index, int count)
    {
        try
        {
            return StrictUtf8.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException)
        {
        }

        try
        {
            return ShiftJis.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException)
        {
            return LenientUtf8.GetString(bytes, index, count);
        }
    }

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
