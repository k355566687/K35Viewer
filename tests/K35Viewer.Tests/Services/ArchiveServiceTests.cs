using System.Security.Cryptography;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

/// <summary>書き込み処理の一部を差し替えて、失敗・中断・待機を再現するためのラッパー。</summary>
internal sealed class ScriptedArchiveWriter(IArchiveWriter inner) : IArchiveWriter
{
    /// <summary>null 以外を返すと、実際の再構築の代わりにその結果を使う。</summary>
    public Func<string, string, IReadOnlySet<string>, CancellationToken, ArchiveWriteResult?>? OnRebuild { get; set; }

    public Func<string, IReadOnlyList<WrittenEntry>, CancellationToken, ArchiveWriteResult?>? OnVerify { get; set; }

    public ArchiveWriteResult Rebuild(
        string sourcePath, string destinationPath, IReadOnlySet<string> excludedEntries, CancellationToken ct = default) =>
        OnRebuild?.Invoke(sourcePath, destinationPath, excludedEntries, ct)
        ?? inner.Rebuild(sourcePath, destinationPath, excludedEntries, ct);

    public ArchiveWriteResult Verify(string path, IReadOnlyList<WrittenEntry> expected, CancellationToken ct = default) =>
        OnVerify?.Invoke(path, expected, ct) ?? inner.Verify(path, expected, ct);
}

/// <summary>報告を、受け取ったスレッドで同期的に処理する（<see cref="Progress{T}"/> は順序が保証されない）。</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>削除予約の反映（ZIP再構築）。元のファイルは、一時ZIPの作成・検証・ごみ箱への退避の順に安全に置き換える。</summary>
public class ArchiveServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Prepared(long LibraryId, long ArchiveId, string ZipPath, ViewerSession Session) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] TempFiles(LibraryTestContext ctx) =>
        Directory.GetFiles(ctx.Workspace, "*" + K35Viewer.Infrastructure.FileSystem.FileSystemService.TempFileSuffix);

    private static string[] EntryNames(string zipPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    /// <summary>
    /// 画像 001.jpg〜(pages)枚と readme.txt を持つZIPを登録し、サムネイルまで作って、ビューアで開く。
    /// 画像 i の幅は 100 + 10×i。
    /// </summary>
    private static async Task<Prepared> PrepareAsync(LibraryTestContext ctx, int pages = 5)
    {
        await ctx.InitializeAsync();
        ctx.CreateZip(
            "book.zip",
            [.. Enumerable.Range(1, pages).Select(i => ($"{i:D3}.jpg", TestImages.Jpeg(100 + (10 * i), 60))), ("readme.txt", [1, 2, 3])]);
        var libraryId = await ctx.RegisterAndScanAsync();
        await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        var opened = await ctx.Viewer.OpenAsync(id, Ct);
        return new Prepared(libraryId, id, ctx.PathOf("book.zip"), opened.Session!);
    }

    private static ArchiveUpdateRequest Reserve(Prepared prepared, params int[] indices)
    {
        _ = prepared.Session.SetPendingDelete(indices, true);
        return prepared.Session.CreateUpdateRequest()!;
    }

    [Fact]
    public async Task 削除予約を反映すると_画像を除いたZIPに置き換わり_元のZIPはごみ箱へ退避される()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var original = Hash(prepared.ZipPath);
        var originalSize = new FileInfo(prepared.ZipPath).Length;
        var request = Reserve(prepared, 0, 2); // 001.jpg と 003.jpg
        var phases = new List<ArchiveUpdatePhase>();
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.ApplyDeletionsAsync(request, new SyncProgress<ArchiveUpdatePhase>(phases.Add), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RemovedCount);
        Assert.Equal(
            [ArchiveUpdatePhase.Preparing, ArchiveUpdatePhase.Rebuilding, ArchiveUpdatePhase.Verifying, ArchiveUpdatePhase.Replacing,
                ArchiveUpdatePhase.Finishing],
            phases);

        // ファイル: 新しいZIPは残りの画像と画像以外のエントリを持ち、元のZIPはごみ箱にある
        Assert.Equal(["002.jpg", "004.jpg", "005.jpg", "readme.txt"], EntryNames(prepared.ZipPath));
        var binned = Assert.Single(ctx.RecycleBin.Items);
        Assert.Equal(original, Hash(binned));
        Assert.Equal(originalSize, new FileInfo(binned).Length);
        Assert.Empty(TempFiles(ctx));

        // DB: 新しいファイルの状態、解析待ち、画像枚数、サムネイルの再生成待ち、閲覧位置、操作履歴
        var info = new FileInfo(prepared.ZipPath);
        Assert.Equal(info.Length, await ctx.ScalarAsync("SELECT file_size FROM archives"));
        Assert.Equal(
            UnixTime.ToUnixSeconds(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)),
            await ctx.ScalarAsync("SELECT last_write_time FROM archives"));
        Assert.Equal("PENDING", await ctx.StringAsync("SELECT analysis_status FROM archives"));
        Assert.Equal(3, await ctx.ScalarAsync("SELECT image_count FROM archives"));
        Assert.Equal("STALE", await ctx.StringAsync("SELECT status FROM thumbnail_cache"));
        Assert.Equal("002.jpg", await ctx.StringAsync("SELECT last_view_entry FROM archives")); // 現在位置（001）は削除されたため、次の画像
        Assert.Equal(0, await ctx.ScalarAsync("SELECT last_view_index FROM archives"));
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'IMAGE_DELETE' AND status = 'SUCCESS' " +
                $"AND archive_id = {prepared.ArchiveId} AND affected_count = 2 AND old_path = new_path"));

        // 通知: 開始と完了（削除した枚数を含む）
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Info && n.Message.Contains("更新を開始", StringComparison.Ordinal));
        Assert.Contains(
            ctx.Notifications.Items,
            n => n.Level == NotificationLevel.Success && n.Message.Contains("2枚の画像を削除しました", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 更新後は_サムネイルと表紙が作り直され_スキャンしても同じArchiveIDのまま変わらない()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var result = await ctx.Archives.ApplyDeletionsAsync(Reserve(prepared, 0, 1), ct: Ct);
        Assert.True(result.Succeeded);

        var run = await ctx.Thumbnails.ProcessPendingAsync(ct: Ct);

        Assert.Equal(1, run!.Total);
        Assert.Equal("READY", await ctx.StringAsync("SELECT analysis_status FROM archives"));
        Assert.Equal(3, await ctx.ScalarAsync("SELECT image_count FROM archives"));
        Assert.Equal("003.jpg", await ctx.StringAsync("SELECT first_image_entry FROM archives")); // 新しい先頭の画像が表紙
        Assert.Equal("READY", await ctx.StringAsync("SELECT status FROM thumbnail_cache"));
        Assert.Equal("003.jpg", await ctx.StringAsync("SELECT source_entry FROM thumbnail_cache"));

        // スキャンしても、同じアーカイブとして扱われる（変更扱いで解析待ちに戻らず、一時ファイルも登録されない）
        _ = await ctx.Scan.ScanAsync(prepared.LibraryId, ScanType.Manual, Ct);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives"));
        Assert.Equal(prepared.ArchiveId, await ctx.ScalarAsync("SELECT id FROM archives"));
        Assert.Equal("READY", await ctx.StringAsync("SELECT analysis_status FROM archives"));
    }

    [Fact]
    public async Task すべての画像を削除予約しても_更新でき_閲覧位置は消える()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx, pages: 3);

        var result = await ctx.Archives.ApplyDeletionsAsync(Reserve(prepared, 0, 1, 2), ct: Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["readme.txt"], EntryNames(prepared.ZipPath));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT image_count FROM archives"));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM archives WHERE last_view_entry IS NOT NULL"));
        Assert.Single(ctx.RecycleBin.Items);
    }

    [Fact]
    public async Task 削除対象がなければ_何もせず失敗する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0) with { EntriesToRemove = [] };

        var result = await ctx.Archives.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal("NOTHING_TO_DELETE", result.ErrorCode);
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
    }

    // ---- 更新できるかの確認 ----

    [Fact]
    public async Task 更新の確認は_外部で変更されていないかとごみ箱を使えるかを見る()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        Assert.Null(ctx.Archives.GetUpdateBlockReason(request));
        Assert.False(ctx.Archives.IsChangedExternally(prepared.ZipPath, prepared.Session.OpenedFileState));

        ctx.RecycleBin.Unavailable = "ネットワーク上の保存先のため、ごみ箱を使えません";
        var recycle = ctx.Archives.GetUpdateBlockReason(request);
        Assert.Contains("ごみ箱を使えません", recycle, StringComparison.Ordinal);
        ctx.RecycleBin.Unavailable = null;

        ctx.CreateZip("book.zip", ("x.jpg", TestImages.Jpeg(50, 50))); // 外部で書き換えられた
        Assert.True(ctx.Archives.IsChangedExternally(prepared.ZipPath, prepared.Session.OpenedFileState));
        Assert.Contains("外部で変更", ctx.Archives.GetUpdateBlockReason(request), StringComparison.Ordinal);

        File.Delete(prepared.ZipPath);
        Assert.True(ctx.Archives.IsChangedExternally(prepared.ZipPath, prepared.Session.OpenedFileState));
        Assert.Contains("見つからない", ctx.Archives.GetUpdateBlockReason(request), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 開いたあとに外部で変更されたアーカイブは_更新を中止し_ファイルに触れない()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        ctx.CreateZip("book.zip", ("only.jpg", TestImages.Jpeg(50, 50)), ("other.jpg", TestImages.Jpeg(60, 50)));
        var changed = Hash(prepared.ZipPath);
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal(ArchiveUpdateStatus.Failed, result.Status);
        Assert.Equal("ARCHIVE_CHANGED", result.ErrorCode);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(changed, Hash(prepared.ZipPath)); // 外部で書き換えられた内容のまま（上書きしない）
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Empty(TempFiles(ctx));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'IMAGE_DELETE'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error && n.Message.Contains("外部で変更", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ごみ箱を使えない保存先では_一時ZIPを作らずに中止し_元のZIPを保持する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        var original = Hash(prepared.ZipPath);
        ctx.RecycleBin.Unavailable = "ネットワーク上の保存先のため、ごみ箱を使えません";
        var rebuilds = 0;
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, _, _, _) =>
            {
                rebuilds++;
                return null;
            },
        });

        var result = await service.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal("RECYCLE_BIN_UNAVAILABLE", result.ErrorCode);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(0, rebuilds); // 無駄な一時ZIPを作らない
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
        Assert.Contains("ごみ箱を使えません", result.Message, StringComparison.Ordinal);
    }

    // ---- 安全境界より前の失敗 ----

    [Fact]
    public async Task ごみ箱への移動に失敗したら_一時ZIPを削除し_元のZIPをそのまま保持する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        var original = Hash(prepared.ZipPath);
        ctx.RecycleBin.FailToMove = true;

        var result = await ctx.Archives.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal("RECYCLE_FAILED", result.ErrorCode);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(1, ctx.RecycleBin.MoveCalls);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'IMAGE_DELETE'"));
        Assert.Equal("READY", await ctx.StringAsync("SELECT status FROM thumbnail_cache")); // DBは変更しない
    }

    [Theory]
    [InlineData("DISK_FULL")]
    [InlineData("ACCESS_DENIED")]
    [InlineData("ARCHIVE_BROKEN")]
    [InlineData("IO_ERROR")]
    public async Task 一時ZIPの作成に失敗したら_書きかけを削除し_元のZIPを保持する(string errorCode)
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        var original = Hash(prepared.ZipPath);
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, destination, _, _) =>
            {
                File.WriteAllText(destination, "書きかけ");
                return ArchiveWriteResult.Failure(errorCode, "テスト用の失敗");
            },
        });

        var result = await service.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal(errorCode, result.ErrorCode);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Contains("元のアーカイブは変更していません", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 一時ZIPの検証に失敗したら_ごみ箱へ移さず_元のZIPを保持する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        var original = Hash(prepared.ZipPath);
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnVerify = (_, _, _) => ArchiveWriteResult.Failure("VERIFY_FAILED", "テスト用の失敗"),
        });

        var result = await service.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal("VERIFY_FAILED", result.ErrorCode);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
    }

    [Fact]
    public async Task 想定外の例外が起きても_一時ZIPを削除し_元のZIPを保持する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        var original = Hash(prepared.ZipPath);
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, destination, _, _) =>
            {
                File.WriteAllText(destination, "書きかけ");
                throw new InvalidOperationException("テスト用の例外");
            },
        });

        var result = await service.ApplyDeletionsAsync(request, ct: Ct);

        Assert.Equal("UPDATE_FAILED", result.ErrorCode);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
    }

    [Fact]
    public async Task 安全境界より前にキャンセルすると_一時ZIPを削除し_元のZIPと削除予約を保持する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0, 1);
        var original = Hash(prepared.ZipPath);
        using var started = new ManualResetEventSlim();
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, destination, _, ct) =>
            {
                File.WriteAllText(destination, "書きかけ");
                started.Set();
                _ = ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                ct.ThrowIfCancellationRequested();
                return null;
            },
        });
        using var cts = new CancellationTokenSource();
        ctx.Notifications.Items.Clear();

        var task = service.ApplyDeletionsAsync(request, ct: cts.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(15), Ct));
        await cts.CancelAsync();
        var result = await task;

        Assert.Equal(ArchiveUpdateStatus.Cancelled, result.Status);
        Assert.True(result.OriginalPreserved);
        Assert.Equal(original, Hash(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Equal(2, prepared.Session.PendingDeleteCount); // 削除予約は、呼び出し側のセッションに残っている
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Info && n.Message.Contains("キャンセル", StringComparison.Ordinal));
    }

    // ---- 安全境界 ----

    [Fact]
    public async Task 元のZIPの退避を始めた後にキャンセルされても_最後まで完了する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ArchiveUpdatePhase>(phase =>
        {
            if (phase == ArchiveUpdatePhase.Replacing)
            {
                cts.Cancel(); // 安全境界に入った時点でキャンセルを要求する
            }
        });

        var result = await ctx.Archives.ApplyDeletionsAsync(request, progress, cts.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(["002.jpg", "003.jpg", "004.jpg", "005.jpg", "readme.txt"], EntryNames(prepared.ZipPath));
        Assert.Single(ctx.RecycleBin.Items);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'IMAGE_DELETE' AND status = 'SUCCESS'"));
    }

    [Fact]
    public async Task 置き換えに失敗した場合は_更新前のファイルがごみ箱にあることと一時ファイルの場所を知らせて記録する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        var original = Hash(prepared.ZipPath);
        // 元の場所を塞いで、一時ZIPを元の名前にできない状態を作る
        ctx.RecycleBin.AfterMove = path => Directory.CreateDirectory(path);
        ctx.Notifications.Items.Clear();

        var result = await ctx.Archives.ApplyDeletionsAsync(request, ct: Ct);

        var temp = Assert.Single(TempFiles(ctx)); // 新しいZIPは一時ファイルのまま残る
        Assert.Equal("REPLACE_FAILED", result.ErrorCode);
        Assert.False(result.OriginalPreserved);
        Assert.Contains(temp, result.Message, StringComparison.Ordinal);
        Assert.Contains("ごみ箱", result.Message, StringComparison.Ordinal);
        Assert.Equal(original, Hash(Assert.Single(ctx.RecycleBin.Items))); // 更新前のファイルはごみ箱にある
        Assert.Equal(
            1,
            await ctx.ScalarAsync(
                "SELECT COUNT(*) FROM operation_history WHERE operation_type = 'IMAGE_DELETE' AND status = 'FAILED' " +
                "AND error_code = 'REPLACE_FAILED'"));
        Assert.Equal(temp, await ctx.StringAsync("SELECT new_path FROM operation_history WHERE status = 'FAILED'"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Error && n.Message.Contains(temp, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 一時的に置き換えられなくても_再試行して完了する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        // 退避の直後だけ元の場所を塞ぎ、再試行の間に解除する（他のプログラムが一時的に掴む状況）
        ctx.RecycleBin.AfterMove = path =>
        {
            Directory.CreateDirectory(path);
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                Directory.Delete(path);
            });
        };
        // 塞ぎを外すのは約30ms後。負荷が高くても間に合うよう、再試行の待ち時間には余裕を持たせる
        var service = ctx.CreateArchiveService(replaceDelays: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)]);

        var result = await service.ApplyDeletionsAsync(request, ct: Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["002.jpg", "003.jpg", "004.jpg", "005.jpg", "readme.txt"], EntryNames(prepared.ZipPath));
        Assert.Empty(TempFiles(ctx));
    }

    [Fact]
    public async Task 進行状況の報告で例外が起きても_更新は最後まで完了する()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);

        var result = await ctx.Archives.ApplyDeletionsAsync(
            request, new SyncProgress<ArchiveUpdatePhase>(_ => throw new InvalidOperationException("画面側の不具合")), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["002.jpg", "003.jpg", "004.jpg", "005.jpg", "readme.txt"], EntryNames(prepared.ZipPath));
        Assert.Single(ctx.RecycleBin.Items);
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'IMAGE_DELETE' AND status = 'SUCCESS'"));
    }

    // ---- 排他 ----

    [Fact]
    public async Task 同じアーカイブの更新中に_もう一度更新を始めることはできない()
    {
        using var ctx = new LibraryTestContext();
        using var prepared = await PrepareAsync(ctx);
        var request = Reserve(prepared, 0);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = ctx.CreateArchiveService(new ScriptedArchiveWriter(new ArchiveWriter())
        {
            OnRebuild = (_, _, _, ct) =>
            {
                entered.Set();
                release.Wait(ct);
                return null; // 待ったあとは、実際の再構築に進む
            },
        });

        var first = service.ApplyDeletionsAsync(request, ct: Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15), Ct));
        var second = await service.ApplyDeletionsAsync(request, ct: Ct);
        release.Set();
        var firstResult = await first;

        Assert.Equal("UPDATE_IN_PROGRESS", second.ErrorCode);
        Assert.True(firstResult.Succeeded);
        Assert.Equal(1, ctx.RecycleBin.MoveCalls); // 退避は1回だけ

        // 終わったあとは、同じアーカイブでもまた更新できる（排他が解除されている）
        Assert.NotEqual("UPDATE_IN_PROGRESS", (await service.ApplyDeletionsAsync(request, ct: Ct)).ErrorCode);
    }
}
