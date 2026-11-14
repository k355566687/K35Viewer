using System.Security.Cryptography;
using System.Windows.Input;
using K35Viewer.Services;
using K35Viewer.Tests.Infrastructure;
using K35Viewer.ViewModels;

namespace K35Viewer.Tests.Services;

/// <summary>ライブラリ画面での、アーカイブの選択・操作（名前変更、移動、削除、エクスプローラーで表示）と管理フォルダの解除、キー操作。</summary>
public class LibraryOperationsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされるまで待機しましたが、タイムアウトしました。");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>a.zip・b.zip・c.zip・sub\d.zip と、空のフォルダ dest を登録し、一覧を表示した状態にする（ファイル名の昇順）。</summary>
    internal static async Task<MainTestHarness> OpenLibraryAsync(LibraryTestContext ctx, SettingsService? settings = null)
    {
        var harness = new MainTestHarness(ctx, settings: settings);
        await ctx.InitializeAsync();
        foreach (var name in new[] { "a.zip", "b.zip", "c.zip", @"sub\d.zip" })
        {
            ctx.CreateZip(name, ("1.jpg", TestImages.Jpeg(100, 80)), ("2.jpg", TestImages.Jpeg(110, 80)));
        }

        _ = Directory.CreateDirectory(ctx.PathOf("dest"));
        _ = await ctx.RegisterAndScanAsync();
        await harness.Library.InitializeAsync();
        await harness.WaitForIdleAsync();
        Assert.Equal(["a.zip", "b.zip", "c.zip", "d.zip"], harness.Library.Archives.Select(a => a.FileName));
        return harness;
    }

    private static ArchiveListItem Item(MainTestHarness harness, string fileName) =>
        harness.Library.Archives.Single(a => a.FileName == fileName);

    // ---- 選択 ----

    [Fact]
    public async Task 選択すると_項目に選択中の状態が付き_前の選択は外れる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var library = harness.Library;
        Assert.Null(library.SelectedArchive);
        Assert.False(library.RenameSelectedCommand.CanExecute(null));
        Assert.False(library.DeleteSelectedCommand.CanExecute(null));
        Assert.False(library.MoveSelectedCommand.CanExecute(null));
        Assert.False(library.ShowSelectedInExplorerCommand.CanExecute(null));
        Assert.False(library.OpenSelectedCommand.CanExecute(null));

        library.SelectedArchive = Item(harness, "b.zip");
        Assert.True(Item(harness, "b.zip").IsSelected);
        Assert.Equal("選択中", Item(harness, "b.zip").StatusText);
        Assert.True(library.RenameSelectedCommand.CanExecute(null));
        Assert.True(library.DeleteSelectedCommand.CanExecute(null));

        library.OnThumbnailClicked(Item(harness, "c.zip"), clickCount: 1);
        Assert.False(Item(harness, "b.zip").IsSelected);
        Assert.Equal(string.Empty, Item(harness, "b.zip").StatusText);
        Assert.True(Item(harness, "c.zip").IsSelected);
        Assert.Equal(1, library.Archives.Count(a => a.IsSelected));
    }

    [Fact]
    public async Task 選択は_検索や並べ替えで一覧を取得し直しても維持される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var library = harness.Library;
        library.SelectedArchive = Item(harness, "b.zip");

        library.IsDescending = true;
        await WaitUntilAsync(() => library.Archives[0].FileName == "d.zip");

        Assert.Equal("b.zip", library.SelectedArchive?.FileName);
        Assert.True(Item(harness, "b.zip").IsSelected);

        library.SearchText = "c";
        await WaitUntilAsync(() => library.Archives.Count == 1);
        Assert.Null(library.SelectedArchive); // 絞り込みで一覧になくなった項目は、選択が外れる
    }

    [Fact]
    public async Task ダブルクリックで開く要求が出て_右クリックは選択だけを行う()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var requested = new List<long>();
        harness.Library.OpenRequested += (_, id) => requested.Add(id);

        harness.Library.OnThumbnailClicked(Item(harness, "a.zip"), clickCount: 1);
        Assert.Empty(requested);
        harness.Library.OnArchiveRightClicked(Item(harness, "c.zip"));
        Assert.Equal("c.zip", harness.Library.SelectedArchive?.FileName);
        Assert.Empty(requested);

        harness.Library.OnThumbnailClicked(Item(harness, "b.zip"), clickCount: 2);

        Assert.Equal([Item(harness, "b.zip").Id], requested);
    }

    // ---- 名前変更 ----

    [Fact]
    public async Task 名前変更を実行すると_入力した名前に変わり_同じ項目が選択されたままになる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var id = Item(harness, "b.zip").Id;
        harness.Library.SelectedArchive = Item(harness, "b.zip");
        harness.Dialogs.RenameInput = "bb";

        await harness.Library.RenameSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["b.zip"], harness.Dialogs.RenamePrompts);
        Assert.Null(harness.Dialogs.LastRenameError);
        Assert.True(File.Exists(ctx.PathOf("bb.zip")));
        Assert.False(File.Exists(ctx.PathOf("b.zip")));
        Assert.Equal(["a.zip", "bb.zip", "c.zip", "d.zip"], harness.Library.Archives.Select(a => a.FileName));
        Assert.Equal(id, harness.Library.SelectedArchive?.Id);
        Assert.True(harness.Library.SelectedArchive!.IsSelected);
        Assert.False(harness.Library.IsOperating);
    }

    [Fact]
    public async Task 名前変更の入力が不正なら_ダイアログへ理由が渡され_何も変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "a.zip");
        var original = Hash(ctx.PathOf("a.zip"));

        harness.Dialogs.RenameInput = "c"; // 同じフォルダに c.zip がある
        await harness.Library.RenameSelectedCommand.ExecuteAsync(null);
        Assert.Contains("既にあります", harness.Dialogs.LastRenameError, StringComparison.Ordinal);

        harness.Dialogs.RenameInput = "a:b";
        await harness.Library.RenameSelectedCommand.ExecuteAsync(null);
        Assert.Contains("使用できない文字", harness.Dialogs.LastRenameError, StringComparison.Ordinal);

        Assert.Equal(original, Hash(ctx.PathOf("a.zip")));
        Assert.Equal(["a.zip", "b.zip", "c.zip", "d.zip"], harness.Library.Archives.Select(a => a.FileName));
    }

    [Fact]
    public async Task 名前変更をキャンセルすると_何も変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "a.zip");
        harness.Dialogs.RenameInput = null;
        ctx.Notifications.Items.Clear();

        await harness.Library.RenameSelectedCommand.ExecuteAsync(null);

        Assert.True(File.Exists(ctx.PathOf("a.zip")));
        Assert.Empty(ctx.Notifications.Items);
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_RENAME'"));
    }

    // ---- 移動 ----

    [Fact]
    public async Task 移動を実行すると_別のフォルダに移り_同じ項目が選択されたままになる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var id = Item(harness, "a.zip").Id;
        harness.Library.SelectedArchive = Item(harness, "a.zip");
        harness.Dialogs.MoveDestination = ctx.PathOf("dest");

        await harness.Library.MoveSelectedCommand.ExecuteAsync(null);

        Assert.True(File.Exists(ctx.PathOf(@"dest\a.zip")));
        Assert.False(File.Exists(ctx.PathOf("a.zip")));
        Assert.Equal(id, harness.Library.SelectedArchive?.Id);
        Assert.Equal(ctx.PathOf("dest"), harness.Library.SelectedArchive!.Location);
        Assert.Equal(4, harness.Library.TotalCount);
    }

    [Fact]
    public async Task 移動先を選ばなければ_何も変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "a.zip");
        harness.Dialogs.MoveDestination = null;

        await harness.Library.MoveSelectedCommand.ExecuteAsync(null);

        Assert.True(File.Exists(ctx.PathOf("a.zip")));
        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM operation_history WHERE operation_type = 'ARCHIVE_MOVE'"));
    }

    // ---- 削除 ----

    [Fact]
    public async Task 削除を確認すると_ごみ箱へ移動され_次の項目が選択される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "b.zip");
        harness.Dialogs.DeleteConfirmed = true;

        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["b.zip"], harness.Dialogs.DeleteConfirmations);
        Assert.Empty(harness.Dialogs.PermanentDeleteConfirmations);
        Assert.False(File.Exists(ctx.PathOf("b.zip")));
        Assert.Single(ctx.RecycleBin.Items);
        Assert.Equal(["a.zip", "c.zip", "d.zip"], harness.Library.Archives.Select(a => a.FileName));
        Assert.Equal("c.zip", harness.Library.SelectedArchive?.FileName); // 次の項目
        Assert.Equal(3, harness.Library.TotalCount);
    }

    [Fact]
    public async Task 末尾の項目を削除すると_前の項目が選択される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "d.zip");
        harness.Dialogs.DeleteConfirmed = true;

        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal("c.zip", harness.Library.SelectedArchive?.FileName);
    }

    [Fact]
    public async Task 削除の確認でキャンセルすると_何も変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "b.zip");
        harness.Dialogs.DeleteConfirmed = false;

        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(harness.Dialogs.DeleteConfirmations);
        Assert.True(File.Exists(ctx.PathOf("b.zip")));
        Assert.Empty(ctx.RecycleBin.Items);
        Assert.Equal(4, harness.Library.Archives.Count);
        Assert.Equal("b.zip", harness.Library.SelectedArchive?.FileName);
    }

    [Fact]
    public async Task ごみ箱を使えない保存先では_元に戻せないことを確認し_承認された場合だけ完全に削除する()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "b.zip");
        ctx.RecycleBin.Unavailable = "ネットワーク上の保存先のため、ごみ箱を使えません";

        // キャンセル: ファイルもDBの登録も変更しない
        harness.Dialogs.PermanentDeleteConfirmed = false;
        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Empty(harness.Dialogs.DeleteConfirmations); // 通常の削除確認ではなく、完全削除の確認だけを出す
        var (fileName, reason) = Assert.Single(harness.Dialogs.PermanentDeleteConfirmations);
        Assert.Equal("b.zip", fileName);
        Assert.Contains("ごみ箱を使えません", reason, StringComparison.Ordinal);
        Assert.True(File.Exists(ctx.PathOf("b.zip")));
        Assert.Equal(4, harness.Library.Archives.Count);

        // 承認: ごみ箱を使わずに完全に削除する
        harness.Dialogs.PermanentDeleteConfirmed = true;
        await harness.Library.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.False(File.Exists(ctx.PathOf("b.zip")));
        Assert.Empty(ctx.RecycleBin.Items);
        Assert.Equal(0, ctx.RecycleBin.MoveCalls);
        Assert.Equal(3, harness.Library.Archives.Count);
    }

    // ---- エクスプローラーで表示 ----

    [Fact]
    public async Task エクスプローラーで表示を実行すると_選択中のファイルが渡される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.SelectedArchive = Item(harness, "c.zip");

        await harness.Library.ShowSelectedInExplorerCommand.ExecuteAsync(null);

        Assert.Equal([ctx.PathOf("c.zip")], ctx.Explorer.Revealed);
        Assert.Equal(4, harness.Library.Archives.Count); // 一覧は変わらない
    }

    // ---- 管理フォルダを外す ----

    [Fact]
    public async Task 管理フォルダを外せるのは_スキャン中でない時の管理フォルダ自身だけ()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var all = harness.Library.FolderRoots.Single(f => f.Id is null);
        var root = harness.Library.FolderRoots.Single(f => f.IsLibraryRoot);
        var sub = root.Children.First(c => c.Header == "sub");

        harness.Library.OnFolderSelected(all);
        Assert.False(harness.Library.RemoveSelectedFolderCommand.CanExecute(null));
        harness.Library.OnFolderSelected(sub);
        Assert.False(harness.Library.RemoveSelectedFolderCommand.CanExecute(null)); // サブフォルダは単独では外せない
        harness.Library.OnFolderSelected(root);
        Assert.True(harness.Library.RemoveSelectedFolderCommand.CanExecute(null));

        harness.Library.IsScanning = true;
        Assert.False(harness.Library.RemoveSelectedFolderCommand.CanExecute(null)); // スキャン中は実行できない
        harness.Library.IsScanning = false;
        Assert.True(harness.Library.RemoveSelectedFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task 管理フォルダを外す確認でキャンセルすると_何も変更されない()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.OnFolderSelected(harness.Library.FolderRoots.Single(f => f.IsLibraryRoot));
        harness.Dialogs.RemoveFolderConfirmed = false;

        await harness.Library.RemoveSelectedFolderCommand.ExecuteAsync(null);

        var (path, count) = Assert.Single(harness.Dialogs.RemoveFolderConfirmations);
        Assert.Equal(ctx.Workspace, path);
        Assert.Equal(4, count); // 確認には、配下のアーカイブ件数を含める
        Assert.Equal(1, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(4, harness.Library.Archives.Count);
    }

    [Fact]
    public async Task 管理フォルダを外すと_ツリーと一覧から消え_ファイルは残る()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        harness.Library.OnFolderSelected(harness.Library.FolderRoots.Single(f => f.IsLibraryRoot));
        harness.Dialogs.RemoveFolderConfirmed = true;

        await harness.Library.RemoveSelectedFolderCommand.ExecuteAsync(null);

        Assert.Equal(0, await ctx.ScalarAsync("SELECT COUNT(*) FROM library_folders"));
        Assert.Empty(harness.Library.Archives);
        Assert.False(harness.Library.HasFolders);
        Assert.DoesNotContain(harness.Library.FolderRoots, f => f.IsLibraryRoot);
        Assert.Contains("管理フォルダがありません", harness.Library.EmptyMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(ctx.PathOf("a.zip")));
        Assert.True(File.Exists(ctx.PathOf(@"sub\d.zip")));
        Assert.False(harness.Library.IsOperating);
    }

    // ---- キー操作 ----

    [Fact]
    public async Task サムネイル表示では_矢印キーなどで選択を動かせ_動いた先の行が画面に表示される()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var library = harness.Library;
        library.IsThumbnailView = true;
        library.UpdateViewportWidth((LibraryViewModel.ThumbnailCellWidth * 2) + 10); // 1行に2枚
        var rows = new List<int>();
        library.ScrollToRowRequested += (_, row) => rows.Add(row);

        Assert.True(library.HandleKey(Key.Right, ModifierKeys.None)); // 選択がなければ先頭
        Assert.Equal("a.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.Right, ModifierKeys.None));
        Assert.Equal("b.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.Down, ModifierKeys.None)); // 1行ぶん（2枚）下
        Assert.Equal("d.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.Up, ModifierKeys.None));
        Assert.Equal("b.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.Left, ModifierKeys.None));
        Assert.Equal("a.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.Left, ModifierKeys.None)); // 端で止まる
        Assert.Equal("a.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.End, ModifierKeys.None));
        Assert.Equal("d.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.Home, ModifierKeys.None));
        Assert.Equal("a.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.PageDown, ModifierKeys.None)); // 3行ぶんだが、末尾で止まる
        Assert.Equal("d.zip", library.SelectedArchive?.FileName);
        Assert.True(library.HandleKey(Key.PageUp, ModifierKeys.None));
        Assert.Equal("a.zip", library.SelectedArchive?.FileName);

        Assert.Equal(1, library.Archives.Count(a => a.IsSelected));
        Assert.Contains(1, rows); // d.zip は2行目
        Assert.Contains(0, rows);
    }

    [Fact]
    public async Task 詳細表示では_矢印キーは一覧自体に任せ_Enter_F2_Deleteで操作できる()
    {
        using var ctx = new LibraryTestContext();
        using var harness = await OpenLibraryAsync(ctx);
        var library = harness.Library;
        var requested = new List<long>();
        library.OpenRequested += (_, id) => requested.Add(id);

        Assert.False(library.HandleKey(Key.Down, ModifierKeys.None)); // 一覧が処理する
        Assert.False(library.HandleKey(Key.Enter, ModifierKeys.None)); // 選択がなければ何もしない
        Assert.False(library.HandleKey(Key.F2, ModifierKeys.None));
        Assert.False(library.HandleKey(Key.Delete, ModifierKeys.None));

        library.SelectedArchive = Item(harness, "c.zip");
        Assert.True(library.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.Equal([Item(harness, "c.zip").Id], requested);

        harness.Dialogs.RenameInput = null;
        Assert.True(library.HandleKey(Key.F2, ModifierKeys.None));
        await WaitUntilAsync(() => !library.IsOperating);
        Assert.Equal(["c.zip"], harness.Dialogs.RenamePrompts);

        harness.Dialogs.DeleteConfirmed = false;
        Assert.True(library.HandleKey(Key.Delete, ModifierKeys.None));
        await WaitUntilAsync(() => !library.IsOperating);
        Assert.Equal(["c.zip"], harness.Dialogs.DeleteConfirmations);

        Assert.False(library.HandleKey(Key.Delete, ModifierKeys.Control)); // 修飾キー付きは処理しない
        Assert.False(library.HandleKey(Key.A, ModifierKeys.None));
    }
}
