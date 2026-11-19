using K35Viewer.Domain;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;

namespace K35Viewer.Tests.Services;

/// <summary>
/// 最近開いたアーカイブの一覧（GitHub の Issue #27）と、終了時の状態を次の起動で戻す機能（Issue #26）。
/// </summary>
public class RecentAndRestoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    private static Task<long> IdOfAsync(LibraryTestContext ctx, string fileName) =>
        ctx.ScalarAsync($"SELECT id FROM archives WHERE file_name = '{fileName}'");

    private static async Task<IReadOnlyList<string>> RecentNamesAsync(LibraryTestContext ctx, int? limit = null)
    {
        var result = await ctx.Library.QueryArchivesAsync(new ArchiveQuery(Limit: limit, RecentOnly: true), Ct);
        return [.. result.Items.Select(a => a.FileName)];
    }

    // ---- 最近開いたアーカイブ（Issue #27） ----

    [Fact]
    public async Task アーカイブを開くと_最後に開いた日時が記録され_開けなかったものは記録されない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = ctx.CreateFile("broken.zip", size: 64); // ZIP ではない
        _ = await ctx.RegisterAndScanAsync();
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE last_opened_at IS NOT NULL"));

        var opened = await ctx.Viewer.OpenAsync(await IdOfAsync(ctx, "a.zip"), Ct);
        Assert.True(opened.Succeeded);
        opened.Session!.Dispose();
        var failed = await ctx.Viewer.OpenAsync(await IdOfAsync(ctx, "broken.zip"), Ct);
        Assert.False(failed.Succeeded);

        Assert.Equal(["a.zip"], await RecentNamesAsync(ctx));
        Assert.True(await ctx.ScalarAsync("SELECT last_opened_at FROM archives WHERE file_name = 'a.zip'") > 0);
    }

    [Fact]
    public async Task 最近開いた一覧は_開いたものだけを_新しい順に_指定した件数まで返す()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        for (var i = 1; i <= 12; i++)
        {
            ctx.CreateZip($"book{i:D2}.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        }

        _ = await ctx.RegisterAndScanAsync();
        // book01 ～ book11 を、番号の順に開いた（book12 は開いていない）。番号が大きいほど新しい
        for (var i = 1; i <= 11; i++)
        {
            await ctx.ExecuteAsync($"UPDATE archives SET last_opened_at = {1000 + i} WHERE file_name = 'book{i:D2}.zip'");
        }

        var recent = await RecentNamesAsync(ctx, limit: 10);
        Assert.Equal(10, recent.Count);
        Assert.Equal("book11.zip", recent[0]);
        Assert.Equal("book02.zip", recent[^1]); // 11 件のうち、いちばん古い book01 は、件数の上限で外れる
        Assert.DoesNotContain("book12.zip", await RecentNamesAsync(ctx));
    }

    [Fact]
    public async Task 履歴を消すと_最後に開いた日時だけが消え_閲覧位置は残る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(50, 50)), ("2.jpg", TestImages.Jpeg(50, 50)));
        ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(50, 50)));
        _ = await ctx.RegisterAndScanAsync();
        await ctx.ExecuteAsync("UPDATE archives SET last_opened_at = 10, last_view_entry = '2.jpg', last_view_index = 1 WHERE file_name = 'a.zip'");
        await ctx.ExecuteAsync("UPDATE archives SET last_opened_at = 20 WHERE file_name = 'b.zip'");

        var cleared = await ctx.Library.ClearRecentAsync(Ct);

        Assert.Equal(2, cleared);
        Assert.Empty(await RecentNamesAsync(ctx));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal("2.jpg", await ctx.StringAsync("SELECT last_view_entry FROM archives WHERE file_name = 'a.zip'"));
    }

    [Fact]
    public async Task 画面の一覧を読み込み_項目を選ぶとビューアで開き_履歴を消すと空になる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        await ctx.ExecuteAsync("UPDATE archives SET last_opened_at = 100 WHERE file_name = 'a.zip'");
        await ctx.ExecuteAsync("UPDATE archives SET last_opened_at = 200 WHERE file_name = 'c.zip'");

        await harness.Library.LoadRecentAsync();
        Assert.Equal(["c.zip", "a.zip"], harness.Library.RecentArchives.Select(a => a.FileName));
        Assert.True(harness.Library.HasRecentArchives);
        Assert.False(string.IsNullOrEmpty(harness.Library.RecentArchives[0].LastOpenedText));

        harness.Library.IsRecentOpen = true;
        harness.Library.OpenRecentCommand.Execute(harness.Library.RecentArchives[1]); // a.zip
        Assert.False(harness.Library.IsRecentOpen); // 一覧は閉じる
        await WaitUntilAsync(() => harness.Main.IsViewerVisible && harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        Assert.Equal("a.zip", harness.Viewer.ArchiveName);
        await harness.Viewer.CloseAsync();

        await harness.Library.LoadRecentAsync();
        Assert.Equal("a.zip", harness.Library.RecentArchives[0].FileName); // 開き直したので、先頭になる

        harness.Library.ClearRecentCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Library.HasRecentArchives);
        Assert.Empty(await RecentNamesAsync(ctx));
    }

    // ---- ウィンドウの位置と大きさ（Issue #26） ----

    [Theory]
    [InlineData(100, 100, 1280, 800, true)]
    [InlineData(-1900, 100, 1280, 800, true)] // 左にあるモニター（仮想スクリーンの内側）
    [InlineData(5000, 100, 1280, 800, false)] // 外したモニターの位置
    [InlineData(100, -300, 1280, 800, false)] // タイトルバーが画面の上に出ている
    [InlineData(100, 100, 100, 100, false)] // 小さすぎる
    [InlineData(double.NaN, 100, 1280, 800, false)]
    public void ウィンドウの位置は_画面の中にあるときだけ使える(double left, double top, double width, double height, bool expected)
    {
        var placement = new WindowPlacement(left, top, width, height, IsMaximized: false);
        // 仮想スクリーン: 左のモニター(-1920 ～ 0)と、メインのモニター(0 ～ 1920)の、横に並んだ 3840 x 1080
        Assert.Equal(expected, placement.IsUsable(-1920, 0, 3840, 1080));
    }

    [Fact]
    public void 画面の右端に少しだけかかる位置は_使えない()
    {
        var placement = new WindowPlacement(1850, 100, 1280, 800, IsMaximized: false); // 画面に入るのは 70
        Assert.False(placement.IsUsable(0, 0, 1920, 1080));
        Assert.True(new WindowPlacement(1800, 100, 1280, 800, IsMaximized: false).IsUsable(0, 0, 1920, 1080)); // 120 入る
    }

    [Fact]
    public void 終了時の状態の設定は_保存して読み直せ_古いファイルでは既定値になる()
    {
        var directory = Path.Combine(Path.GetTempPath(), "K35ViewerTests", "restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var saved = new AppSettings
            {
                MainWindowPlacement = new WindowPlacement(10.5, 20, 1000, 700, IsMaximized: true),
                LibrarySortKey = ArchiveSortKey.FileSize,
                LibrarySortDescending = true,
                LibraryShowNewOnly = true,
                ResumeLastArchive = true,
                LastOpenArchiveId = 42,
            };
            store.Save(saved);
            Assert.Equal(saved, store.Load().Settings);

            File.WriteAllText(store.FilePath, "{ \"Theme\": \"Light\" }"); // これらの項目がない、古い版のファイル
            var old = store.Load().Settings;
            Assert.Equal(AppTheme.Light, old.Theme);
            Assert.Null(old.MainWindowPlacement);
            Assert.Equal(ArchiveSortKey.FileName, old.LibrarySortKey);
            Assert.False(old.LibrarySortDescending);
            Assert.False(old.LibraryShowNewOnly);
            Assert.False(old.ResumeLastArchive);
            Assert.Null(old.LastOpenArchiveId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- 一覧の並べ替えと絞り込み（Issue #26） ----

    [Fact]
    public async Task 一覧の並べ替えと絞り込みは_設定へ記録され_次の起動で戻る()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { LibrarySortKey = ArchiveSortKey.FileName, LibrarySortDescending = true });
        using var harness = new MainTestHarness(ctx, settings: settings);
        await ctx.InitializeAsync();
        foreach (var name in new[] { "a.zip", "b.zip", "c.zip" })
        {
            ctx.CreateZip(name, ("1.jpg", TestImages.Jpeg(100, 80)));
        }

        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();

        // 前回の並べ替え（ファイル名の降順）で始まる
        Assert.True(harness.Library.IsDescending);
        Assert.Equal(["c.zip", "b.zip", "a.zip"], harness.Library.Archives.Select(a => a.FileName));

        // 変えると、設定へ記録する
        harness.Library.SelectedSortOption = harness.Library.SortOptions.Single(o => o.Key == ArchiveSortKey.FileSize);
        harness.Library.IsDescending = false;
        harness.Library.ShowNewOnly = true;
        Assert.Equal(ArchiveSortKey.FileSize, settings.Current.LibrarySortKey);
        Assert.False(settings.Current.LibrarySortDescending);
        Assert.True(settings.Current.LibraryShowNewOnly);

        // 新しい画面（次の起動）は、その並べ替えと絞り込みで始まる
        using var next = new MainTestHarness(ctx, settings: ctx.CreateSettings());
        await next.Library.InitializeAsync();
        Assert.Equal(ArchiveSortKey.FileSize, next.Library.SelectedSortOption.Key);
        Assert.False(next.Library.IsDescending);
        Assert.True(next.Library.ShowNewOnly);
    }

    // ---- 終了時に開いていたアーカイブ（Issue #26） ----

    [Fact]
    public async Task 終了時の状態として_ビューアで開いていたアーカイブとウィンドウの位置が記録される()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx, settings);
        var placement = new WindowPlacement(30, 40, 900, 600, IsMaximized: false);

        // ライブラリ画面で終了: アーカイブは記録しない
        harness.Main.SaveExitState(placement);
        Assert.Null(settings.Current.LastOpenArchiveId);
        Assert.Equal(placement, settings.Current.MainWindowPlacement);

        // ビューアで終了: 開いているアーカイブを記録する
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single(a => a.FileName == "b.zip"));
        await WaitUntilAsync(() => harness.Main.IsViewerVisible && harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        harness.Main.SaveExitState(null); // ウィンドウの位置が取れなくても、前回の値を消さない
        Assert.Equal(await IdOfAsync(ctx, "b.zip"), settings.Current.LastOpenArchiveId);
        Assert.Equal(placement, settings.Current.MainWindowPlacement);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 続きから開く設定がオンなら_前回開いていたアーカイブを開き_記録は消す()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        using var harness = new MainTestHarness(ctx, settings: settings);
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(100, 80)));
        _ = await ctx.RegisterAndScanAsync();
        var bId = await IdOfAsync(ctx, "b.zip");
        settings.Update(s => s with { ResumeLastArchive = true, LastOpenArchiveId = bId });

        await harness.Main.ResumeLastArchiveAsync();
        await WaitUntilAsync(() => harness.Main.IsViewerVisible && harness.Viewer.IsOpen && !harness.Main.IsTransitioning);

        Assert.Equal("b.zip", harness.Viewer.ArchiveName);
        Assert.Null(settings.Current.LastOpenArchiveId);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 続きから開く設定がオフなら_前回開いていたアーカイブがあっても_ライブラリ画面で始まる()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        using var harness = new MainTestHarness(ctx, settings: settings);
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = await ctx.RegisterAndScanAsync();
        var aId = await IdOfAsync(ctx, "a.zip");
        settings.Update(s => s with { ResumeLastArchive = false, LastOpenArchiveId = aId });

        await harness.Main.ResumeLastArchiveAsync();
        await harness.WaitForIdleAsync();

        Assert.False(harness.Main.IsViewerVisible);
        Assert.False(harness.Viewer.IsOpen);
    }

    [Fact]
    public async Task 前回開いていたアーカイブが見つからなければ_通知してライブラリ画面で始まる()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        using var harness = new MainTestHarness(ctx, settings: settings);
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        _ = await ctx.RegisterAndScanAsync();
        settings.Update(s => s with { ResumeLastArchive = true, LastOpenArchiveId = 9999 });

        await harness.Main.ResumeLastArchiveAsync();
        await WaitUntilAsync(() => !harness.Main.IsTransitioning);

        Assert.False(harness.Main.IsViewerVisible);
        Assert.Null(settings.Current.LastOpenArchiveId);
    }
}
