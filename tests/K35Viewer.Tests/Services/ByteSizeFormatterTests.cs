using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.ViewModels;

namespace K35Viewer.Tests.Services;

public class ByteSizeFormatterTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(314572800L, "300.0 MB")]
    [InlineData(1073741824L, "1.0 GB")]
    public void Format_1024単位で読みやすい表記にする(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, ByteSizeFormatter.Format(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ArchiveListItem_未解析の画像枚数は_ダッシュで表示する()
    {
        var unanalyzed = new ArchiveListItem(new ArchiveSummary(1, "a.zip", @"D:\A", 10, 1700000000, null, "PENDING"));
        var analyzed = new ArchiveListItem(new ArchiveSummary(2, "b.zip", @"D:\A", 10, 1700000000, 1234, "READY"));

        Assert.Equal("－", unanalyzed.ImageCountText);
        Assert.Equal(1234.ToString("N0", CultureInfo.CurrentCulture), analyzed.ImageCountText);
        Assert.Equal(@"D:\A", analyzed.Location);
        Assert.Equal(
            UnixTime.FromUnixSeconds(1700000000).ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture),
            analyzed.LastWriteTimeText);
    }
}
