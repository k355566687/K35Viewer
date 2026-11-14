using System.ComponentModel;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace K35Viewer.Tests.Services;

/// <summary>サムネイルのキャッシュの大きさの表示と削除（要件定義 24.5）と、確認のオン/オフ（24.3）の設定項目。</summary>
public class ThumbnailCacheClearTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SettingsViewModel CreateViewModel(
        LibraryTestContext ctx, FakeThumbnailCacheMaintenance cache, SettingsService? settings = null) =>
        new(settings ?? ctx.CreateSettings(), new AppPaths(ctx.CacheDirectory), ctx.Explorer, cache, ctx.Notifications,
            NullLogger<SettingsViewModel>.Instance);

    [Fact]
    public void キャッシュの削除は_サムネイルのファイルだけを消し_同じフォルダのほかのファイルには触れない()
    {
        using var ctx = new LibraryTestContext();
        var cache = ctx.Cache;
        _ = cache.Save(1, new byte[100]);
        _ = cache.Save(1500, new byte[200]); // 別のサブフォルダ
        var other = Path.Combine(ctx.CacheDirectory, "settings.json");
        File.WriteAllText(other, "{}");
        var keep = Path.Combine(ctx.CacheDirectory, "0000", "keep.txt");
        File.WriteAllText(keep, "x");

        Assert.Equal(300, cache.GetTotalBytes());
        var deleted = cache.Clear();

        Assert.Equal(2, deleted);
        Assert.Equal(0, cache.GetTotalBytes());
        Assert.False(cache.Exists(ThumbnailCacheStoreHelper.Relative(1)));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(keep)); // サムネイルではないファイルは残り、そのフォルダも残る
        Assert.False(Directory.Exists(Path.Combine(ctx.CacheDirectory, "0001"))); // 空になったフォルダは消える
    }

    [Fact]
    public void キャッシュのフォルダがなくても_大きさは0で_削除は何もしない()
    {
        using var ctx = new LibraryTestContext();

        Assert.Equal(0, ctx.Cache.GetTotalBytes());
        Assert.Equal(0, ctx.Cache.Clear());
    }

    [Fact]
    public async Task キャッシュを削除すると_記録とファイルが消え_次の作成ですべて作り直される()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        ctx.CreateZip("a.zip", ("1.jpg", TestImages.Jpeg(100, 80)));
        ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(110, 80)));
        _ = await ctx.RegisterAndScanAsync();
        _ = await ctx.Thumbnails.ProcessPendingAsync(null, Ct);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.True(await ctx.Thumbnails.GetCacheSizeAsync(Ct) > 0);

        Assert.True(await ctx.Thumbnails.ClearCacheAsync(Ct));

        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache"));
        Assert.Equal(0, await ctx.Thumbnails.GetCacheSizeAsync(Ct));
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives")); // アーカイブの登録は変わらない

        var summary = await ctx.Thumbnails.ProcessPendingAsync(null, Ct);
        Assert.Equal(2, summary?.Total);
        Assert.Equal(2, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.True(await ctx.Thumbnails.GetCacheSizeAsync(Ct) > 0);
    }

    [Fact]
    public async Task ライブラリから削除すると_一覧が作成待ちに戻り_サムネイルが作り直される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx);
        Assert.Equal(4, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.Null(harness.Library.ClearBlockedReason);

        Assert.True(await harness.Library.ClearCacheAsync());
        await harness.WaitForIdleAsync();

        Assert.Equal(4, await ctx.ScalarAsync("SELECT COUNT(*) FROM thumbnail_cache WHERE status = 'READY'"));
        Assert.True(await harness.Library.GetCacheSizeAsync(Ct) > 0);
    }

    [Fact]
    public async Task 設定画面は_キャッシュの大きさを表示し_削除すると通知して大きさを調べ直す()
    {
        using var ctx = new LibraryTestContext();
        var cache = new FakeThumbnailCacheMaintenance { SizeBytes = 5 * 1024 * 1024 };
        using var viewModel = CreateViewModel(ctx, cache);
        Assert.Equal("計算中…", viewModel.CacheSizeText);

        await viewModel.InitializeAsync();
        Assert.Equal("5.0 MB", viewModel.CacheSizeText);
        Assert.True(viewModel.ClearThumbnailCacheCommand.CanExecute(null));

        await viewModel.ClearThumbnailCacheCommand.ExecuteAsync(null);

        Assert.Equal(1, cache.ClearCalls);
        Assert.Equal("0 B", viewModel.CacheSizeText);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success);
    }

    [Fact]
    public async Task スキャンや作成の最中は_削除できず_理由を示し_終わると削除できるようになる()
    {
        using var ctx = new LibraryTestContext();
        var cache = new FakeThumbnailCacheMaintenance { SizeBytes = 1024, BlockedReason = "サムネイルの作成中は削除できません" };
        using var viewModel = CreateViewModel(ctx, cache);
        await viewModel.InitializeAsync();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(viewModel.ClearThumbnailCacheCommand.CanExecute(null));
        Assert.Equal("サムネイルの作成中は削除できません", viewModel.ClearCacheToolTip);

        cache.SizeBytes = 2048;
        cache.BlockedReason = null; // 作成が終わった
        Assert.Contains(nameof(SettingsViewModel.ClearCacheToolTip), changed);
        Assert.True(viewModel.ClearThumbnailCacheCommand.CanExecute(null));
        Assert.DoesNotContain("削除できません", viewModel.ClearCacheToolTip, StringComparison.Ordinal);
        await Task.Delay(50, Ct);
        Assert.Equal("2.0 KB", viewModel.CacheSizeText); // 終わったあとの大きさに更新される
    }

    [Fact]
    public async Task 削除できない状態になっていた場合は_削除せず警告する()
    {
        using var ctx = new LibraryTestContext();
        var cache = new FakeThumbnailCacheMaintenance { AllowClear = false };
        using var viewModel = CreateViewModel(ctx, cache);
        await viewModel.InitializeAsync();

        await viewModel.ClearThumbnailCacheCommand.ExecuteAsync(null);

        Assert.Equal(1, cache.ClearCalls);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning);
        Assert.DoesNotContain(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success);
    }

    [Fact]
    public void 確認の設定は_既定でオン_変えるとその場で保存される()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        using var viewModel = CreateViewModel(ctx, new FakeThumbnailCacheMaintenance(), settings);
        Assert.True(viewModel.ConfirmBeforeLeave);
        Assert.True(viewModel.ConfirmBeforeDelete);

        viewModel.ConfirmBeforeLeave = false;
        viewModel.ConfirmBeforeDelete = false;

        var reloaded = ctx.CreateSettings().Current; // 再起動しても残る
        Assert.False(reloaded.ConfirmBeforeLeave);
        Assert.False(reloaded.ConfirmBeforeDelete);
        Assert.False(viewModel.ConfirmBeforeLeave);
    }

    [Fact]
    public async Task 削除の確認をオフにすると_確認せずにごみ箱へ移す()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { ConfirmBeforeDelete = false });
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx, settings);
        harness.Library.SelectedArchive = harness.Library.Archives.Single(a => a.FileName == "b.zip");
        harness.Dialogs.DeleteConfirmed = false; // 確認が出たら、キャンセルになる

        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(harness.Dialogs.DeleteConfirmations);
        Assert.Empty(harness.Dialogs.PermanentDeleteConfirmations);
        Assert.False(File.Exists(ctx.PathOf("b.zip")));
        Assert.Single(ctx.RecycleBin.Items); // 削除は、ごみ箱へ退避する
    }

    [Fact]
    public async Task 削除の確認をオフにしても_ごみ箱を使えない保存先の完全削除は確認する()
    {
        using var ctx = new LibraryTestContext();
        var settings = ctx.CreateSettings();
        settings.Update(s => s with { ConfirmBeforeDelete = false });
        using var harness = await LibraryOperationsViewModelTests.OpenLibraryAsync(ctx, settings);
        harness.Library.SelectedArchive = harness.Library.Archives.Single(a => a.FileName == "b.zip");
        ctx.RecycleBin.Unavailable = "ネットワーク上の保存先のため、ごみ箱を使えません";
        harness.Dialogs.PermanentDeleteConfirmed = false;

        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);

        _ = Assert.Single(harness.Dialogs.PermanentDeleteConfirmations); // 元に戻せないため、必ず確認する
        Assert.True(File.Exists(ctx.PathOf("b.zip")));
    }

    /// <summary>キャッシュの管理の代わり。状態の変化は、本物と同じく PropertyChanged で知らせる。</summary>
    private sealed class FakeThumbnailCacheMaintenance : IThumbnailCacheMaintenance
    {
        private string? _blockedReason;

        public event PropertyChangedEventHandler? PropertyChanged;

        public long SizeBytes { get; set; }

        public bool AllowClear { get; set; } = true;

        public int ClearCalls { get; private set; }

        public string? BlockedReason
        {
            get => _blockedReason;
            set
            {
                _blockedReason = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClearBlockedReason)));
            }
        }

        public string? ClearBlockedReason => _blockedReason;

        public Task<long> GetCacheSizeAsync(CancellationToken ct = default) => Task.FromResult(SizeBytes);

        public Task<bool> ClearCacheAsync()
        {
            ClearCalls++;
            if (AllowClear)
            {
                SizeBytes = 0;
            }

            return Task.FromResult(AllowClear);
        }
    }

    private static class ThumbnailCacheStoreHelper
    {
        public static string Relative(long archiveId) => K35Viewer.Infrastructure.Thumbnails.ThumbnailCacheStore.GetRelativePath(archiveId);
    }
}
