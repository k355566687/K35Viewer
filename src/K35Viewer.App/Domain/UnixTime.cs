namespace K35Viewer.Domain;

/// <summary>
/// UTC Unix time(秒)の変換。DBには64bit整数で保存し、Int32へは変換しない(2038年問題の回避)。
/// </summary>
public static class UnixTime
{
    public static long ToUnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    public static DateTimeOffset FromUnixSeconds(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
