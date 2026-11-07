using K35Viewer.Infrastructure.FileSystem;

namespace K35Viewer.Tests.Infrastructure;

public class PathNormalizerTests
{
    [Fact]
    public void Normalize_大文字小文字と末尾区切りの違いを同じ値にする()
    {
        var a = PathNormalizer.Normalize(@"D:\Archive\Books");
        var b = PathNormalizer.Normalize(@"d:\archive\books\");
        var c = PathNormalizer.Normalize(@"D:\Archive\Books\..\Books");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Normalize_ドライブ直下の区切りは保持する()
    {
        Assert.Equal(@"D:\", PathNormalizer.Normalize(@"d:\"));
    }

    [Theory]
    [InlineData(@"D:\A", @"D:\A", true)]
    [InlineData(@"D:\A\B", @"D:\A", true)]
    [InlineData(@"D:\AB", @"D:\A", false)] // 前方一致だが別フォルダ
    [InlineData(@"D:\A", @"D:\A\B", false)]
    [InlineData(@"D:\A\B", @"D:\", true)]
    public void IsSameOrDescendant_フォルダ境界で判定する(string path, string ancestor, bool expected)
    {
        Assert.Equal(
            expected,
            PathNormalizer.IsSameOrDescendant(PathNormalizer.Normalize(path), PathNormalizer.Normalize(ancestor)));
    }

    [Theory]
    [InlineData(@"D:\Archive\Books", "Books")]
    [InlineData(@"D:\Archive\Books\", "Books")]
    [InlineData(@"D:\", @"D:\")]
    public void GetDisplayName_フォルダ名を返す(string path, string expected)
    {
        Assert.Equal(expected, PathNormalizer.GetDisplayName(path));
    }
}
