using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using K35Viewer.Domain;
using K35Viewer.Services;
using K35Viewer.ViewModels;
using K35Viewer.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>
/// 実際のWPFウィンドウに1万件を表示し、一覧の仮想化が効いて「画面に見える行だけ」が生成されることを確認する。
/// </summary>
public class LibraryViewVirtualizationTests
{
    private sealed class NoDialogs : IDialogService
    {
        public string? PickFolder() => null;

        public bool ConfirmMerge(string parentPath, IReadOnlyList<string> childPaths) => false;

        public LeaveChoice ConfirmLeaveArchive(string archiveName, int pendingDeleteCount, bool canUpdate) => LeaveChoice.Cancel;
    }

    private static void RunOnStaThread(Action action) => WpfTestHelper.RunOnStaThread(action);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject => WpfTestHelper.Descendants<T>(root);

    [Fact]
    public async Task 一覧は1万件を渡しても_画面に見える行だけを生成する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = new LibraryViewModel(
            ctx.Library, ctx.Scan, ctx.Thumbnails, ctx.ImageLoader, new NoDialogs(), NullLogger<LibraryViewModel>.Instance);
        await viewModel.InitializeAsync(); // 画面表示時の初期化を済ませ、後から一覧が上書きされないようにする

        var items = Enumerable.Range(0, 10_000)
            .Select(i => new ArchiveListItem(new ArchiveSummary(
                i + 1, $"書籍_{i:D5}.zip", @"D:\Archive\Books", 1_000_000 + i, 1_700_000_000 + (i * 60), null, "PENDING")))
            .ToList();
        viewModel.Archives = items;
        viewModel.TotalCount = items.Count;
        viewModel.EmptyMessage = string.Empty;

        var realizedRows = 0;
        var firstShowMs = 0L;
        var listViewFound = false;

        RunOnStaThread(() =>
        {
            var view = new LibraryView { DataContext = viewModel };
            var window = new Window { Width = 1280, Height = 800, Content = view };

            var stopwatch = Stopwatch.StartNew();
            window.Show();
            window.UpdateLayout();
            firstShowMs = stopwatch.ElapsedMilliseconds;

            var listView = Descendants<ListView>(view)
                .FirstOrDefault(l => AutomationProperties.GetName(l) == "アーカイブ一覧");
            listViewFound = listView is not null;
            if (listView is not null)
            {
                realizedRows = Descendants<ListViewItem>(listView).Count();
            }

            window.Close();
        });

        TestContext.Current.SendDiagnosticMessage(
            $"WPF一覧: 1万件を表示してレイアウト完了まで {firstShowMs}ms / 生成された行 {realizedRows}件");
        Assert.True(listViewFound, "アーカイブ一覧が見つかりません。");
        Assert.InRange(realizedRows, 1, 200); // 1万件ではなく、画面に見える数十行だけ
        // 仮想化の可否は上の行数で確認する。時間は他のテストとの並列実行で大きく揺れるため、桁違いの遅さだけを検知する
        Assert.True(firstShowMs < 30_000, $"表示が遅すぎます: {firstShowMs}ms");
    }

    [Fact]
    public async Task サムネイル表示も1万件を渡して_画面に見える行の画像枠だけを生成する()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        using var viewModel = new LibraryViewModel(
            ctx.Library, ctx.Scan, ctx.Thumbnails, ctx.ImageLoader, new NoDialogs(), NullLogger<LibraryViewModel>.Instance);
        await viewModel.InitializeAsync();

        var items = Enumerable.Range(0, 10_000)
            .Select(i => new ArchiveListItem(new ArchiveSummary(
                i + 1, $"書籍_{i:D5}.zip", @"D:\Archive\Books", 1_000_000 + i, 1_700_000_000 + (i * 60), 20, "READY"), ctx.ImageLoader))
            .ToList();
        viewModel.Archives = items;
        viewModel.TotalCount = items.Count;
        viewModel.EmptyMessage = string.Empty;
        viewModel.IsThumbnailView = true;

        var realizedRows = 0;
        var realizedCells = 0;
        var totalRows = 0;
        var showMs = 0L;
        var found = false;

        RunOnStaThread(() =>
        {
            var view = new LibraryView { DataContext = viewModel };
            var window = new Window { Width = 1280, Height = 800, Content = view };

            var stopwatch = Stopwatch.StartNew();
            window.Show();
            window.UpdateLayout();
            showMs = stopwatch.ElapsedMilliseconds;

            var list = Descendants<ListBox>(view)
                .FirstOrDefault(l => AutomationProperties.GetName(l) == "アーカイブのサムネイル一覧");
            found = list is not null;
            if (list is not null)
            {
                realizedRows = Descendants<ListBoxItem>(list).Count();
                realizedCells = Descendants<Image>(list).Count();
                totalRows = viewModel.ThumbnailRows.Count;
            }

            window.Close();
        });

        TestContext.Current.SendDiagnosticMessage(
            $"WPFサムネイル表示: 1万件を表示してレイアウト完了まで {showMs}ms / 全{totalRows}行のうち生成された行 {realizedRows}行・{realizedCells}枚");
        Assert.True(found, "サムネイル一覧が見つかりません。");
        Assert.True(totalRows >= 1_000, $"行にまとめられていません: {totalRows}行");
        Assert.InRange(realizedRows, 1, 100); // 全行ではなく、画面に見える数行だけ
        Assert.InRange(realizedCells, 1, 400);
        Assert.True(showMs < 30_000, $"表示が遅すぎます: {showMs}ms");
    }
}
