using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using K35Viewer.Views;

namespace K35Viewer.Tests.Services;

/// <summary>
/// 画面の操作（離れるときの確認で「更新する」を選ぶ）から、ZIPの更新、ライブラリ画面の更新までの流れ（UC-11）。
/// </summary>
[Collection(WpfViewsGroup.Name)]
public class ArchiveUpdateFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] EntryNames(string zipPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>1.jpg〜3.jpg（幅 110・120・130）を持つZIPを登録し、ビューアで開いた状態にする。</summary>
    private static async Task<MainTestHarness> OpenInMainAsync(LibraryTestContext ctx, ArchiveService? archives = null, SettingsService? settings = null)
    {
        var harness = new MainTestHarness(ctx, archives, settings);
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            ("1.jpg", TestImages.Jpeg(110, 80)), ("2.jpg", TestImages.Jpeg(120, 80)), ("3.jpg", TestImages.Jpeg(130, 80)));
        await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives[0]);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        return harness;
    }

    [Fact]
    public async Task 更新するを選ぶと_ZIPが更新されてライブラリへ戻り_サムネイルが作り直される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        var original = Hash(ctx.PathOf("book.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null); // 1.jpg を削除予約 → 2枚目へ
        Assert.Equal("2 / 3", harness.Viewer.PositionText);
        harness.Dialogs.Choice = LeaveChoice.Update;

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);

        var confirmation = Assert.Single(harness.Dialogs.LeaveConfirmations);
        Assert.Equal(("book.zip", 1, (string?)null), confirmation);
        Assert.True(harness.Main.IsLibraryVisible);
        Assert.False(harness.Main.IsUpdating);
        Assert.Equal(["2.jpg", "3.jpg"], EntryNames(ctx.PathOf("book.zip")));
        Assert.Equal(original, Hash(Assert.Single(ctx.RecycleBin.Items)));
        Assert.Empty(Directory.GetFiles(ctx.Workspace, "*" + FileSystemService.TempFileSuffix));

        // 更新後の閲覧位置は、更新後のアーカイブでの位置。閉じる処理が、古い位置で上書きしない
        await Task.Delay(1200, Ct); // 閲覧位置の保存を待つ時間（0.8秒）より長く待つ
        Assert.Equal("2.jpg", await ctx.StringAsync("SELECT last_view_entry FROM archives"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT last_view_index FROM archives"));

        // ライブラリ画面の更新: サムネイルが作り直され、画像枚数と表紙が新しい内容になる
        await harness.WaitForIdleAsync();
        await WaitUntilAsync(() => harness.Library.Archives[0].ImageCountText == "2");
        Assert.Equal("READY", await ctx.StringAsync("SELECT analysis_status FROM archives"));
        Assert.Equal("2.jpg", await ctx.StringAsync("SELECT first_image_entry FROM archives"));
        Assert.Equal("READY", await ctx.StringAsync("SELECT status FROM thumbnail_cache"));

        // 開き直すと、更新後のアーカイブを前回の位置から開く
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives[0]);
        await WaitUntilAsync(() => harness.Main.IsViewerVisible);
        Assert.Equal("1 / 2", harness.Viewer.PositionText);
        Assert.Equal(0, harness.Viewer.PendingDeleteCount);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 更新に失敗したら_ビューアに留まり_削除予約を保持して_やり直せる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        var original = Hash(ctx.PathOf("book.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        ctx.RecycleBin.FailToMove = true;
        ctx.Notifications.Items.Clear();
        harness.Dialogs.Choice = LeaveChoice.Update;

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => ctx.Notifications.Items.Any(n => n.Level == NotificationLevel.Error));
        await WaitUntilAsync(() => !harness.Main.IsUpdating);

        Assert.True(harness.Viewer.IsOpen);
        Assert.True(harness.Main.IsViewerVisible);
        Assert.Equal(1, harness.Viewer.PendingDeleteCount);
        Assert.Equal(original, Hash(ctx.PathOf("book.zip")));
        Assert.Empty(ctx.RecycleBin.Items);

        // 原因が解消したら、もう一度「更新する」を選べる
        ctx.RecycleBin.FailToMove = false;
        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);

        Assert.Equal(2, harness.Dialogs.LeaveConfirmations.Count);
        Assert.Equal(["2.jpg", "3.jpg"], EntryNames(ctx.PathOf("book.zip")));
        Assert.Single(ctx.RecycleBin.Items);

        // 更新後にライブラリ画面で始まるサムネイルの再生成が終わるのを待つ（実行中にテストを終えない）
        await WaitUntilAsync(() => harness.Library.Archives[0].ImageCountText == "2");
        await harness.WaitForIdleAsync();
    }

    [Fact]
    public async Task ごみ箱を使えない保存先では_更新するを選べず_ビューアに留まる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        ctx.RecycleBin.Unavailable = "ネットワーク上の保存先のため、ごみ箱を使えません";
        harness.Dialogs.Choice = LeaveChoice.Update; // 選べないので、無効な選択として扱われる

        harness.Viewer.RequestCloseCommand.Execute(null);
        await Task.Delay(300, Ct);

        var confirmation = Assert.Single(harness.Dialogs.LeaveConfirmations);
        Assert.Contains("ごみ箱を使えません", confirmation.UpdateUnavailableReason, StringComparison.Ordinal);
        Assert.True(harness.Viewer.IsOpen);
        Assert.Equal(1, harness.Viewer.PendingDeleteCount);
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);

        // 「変更を破棄」なら離れられる（ZIPは変更されない）
        harness.Dialogs.Choice = LeaveChoice.Discard;
        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen);
        Assert.Equal(["1.jpg", "2.jpg", "3.jpg"], EntryNames(ctx.PathOf("book.zip")));
    }

    [Fact]
    public async Task 外部で変更されたアーカイブは_更新するを選べず_ウィンドウが前面になると1回だけ通知される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        var shown = new List<Notification>();
        harness.Notifications.Notified += (_, n) => shown.Add(n);

        harness.Main.OnWindowActivated();
        Assert.Empty(shown); // 変更されていなければ通知しない

        ctx.CreateZip("book.zip", ("x.jpg", TestImages.Jpeg(50, 50))); // 外部で書き換えられた
        harness.Main.OnWindowActivated();
        harness.Main.OnWindowActivated();

        var warning = Assert.Single(shown);
        Assert.Equal(NotificationLevel.Warning, warning.Level);
        Assert.Contains("外部で変更されました", warning.Message, StringComparison.Ordinal);

        harness.Dialogs.Choice = LeaveChoice.Cancel;
        Assert.Equal(LeaveDecision.Stay, harness.Main.DecideLeave());
        Assert.Contains("外部で変更", harness.Dialogs.LeaveConfirmations[^1].UpdateUnavailableReason, StringComparison.Ordinal);
        await harness.Viewer.CloseAsync();
    }

    // ---- 離れるときの確認をオフにした場合（要件定義 24.3） ----

    [Fact]
    public async Task 離れるときの確認をオフにすると_確認せずに更新して離れる()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { ConfirmBeforeLeave = false });
        using var harness = await OpenInMainAsync(ctx, settings: settings);
        var original = Hash(ctx.PathOf("book.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null); // 1.jpg を削除予約
        harness.Dialogs.Choice = LeaveChoice.Cancel; // 確認が出たら、キャンセルになる

        harness.Viewer.RequestCloseCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Viewer.IsOpen && harness.Main.IsLibraryVisible);

        Assert.Empty(harness.Dialogs.LeaveConfirmations);
        Assert.Equal(["2.jpg", "3.jpg"], EntryNames(ctx.PathOf("book.zip")));
        Assert.NotEqual(original, Hash(ctx.PathOf("book.zip")));
        Assert.Single(ctx.RecycleBin.Items); // 更新前のアーカイブは、オンのときと同じくごみ箱へ退避する
    }

    [Fact]
    public async Task 離れるときの確認をオフにしても_削除予約がなければ何も起きない()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { ConfirmBeforeLeave = false });
        using var harness = await OpenInMainAsync(ctx, settings: settings);

        Assert.Equal(LeaveDecision.Proceed, harness.Main.DecideLeave());
        Assert.Empty(harness.Dialogs.LeaveConfirmations);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 離れるときの確認をオフにしても_更新できない場合は確認する()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { ConfirmBeforeLeave = false });
        using var harness = await OpenInMainAsync(ctx, settings: settings);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        ctx.CreateZip("book.zip", ("x.jpg", TestImages.Jpeg(50, 50))); // 外部で書き換えられた
        harness.Dialogs.Choice = LeaveChoice.Discard;

        Assert.Equal(LeaveDecision.Proceed, harness.Main.DecideLeave()); // 「変更を破棄」を選んだ

        var confirmation = Assert.Single(harness.Dialogs.LeaveConfirmations);
        Assert.Contains("外部で変更", confirmation.UpdateUnavailableReason, StringComparison.Ordinal);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task 離れるときの確認をオフにして更新をキャンセルしたあとは_次に離れるとき確認する()
    {
        using var ctx = new LibraryTestContext();
        using var started = new ManualResetEventSlim();
        var writer = new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, destination, _, ct) =>
            {
                File.WriteAllText(destination, "書きかけ");
                started.Set();
                _ = ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                ct.ThrowIfCancellationRequested();
                return null;
            },
        };
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { ConfirmBeforeLeave = false });
        using var harness = await OpenInMainAsync(ctx, ctx.CreateArchiveService(writer), settings);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Cancel;

        harness.Viewer.RequestCloseCommand.Execute(null); // 確認なしで更新が始まる
        Assert.True(started.Wait(TimeSpan.FromSeconds(15), Ct));
        await WaitUntilAsync(() => harness.Main.IsUpdating);
        harness.Main.CancelUpdateCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Main.IsUpdating);
        Assert.Empty(harness.Dialogs.LeaveConfirmations);
        Assert.True(harness.Viewer.IsOpen);

        // 失敗する更新を確認なしで繰り返さないよう、今度は確認を表示する
        Assert.Equal(LeaveDecision.Stay, harness.Main.DecideLeave());
        Assert.Single(harness.Dialogs.LeaveConfirmations);
        await harness.Viewer.CloseAsync();
    }
    [Fact]
    public async Task 更新中は画面の操作を受け付けず_キャンセルできる間はキャンセルでき_元のアーカイブと削除予約が残る()
    {
        using var ctx = new LibraryTestContext();
        using var started = new ManualResetEventSlim();
        var writer = new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, destination, _, ct) =>
            {
                File.WriteAllText(destination, "書きかけ");
                started.Set();
                _ = ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                ct.ThrowIfCancellationRequested();
                return null;
            },
        };
        using var harness = await OpenInMainAsync(ctx, ctx.CreateArchiveService(writer));
        var original = Hash(ctx.PathOf("book.zip"));
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Update;

        harness.Viewer.RequestCloseCommand.Execute(null);
        Assert.True(started.Wait(TimeSpan.FromSeconds(15), Ct));
        await WaitUntilAsync(() => harness.Main.IsUpdating);
        await WaitUntilAsync(() => harness.Main.UpdateStatusText.Contains("作成", StringComparison.Ordinal));

        Assert.True(harness.Main.CanCancelUpdate);
        Assert.True(harness.Main.CancelUpdateCommand.CanExecute(null));
        harness.Viewer.RequestCloseCommand.Execute(null); // 更新中の閉じる操作は受け付けない
        Assert.Single(harness.Dialogs.LeaveConfirmations);

        harness.Main.CancelUpdateCommand.Execute(null);
        await WaitUntilAsync(() => !harness.Main.IsUpdating);

        Assert.True(harness.Viewer.IsOpen);
        Assert.True(harness.Main.IsViewerVisible);
        Assert.Equal(1, harness.Viewer.PendingDeleteCount);
        Assert.Equal(original, Hash(ctx.PathOf("book.zip")));
        Assert.Empty(Directory.GetFiles(ctx.Workspace, "*" + FileSystemService.TempFileSuffix));
        Assert.Empty(ctx.RecycleBin.Items);
        Assert.Equal(string.Empty, harness.Main.UpdateStatusText);
        Assert.False(harness.Main.CanCancelUpdate);
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task ウィンドウを閉じるときに更新するを選ぶと_更新が終わってからウィンドウが閉じる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenInMainAsync(ctx);
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Update;
        var closed = false;
        var visibleAfterFirstClose = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new MainWindow(harness.Main));
            harness.Main.UiContext = new DispatcherSynchronizationContext(window.Dispatcher); // 進行状況の表示を、ウィンドウのスレッドへ届ける
            window.Show();
            WpfTestHelper.Pump();

            window.Close(); // すぐには閉じず、更新してから閉じる
            visibleAfterFirstClose = window.IsVisible;
            WpfTestHelper.PumpUntil(() => !window.IsVisible, seconds: 20);
            closed = !window.IsVisible;
        });

        Assert.True(visibleAfterFirstClose);
        Assert.True(closed);
        Assert.Equal(["2.jpg", "3.jpg"], EntryNames(ctx.PathOf("book.zip")));
        Assert.Single(ctx.RecycleBin.Items);
        await Task.Delay(1200, Ct);
        Assert.Equal("2.jpg", await ctx.StringAsync("SELECT last_view_entry FROM archives")); // 閉じるときの保存で上書きされない
    }

    [Fact]
    public async Task 更新中のウィンドウを閉じようとしても_更新が終わるまで閉じない()
    {
        using var ctx = new LibraryTestContext();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, _, _, ct) =>
            {
                started.Set();
                release.Wait(ct);
                return null;
            },
        };
        using var harness = await OpenInMainAsync(ctx, ctx.CreateArchiveService(writer));
        harness.Viewer.ToggleDeleteCommand.Execute(null);
        harness.Dialogs.Choice = LeaveChoice.Update;
        var visibleWhileUpdating = false;
        var closedAfterUpdate = false;

        WpfTestHelper.RunOnStaThread(() =>
        {
            var window = WpfTestHelper.WithAppTheme(new MainWindow(harness.Main));
            harness.Main.UiContext = new DispatcherSynchronizationContext(window.Dispatcher); // 進行状況の表示を、ウィンドウのスレッドへ届ける
            window.Show();
            WpfTestHelper.Pump();

            harness.Viewer.RequestCloseCommand.Execute(null); // 更新を始める
            WpfTestHelper.PumpUntil(() => harness.Main.IsUpdating && started.IsSet);
            window.Close(); // 更新中の終了要求
            WpfTestHelper.Pump();
            visibleWhileUpdating = window.IsVisible;

            release.Set();
            WpfTestHelper.PumpUntil(() => !window.IsVisible, seconds: 20);
            closedAfterUpdate = !window.IsVisible;
        });

        Assert.True(visibleWhileUpdating);
        Assert.True(closedAfterUpdate);
        Assert.Equal(["2.jpg", "3.jpg"], EntryNames(ctx.PathOf("book.zip")));
        Assert.Single(harness.Dialogs.LeaveConfirmations); // 更新中の終了要求では、確認を重ねて表示しない
    }
}
