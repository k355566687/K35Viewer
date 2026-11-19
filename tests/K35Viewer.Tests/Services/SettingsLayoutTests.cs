using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using K35Viewer.Services;
using K35Viewer.Views;

namespace K35Viewer.Tests.Services;

/// <summary>設定画面の配置（GitHub の Issue #25）。項目は、3つのタブ（全般、データ、アプリ）に分かれ、関係する項目は枠で囲ってある。</summary>
[Collection(WpfViewsGroup.Name)]
public sealed class SettingsLayoutTests
{
    private sealed class NoCacheMaintenance : IThumbnailCacheMaintenance
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public string? ClearBlockedReason => null;

        public Task<long> GetCacheSizeAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<bool> ClearCacheAsync() => Task.FromResult(true);
    }

    /// <summary>設定画面を開き、タブごとに、画面にある項目の名前（スクリーンリーダー向けの名前）を集める。</summary>
    private static (IReadOnlyList<string> Headers, Dictionary<string, HashSet<string>> NamesByTab) Inspect()
    {
        using var ctx = new LibraryTestContext();
        var viewModel = ctx.CreateSettingsViewModel(ctx.CreateSettings(), new NoCacheMaintenance());
        IReadOnlyList<string> headers = [];
        var names = new Dictionary<string, HashSet<string>>();
        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new SettingsWindow(viewModel));
            window.Show();
            WpfTestHelper.Pump();
            var tabs = WpfTestHelper.Descendants<TabControl>(window).Single();
            headers = [.. tabs.Items.OfType<TabItem>().Select(t => (string)t.Header)];
            foreach (var item in tabs.Items.OfType<TabItem>())
            {
                tabs.SelectedItem = item;
                WpfTestHelper.Pump();
                names[(string)item.Header] =
                [
                    .. WpfTestHelper.Descendants<FrameworkElement>(tabs)
                        .Select(AutomationProperties.GetName)
                        .Where(n => !string.IsNullOrEmpty(n)),
                ];
            }

            window.Close();
        });
        return (headers, names);
    }

    [Fact]
    public void 設定は_全般_データ_アプリの3つのタブに分かれる()
    {
        var (headers, _) = Inspect();

        Assert.Equal(["全般", "データ", "アプリ"], headers);
    }

    [Fact]
    public void 項目は_決めたタブにあり_ほかのタブにはない()
    {
        var (_, names) = Inspect();

        // 全般: 外観、確認
        Assert.Contains("テーマ: ダーク", names["全般"]);
        Assert.Contains("アーカイブから離れるときの確認", names["全般"]);
        Assert.Contains("アーカイブを削除するときの確認", names["全般"]);
        // データ: 保存場所、サムネイルのキャッシュ、バックアップと復元
        Assert.Contains("アプリのデータの保存場所", names["データ"]);
        Assert.Contains("サムネイルのキャッシュを削除", names["データ"]);
        Assert.Contains("バックアップを作成", names["データ"]);
        Assert.Contains("バックアップから復元", names["データ"]);
        Assert.Contains("自動バックアップを残す数", names["データ"]);
        // アプリ: ショートカット、アプリの更新、情報
        Assert.Contains("スタートメニューにショートカットを作る", names["アプリ"]);
        Assert.Contains("デスクトップにショートカットを作る", names["アプリ"]);
        Assert.Contains("更新を確認", names["アプリ"]);
        Assert.Contains("新しい版へ更新する", names["アプリ"]);
        Assert.Contains("ライセンスのページを開く", names["アプリ"]);
        Assert.Contains("アンインストールする", names["アプリ"]);

        Assert.DoesNotContain("バックアップを作成", names["全般"]);
        Assert.DoesNotContain("テーマ: ダーク", names["データ"]);
        Assert.DoesNotContain("更新を確認", names["データ"]);
        Assert.DoesNotContain("バックアップを作成", names["アプリ"]);
        Assert.DoesNotContain("アンインストールする", names["データ"]);
    }

    [Fact]
    public void 関係する項目は_名前のついた枠で囲ってある()
    {
        var (_, names) = Inspect();

        Assert.Contains("外観", names["全般"]);
        Assert.Contains("確認", names["全般"]);
        Assert.Contains("保存場所とキャッシュ", names["データ"]);
        Assert.Contains("バックアップ", names["データ"]);
        Assert.Contains("ショートカット", names["アプリ"]);
        Assert.Contains("アプリの更新", names["アプリ"]);
        Assert.Contains("アンインストール", names["アプリ"]);
        Assert.Contains("情報", names["アプリ"]);
    }
}