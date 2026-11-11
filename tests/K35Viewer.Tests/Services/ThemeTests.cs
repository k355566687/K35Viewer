using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace K35Viewer.Tests.Services;

/// <summary>
/// 画面の色（テーマ）。XAMLで参照している色の名前が、テーマの中に実際にあることを確認する。
/// 名前を間違えても実行時のエラーにはならず、文字や枠が見えなくなるだけのため、テストで防ぐ。
/// </summary>
[Collection(WpfViewsGroup.Name)]
public partial class ThemeTests
{
    [GeneratedRegex(@"\{DynamicResource\s+([A-Za-z0-9_.]+)\}")]
    private static partial Regex DynamicResourcePattern();

    private static string ViewsDirectory()
    {
        // テストの実行場所から、リポジトリの src を探す
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "K35Viewer.App")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "K35Viewer.App");
    }

    [Fact]
    public void XAMLで参照している色は_すべてダークのテーマの中にある()
    {
        var keys = Directory.EnumerateFiles(ViewsDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => DynamicResourcePattern().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var missing = new List<string>();

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new Window());
            missing.AddRange(keys.Where(key => window.TryFindResource(key) is not Brush));
        });

        Assert.NotEmpty(keys);
        Assert.Empty(missing);
    }

    [Fact]
    public void ダークのテーマでは_画面の背景が暗く_文字が明るい()
    {
        Color background = default, text = default;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new Window());
            background = ((SolidColorBrush)window.FindResource("ApplicationBackgroundBrush")).Color;
            text = ((SolidColorBrush)window.FindResource("TextFillColorPrimaryBrush")).Color;
        });

        Assert.True(Luminance(background) < 0.2, $"背景が暗くありません: {background}");
        Assert.True(Luminance(text) > 0.8, $"文字が明るくありません: {text}");
    }

    private static double Luminance(Color color) => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
}
