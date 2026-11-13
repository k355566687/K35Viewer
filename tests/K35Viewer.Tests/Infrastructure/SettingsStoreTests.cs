using K35Viewer.Infrastructure;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>設定のファイル（要件定義 24.1）の読み書き。</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ファイルがなければ_既定値で読み_壊れたとはしない()
    {
        var result = new SettingsStore(SettingsFile).Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.False(result.WasCorrupted);
        Assert.Equal(AppTheme.Dark, result.Settings.Theme);
        Assert.True(result.Settings.ConfirmBeforeLeave);
        Assert.True(result.Settings.ConfirmBeforeDelete);
    }

    [Fact]
    public void 保存した設定を_そのまま読める_一時ファイルは残らない()
    {
        var store = new SettingsStore(SettingsFile);
        var settings = new AppSettings
        {
            Theme = AppTheme.System,
            ConfirmBeforeLeave = false,
            ConfirmBeforeDelete = false,
            LibraryViewMode = LibraryViewMode.Thumbnail,
            ViewerViewMode = ViewerViewMode.List,
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load().Settings);
        Assert.False(File.Exists(SettingsFile + ".tmp"));
        Assert.Contains("\"System\"", File.ReadAllText(SettingsFile), StringComparison.Ordinal); // 選択肢は名前で保存する（読んでわかる）
    }

    [Fact]
    public void ファイルにない項目は_既定値になる()
    {
        // 古い版で作ったファイル（項目が少ない）を読んでも、新しい項目は既定値で動く
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, """{ "Theme": "Light" }""");

        var result = new SettingsStore(SettingsFile).Load();

        Assert.False(result.WasCorrupted);
        Assert.Equal(new AppSettings { Theme = AppTheme.Light }, result.Settings);
    }

    [Theory]
    [InlineData("これはJSONではない")]
    [InlineData("""{ "Theme": "Purple" }""")]
    [InlineData("null")]
    public void 壊れたファイルは_既定値で読み_別の名前で残す(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, content);

        var result = new SettingsStore(SettingsFile).Load();

        Assert.True(result.WasCorrupted);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.False(File.Exists(SettingsFile));
        Assert.Equal(content, File.ReadAllText(SettingsFile + ".broken"));
    }

    [Fact]
    public void 設定を変えると_保存して知らせる_変わらなければ何もしない()
    {
        var service = new SettingsService(new SettingsStore(SettingsFile), NullLogger<SettingsService>.Instance);
        var changes = new List<AppSettings>();
        service.Changed += (_, s) => changes.Add(s);

        Assert.True(service.Update(s => s with { Theme = AppTheme.Light }));
        Assert.True(service.Update(s => s with { Theme = AppTheme.Light })); // 同じ値

        Assert.Single(changes);
        Assert.Equal(AppTheme.Light, service.Current.Theme);
        Assert.Equal(AppTheme.Light, new SettingsStore(SettingsFile).Load().Settings.Theme);
        Assert.False(service.LoadFailed);
    }

    [Fact]
    public void 起動時にファイルが壊れていれば_既定値で始め_そのことがわかる()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsFile, "{");

        var service = new SettingsService(new SettingsStore(SettingsFile), NullLogger<SettingsService>.Instance);

        Assert.True(service.LoadFailed);
        Assert.Equal(new AppSettings(), service.Current);
    }

    [Theory]
    [InlineData("0.2.0+0372b9bc00ff87655b456c76b9642d6ffc688541", "0.2.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.3", "0.3.0")]
    [InlineData("0.2.0-beta.1", "0.2.0")]
    [InlineData("不正な版", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void 版の文字列から_比べられる版を取り出す(string? text, string expected)
    {
        Assert.Equal(Version.Parse(expected), AppInfo.ParseVersion(text));
    }
}
