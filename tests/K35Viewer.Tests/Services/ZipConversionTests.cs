using System.IO.Compression;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;

namespace K35Viewer.Tests.Services;

/// <summary>RAR / 7z から ZIP への変換（GitHub の Issue #20）。変換の処理、ライブラリからの変換、ビューアからの変換。</summary>
public class ZipConversionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", name);

    private static string CopyFixture(LibraryTestContext ctx, string fixture, string relative)
    {
        var destination = ctx.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(FixturePath(fixture), destination, overwrite: true);
        return destination;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>ZIP の中身（名前 → 内容）。フォルダは内容なし。</summary>
    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ArchiveReader.OpenRead(path);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            result[entry.FullName] = memory.ToArray();
        }

        return result;
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "k35-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ---- 変換の処理（ZipConverter） ----

    [Theory]
    [InlineData("nonsolid.7z")]
    [InlineData("solid.7z")]
    public void 七ゼットの全部のエントリを_同じ名前と内容で_ZIPへ写し_確認できる(string fixture)
    {
        var directory = TempDirectory();
        try
        {
            var destination = Path.Combine(directory, "out.zip");
            var result = new ZipConverter().Convert(FixturePath(fixture), destination, Ct);

            Assert.True(result.Succeeded, result.ErrorMessage);
            var entries = ReadZip(destination);
            // 画像以外のファイル（readme.txt、__MACOSX の中）とフォルダも、そのまま写す。区切りは /
            Assert.Equal(
                ["001.jpg", "002.png", "010.jpg", "__MACOSX/", "__MACOSX/._001.jpg", "readme.txt"],
                entries.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(646, entries["001.jpg"].Length);
            Assert.Equal(30, entries["readme.txt"].Length);
            Assert.True(new ArchiveWriter().Verify(destination, result.Entries!, Ct).Succeeded);

            // 元の 7z と同じ内容
            var original = new SharpCompressArchiveReader().Open(FixturePath(fixture), Ct);
            using var opened = original.Archive!;
            Assert.Equal(opened.ReadEntry("010.jpg", 1 << 20, Ct), entries["010.jpg"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RARの日本語の名前とフォルダは_UTF8の名前のZIPになり_画像は圧縮し直さない()
    {
        var directory = TempDirectory();
        try
        {
            var image = TestImages.JpegPattern(64, 64);
            var source = TestRars.CreateStored(
                Path.Combine(directory, "本.rar"),
                ("第1話/ページ 01.jpg", image),
                ("第1話/メモ.txt", "テキストは圧縮する。テキストは圧縮する。テキストは圧縮する。"u8.ToArray()));
            var destination = Path.Combine(directory, "本.zip");

            var result = new ZipConverter().Convert(source, destination, Ct);

            Assert.True(result.Succeeded, result.ErrorMessage);
            using var zip = ArchiveReader.OpenRead(destination);
            var picture = zip.GetEntry("第1話/ページ 01.jpg");
            Assert.NotNull(picture);
            Assert.Equal(picture.Length, picture.CompressedLength); // そのまま保存
            Assert.NotNull(zip.GetEntry("第1話/メモ.txt"));

            // ZIP の読み取り（ライブラリと同じ処理）でも、同じ画像として読める
            var analysis = new ArchiveReader().Analyze(destination, Ct);
            Assert.Equal(ArchiveAnalysisStatus.Ready, analysis.Status);
            Assert.Equal(["第1話/ページ 01.jpg"], analysis.CoverCandidates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("encrypted-data.7z", "ARCHIVE_ENCRYPTED")]
    [InlineData("encrypted-headers.7z", "ARCHIVE_ENCRYPTED")]
    public void 暗号化された七ゼットは_変換しない(string fixture, string code)
    {
        var directory = TempDirectory();
        try
        {
            var result = new ZipConverter().Convert(FixturePath(fixture), Path.Combine(directory, "out.zip"), Ct);

            Assert.False(result.Succeeded);
            Assert.Equal(code, result.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 途中で切れた七ゼットは_壊れているとして変換しない()
    {
        var directory = TempDirectory();
        try
        {
            var bytes = File.ReadAllBytes(FixturePath("nonsolid.7z"));
            var broken = Path.Combine(directory, "broken.7z");
            File.WriteAllBytes(broken, bytes[..(bytes.Length / 2)]);

            var result = new ZipConverter().Convert(broken, Path.Combine(directory, "out.zip"), Ct);

            Assert.False(result.Succeeded);
            Assert.Equal("ARCHIVE_BROKEN", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 変換先に同じ名前のファイルがあれば_上書きしない()
    {
        var directory = TempDirectory();
        try
        {
            var destination = Path.Combine(directory, "out.zip");
            File.WriteAllText(destination, "既存のファイル");

            var result = new ZipConverter().Convert(FixturePath("nonsolid.7z"), destination, Ct);

            Assert.False(result.Succeeded);
            Assert.Equal("既存のファイル", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ZIPに入らない日時は_範囲の端にそろえる()
    {
        Assert.Equal(1980, ZipConverter.ToZipTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Year);
        Assert.Equal(2107, ZipConverter.ToZipTime(new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Year);
        Assert.Equal(2026, ZipConverter.ToZipTime(new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Local)).Year);
        Assert.Equal("a/b/", ZipConverter.ToZipName(@"a\b", isDirectory: true));
        Assert.Equal("a/b.jpg", ZipConverter.ToZipName(@"a\b.jpg", isDirectory: false));
    }

    [Theory]
    [InlineData("a.rar", "a.zip")]
    [InlineData("a.7z", "a.zip")]
    [InlineData("a.cbr", "a.cbz")]
    [InlineData("a.CB7", "a.cbz")]
    [InlineData("巻.1.rar", "巻.1.zip")]
    public void 変換後の名前は_漫画用の拡張子なら漫画用のまま(string name, string expected) =>
        Assert.Equal(expected, ArchiveService.ZipFileNameFor(name));

    // ---- 変換の流れ（ArchiveService） ----

    [Fact]
    public async Task 変換すると_IDと閲覧位置とサムネイルを引き継ぎ_元のファイルはごみ箱へ移る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var source = CopyFixture(ctx, "solid.7z", "book.7z");
        _ = await ctx.RegisterAndScanAsync();
        _ = await ctx.Thumbnails.ProcessPendingAsync(null, Ct);
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        await ctx.ExecuteAsync($"UPDATE archives SET last_view_entry = '010.jpg', last_view_index = 2 WHERE id = {id}");
        Assert.Empty(await ctx.ArchiveRepository.GetPendingProcessingIdsAsync(Ct));

        var result = await ctx.Archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False(File.Exists(source));
        Assert.Single(ctx.RecycleBin.Items);
        Assert.True(File.Exists(ctx.PathOf("book.zip")));
        Assert.Empty(Directory.GetFiles(ctx.Workspace, "*.k35tmp"));
        Assert.Equal(
            $"{id}|book.zip|zip|010.jpg|2|READY|3",
            await ctx.StringAsync(
                "SELECT id || '|' || file_name || '|' || extension || '|' || last_view_entry || '|' || last_view_index || '|' || analysis_status || '|' || image_count FROM archives"));
        Assert.Equal(new FileInfo(ctx.PathOf("book.zip")).Length, await ctx.ScalarAsync("SELECT file_size FROM archives"));
        // サムネイルは、作り直さずに使い続ける
        Assert.Empty(await ctx.ArchiveRepository.GetPendingProcessingIdsAsync(Ct));
        Assert.Equal("ARCHIVE_CONVERT|SUCCESS|book.zip", await ctx.StringAsync(
            "SELECT operation_type || '|' || status || '|' || archive_name FROM operation_history ORDER BY id DESC LIMIT 1"));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Success && n.Message.Contains("ごみ箱", StringComparison.Ordinal));

        // 変換した ZIP は、ZIP として開けて、画像を削除できる
        var opened = await ctx.Viewer.OpenAsync(id, Ct);
        using var session = opened.Session!;
        Assert.True(session.CanModify);
        Assert.Equal(2, session.CurrentIndex);
    }

    [Fact]
    public async Task CBRはCBZになる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        _ = TestRars.CreateStored(ctx.PathOf("vol.cbr"), ("1.jpg", TestImages.Jpeg(20, 30)));
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");

        var result = await ctx.Archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal("vol.cbz|cbz", await ctx.StringAsync("SELECT file_name || '|' || extension FROM archives"));
    }

    [Fact]
    public async Task ごみ箱を使えない保存先では_元のファイルを残し_次のスキャンで別のアーカイブになる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var source = CopyFixture(ctx, "nonsolid.7z", "a.7z");
        var libraryId = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        ctx.RecycleBin.Unavailable = "ネットワークドライブ";

        var plan = ctx.Archives.PlanZipConversion(ctx.Workspace, "a.7z", out _);
        var result = await ctx.Archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal("ネットワークドライブ", plan!.KeepOriginalReason);
        Assert.Equal(OperationStatus.Partial, result.Status);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(ctx.PathOf("a.zip")));
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning && n.Message.Contains("残しています", StringComparison.Ordinal));
        Assert.Equal($"{id}|a.zip", await ctx.StringAsync("SELECT id || '|' || file_name FROM archives"));

        _ = await ctx.Scan.ScanAsync(libraryId, ScanType.Manual, Ct);
        Assert.Equal("a.7z;a.zip", await ctx.StringAsync(
            "SELECT group_concat(file_name, ';') FROM (SELECT file_name FROM archives WHERE availability_status = 'AVAILABLE' ORDER BY file_name)"));
        Assert.Equal(id, await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.zip'"));
    }

    [Fact]
    public async Task 元のファイルをごみ箱へ移せなかったら_残したことを知らせる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var source = CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        ctx.RecycleBin.FailToMove = true;

        var result = await ctx.Archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal(OperationStatus.Partial, result.Status);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(ctx.PathOf("a.zip")));
        Assert.Contains(ctx.Notifications.Items, n => n.Level == NotificationLevel.Warning && n.Message.Contains("移せなかった", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 同じ名前のZIPがあれば_何もしないで断る()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var source = CopyFixture(ctx, "nonsolid.7z", "a.7z");
        var existing = ctx.CreateZip("a.zip", ("x.jpg", TestImages.Jpeg(10, 10)));
        var before = File.ReadAllBytes(existing);
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives WHERE file_name = 'a.7z'");

        var result = await ctx.Archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("同じ名前", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Equal("a.7z", await ctx.StringAsync($"SELECT file_name FROM archives WHERE id = {id}"));
    }

    [Fact]
    public async Task 名前がCBRで中身がZIPなら_変換は要らないと知らせる()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        _ = TestZips.Create(ctx.PathOf("a.cbr"), ("1.jpg", TestImages.Jpeg(20, 30)));
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");

        var result = await ctx.Archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("すでに ZIP 形式", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(ctx.PathOf("a.cbr")));
        Assert.False(File.Exists(ctx.PathOf("a.cbz")));
    }

    [Fact]
    public async Task 変換に失敗したら_元のファイルも記録も変わらず_書きかけを消す()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var source = CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        var archives = ctx.CreateArchiveService(converter: new FailingConverter());

        var result = await archives.ConvertToZipAsync(id, null, Ct);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal("ARCHIVE_BROKEN", result.ErrorCode);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(ctx.PathOf("a.zip")));
        Assert.Empty(Directory.GetFiles(ctx.Workspace, "*.k35tmp"));
        Assert.Equal("a.7z|7z", await ctx.StringAsync("SELECT file_name || '|' || extension FROM archives"));
        Assert.Equal("ARCHIVE_CONVERT|FAILED", await ctx.StringAsync(
            "SELECT operation_type || '|' || status FROM operation_history ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task 作成の途中でキャンセルすると_何も変わらない()
    {
        using var ctx = new LibraryTestContext();
        await ctx.InitializeAsync();
        var source = CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = await ctx.RegisterAndScanAsync();
        var id = await ctx.ScalarAsync("SELECT id FROM archives");
        using var cts = new CancellationTokenSource();
        var archives = ctx.CreateArchiveService(converter: new CancellingConverter(cts));

        var result = await archives.ConvertToZipAsync(id, null, cts.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(ctx.PathOf("a.zip")));
        Assert.Empty(Directory.GetFiles(ctx.Workspace, "*.k35tmp"));
        Assert.Equal("a.7z", await ctx.StringAsync("SELECT file_name FROM archives"));
    }

    // ---- 画面からの変換 ----

    [Fact]
    public async Task ライブラリの右クリックから変換でき_確認でキャンセルすれば何もしない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = ctx.CreateZip("b.zip", ("1.jpg", TestImages.Jpeg(20, 30)));
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();

        // ZIP には、変換を出さない
        harness.Library.SelectedArchive = harness.Library.Archives.Single(a => a.FileName == "b.zip");
        Assert.False(harness.Library.ConvertSelectedToZipCommand.CanExecute(null));

        var item = harness.Library.Archives.Single(a => a.FileName == "a.7z");
        harness.Library.SelectedArchive = item;
        Assert.True(harness.Library.ConvertSelectedToZipCommand.CanExecute(null));

        harness.Dialogs.ConvertConfirmed = false;
        await harness.Library.ConvertSelectedToZipCommand.ExecuteAsync(null);
        Assert.Equal([("a.7z", "a.zip", (string?)null)], harness.Dialogs.ConvertConfirmations);
        Assert.True(File.Exists(ctx.PathOf("a.7z")));

        harness.Dialogs.ConvertConfirmed = true;
        await harness.Library.ConvertSelectedToZipCommand.ExecuteAsync(null);
        Assert.False(File.Exists(ctx.PathOf("a.7z")));
        Assert.True(File.Exists(ctx.PathOf("a.zip")));
        // 同じアーカイブ（ID は変わらない）が、新しい名前で選ばれている
        Assert.Equal(item.Id, harness.Library.SelectedArchive?.Id);
        Assert.Equal("a.zip", harness.Library.SelectedArchive?.FileName);
        Assert.False(harness.Library.ConvertSelectedToZipCommand.CanExecute(null));
    }

    [Fact]
    public async Task ビューアから変換すると_同じ位置で開き直し_画像を削除できるようになる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        CopyFixture(ctx, "solid.7z", "a.cb7");
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();

        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single());
        await WaitUntilAsync(() => harness.Viewer.IsOpen && harness.Main.IsViewerVisible && !harness.Main.IsTransitioning);
        Assert.True(harness.Viewer.IsReadOnlyArchive);
        harness.Viewer.NextCommand.Execute(null);
        Assert.Equal(1, harness.Viewer.Session!.CurrentIndex);

        harness.Dialogs.ConvertConfirmed = true;
        harness.Viewer.RequestConvertToZipCommand.Execute(null);
        await WaitUntilAsync(() => harness.Viewer.IsOpen && harness.Viewer.ArchiveName == "a.cbz" && !harness.Main.IsTransitioning);

        Assert.True(harness.Main.IsViewerVisible);
        Assert.False(harness.Main.IsUpdating);
        Assert.False(harness.Viewer.IsReadOnlyArchive);
        Assert.True(harness.Viewer.Session!.CanModify);
        Assert.Equal(1, harness.Viewer.Session.CurrentIndex);
        Assert.False(File.Exists(ctx.PathOf("a.cb7")));

        harness.Viewer.ToggleDeleteCommand.Execute(null);
        Assert.Equal(1, harness.Viewer.PendingDeleteCount);
        harness.Dialogs.Choice = LeaveChoice.Discard;
        await harness.Viewer.CloseAsync();
    }

    [Fact]
    public async Task ビューアで確認をキャンセルすれば_開いたまま何もしない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = new MainTestHarness(ctx);
        await ctx.InitializeAsync();
        CopyFixture(ctx, "nonsolid.7z", "a.7z");
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        harness.Library.OpenArchiveCommand.Execute(harness.Library.Archives.Single());
        await WaitUntilAsync(() => harness.Viewer.IsOpen && !harness.Main.IsTransitioning);
        var session = harness.Viewer.Session;

        harness.Dialogs.ConvertConfirmed = false;
        harness.Viewer.RequestConvertToZipCommand.Execute(null);

        Assert.Single(harness.Dialogs.ConvertConfirmations);
        Assert.Same(session, harness.Viewer.Session);
        Assert.True(File.Exists(ctx.PathOf("a.7z")));
        Assert.False(File.Exists(ctx.PathOf("a.zip")));
        await harness.Viewer.CloseAsync();
    }

    /// <summary>書きかけのファイルを残して、壊れているとして失敗する。</summary>
    private sealed class FailingConverter : IZipConverter
    {
        public long? EstimateSize(string sourcePath, CancellationToken ct = default) => 1;

        public ArchiveWriteResult Convert(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            File.WriteAllText(destinationPath, "書きかけ");
            return ArchiveWriteResult.Failure("ARCHIVE_BROKEN", "テスト用の失敗");
        }
    }

    /// <summary>作成の途中で、キャンセルされる。</summary>
    private sealed class CancellingConverter(CancellationTokenSource cts) : IZipConverter
    {
        public long? EstimateSize(string sourcePath, CancellationToken ct = default) => 1;

        public ArchiveWriteResult Convert(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            File.WriteAllText(destinationPath, "書きかけ");
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new ArchiveWriteResult(true, Entries: []);
        }
    }
}
