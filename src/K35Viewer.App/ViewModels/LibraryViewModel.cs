using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>並べ替え項目の選択肢。</summary>
public sealed record SortOption(ArchiveSortKey Key, string Label);

/// <summary>
/// ライブラリ画面のViewModel(SCR-01)。フォルダツリー、アーカイブ一覧(詳細 / サムネイル表示)、検索・ソート、
/// 管理フォルダの追加・解除、再スキャン、起動時の更新チェック、サムネイルの生成、選択したアーカイブへの操作
/// （開く、名前変更、移動、削除、エクスプローラーで表示）を扱う。
/// 一覧はSQLiteの検索結果とサムネイルキャッシュだけで表示し、ZIP本体は開かない。
/// </summary>
public sealed partial class LibraryViewModel(
    LibraryService library,
    ScanService scan,
    ThumbnailService thumbnails,
    ThumbnailImageLoader imageLoader,
    IDialogService dialogs,
    ArchiveService archives,
    ILogger<LibraryViewModel> logger,
    SettingsService? settings = null) : ObservableObject, IThumbnailCacheMaintenance, IDisposable
{
    /// <summary>サムネイル表示の1枚の幅(余白を含む)。この幅で1行に並べる枚数を決める。</summary>
    public const double ThumbnailCellWidth = 176;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private static readonly IReadOnlyList<SortOption> AllSortOptions =
    [
        new(ArchiveSortKey.FileName, "ファイル名"),
        new(ArchiveSortKey.LastWriteTime, "更新日時"),
        new(ArchiveSortKey.FileSize, "ファイルサイズ"),
        new(ArchiveSortKey.ImageCount, "画像枚数"),
    ];

    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private CancellationTokenSource? _refreshCts;
    // 取得のたびに新しい辞書を作って差し替える(作成後は変更しない)。作成スレッドからの参照と競合しない
    private Dictionary<long, ArchiveListItem> _itemsById = [];
    // 一覧の取得中に届いた、サムネイル作成の結果。取得した内容は、取得した時点のDBの状態のため、
    // その後に届いた結果を、差し替えた一覧へ当て直す（当て直さないと「作成待ち」のまま残る）。取得中でなければ null
    private Dictionary<long, ArchiveProcessed>? _updatesDuringQuery;
    private readonly Lock _itemsLock = new();

    /// <summary>
    /// テスト用。一覧の取得が終わってから、一覧を差し替えるまでの間に呼ばれる
    /// （その間にサムネイルの作成が終わる場合を、確実に再現するため）。
    /// </summary>
    internal Func<Task>? QueryCompletedForTesting { get; set; }
    private int _runningScans;
    private bool _initialized;
    private bool _hasLoadedArchives;
    private bool _thumbnailLoopRunning;
    private bool _thumbnailRerun;
    private long? _selectedFolderId;
    private long? _selectAfterReloadId;
    private long? _selectFallbackId;

    /// <summary>サムネイル表示の1行分を何行ぶんずつ移動するか（PageUp / PageDown）。</summary>
    public const int PageStepRows = 3;

    public IReadOnlyList<SortOption> SortOptions => AllSortOptions;

    /// <summary>
    /// サムネイル表示で、指定した行（0始まり）を画面に表示してほしい。キーで選択が動いたときに発行する。
    /// </summary>
    public event EventHandler<int>? ScrollToRowRequested;

    /// <summary>左ナビゲーションのツリー。先頭は「すべて」、続いて登録フォルダ。</summary>
    [ObservableProperty]
    private IReadOnlyList<FolderTreeItem> _folderRoots = [];

    /// <summary>中央の一覧(詳細表示)。1万件でも項目ごとの変更通知を出さないよう、検索のたびに丸ごと差し替える。</summary>
    [ObservableProperty]
    private IReadOnlyList<ArchiveListItem> _archives = [];

    /// <summary>サムネイル表示用に、一覧を1行ぶんずつにまとめたもの。サムネイル表示のときだけ作る。</summary>
    [ObservableProperty]
    private IReadOnlyList<ThumbnailRow> _thumbnailRows = [];

    /// <summary>
    /// 選択中のアーカイブ。詳細表示・サムネイル表示で共通（1件だけ選択する）。開く・名前変更・移動・削除・
    /// エクスプローラーで表示の対象になる。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedInExplorerCommand))]
    private ArchiveListItem? _selectedArchive;

    /// <summary>ツリーで選択中の項目。管理フォルダ自身が選ばれているときだけ、「管理対象から外す」ができる。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFolderCommand))]
    [NotifyPropertyChangedFor(nameof(RemoveFolderToolTip))]
    private FolderTreeItem? _selectedFolder;

    /// <summary>名前変更・移動・削除などの操作を実行中か。実行中は、重ねて操作できない。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedInExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFolderCommand))]
    [NotifyPropertyChangedFor(nameof(RemoveFolderToolTip))]
    [NotifyPropertyChangedFor(nameof(ClearBlockedReason))]
    private bool _isOperating;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SortOption _selectedSortOption = AllSortOptions[0];

    [ObservableProperty]
    private bool _isDescending;

    /// <summary>true ならサムネイル表示、false なら詳細表示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailView))]
    private bool _isThumbnailView;

    /// <summary>サムネイル表示で1行に並べる枚数。表示領域の幅から決まる。</summary>
    [ObservableProperty]
    private int _columnCount = 6;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _countText = string.Empty;

    /// <summary>一覧が空のときに表示する案内。空でなければ空文字。</summary>
    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    private bool _hasFolders;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFolderCommand))]
    [NotifyPropertyChangedFor(nameof(RemoveFolderToolTip))]
    [NotifyPropertyChangedFor(nameof(ClearBlockedReason))]
    private bool _isScanning;

    /// <summary>サムネイルを作成中かどうか。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearBlockedReason))]
    private bool _isGeneratingThumbnails;

    /// <summary>サムネイル作成の進捗表示。作成中でなければ空文字。</summary>
    [ObservableProperty]
    private string _thumbnailProgressText = string.Empty;

    /// <summary>詳細表示かどうか(ラジオボタンの双方向バインド用)。</summary>
    public bool IsDetailView
    {
        get => !IsThumbnailView;
        set => IsThumbnailView = !value;
    }

    /// <summary>
    /// 画面表示後に1回だけ呼ぶ。一覧を読み込み、登録フォルダがあれば更新チェック(UC-17)と
    /// 未作成のサムネイルの作成をバックグラウンドで開始する。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        // 前回の表示方式で始める（要件定義 24.4）。ここでは保存しない（_initialized の前のため）
        if (settings is not null)
        {
            IsThumbnailView = settings.Current.LibraryViewMode == LibraryViewMode.Thumbnail;
        }

        _initialized = true;
        thumbnails.ArchiveProcessed += OnArchiveProcessed;
        await ReloadAsync();

        if (HasFolders)
        {
            // 完了を待たない。更新チェック中も、一覧の閲覧・検索ができる
            _ = RunScanAsync(ct => scan.ScanAllAsync(ScanType.Startup, ct));
            StartThumbnailProcessing();
        }
    }

    /// <summary>フォルダツリーと一覧をDBから読み込み直す。</summary>
    public async Task ReloadAsync()
    {
        try
        {
            await LoadTreeAsync(_cts.Token);
            await RefreshArchivesAsync(null);
        }
        catch (OperationCanceledException)
        {
            // アプリ終了中
        }
        catch (Exception ex)
        {
            LogLoadFailed(logger, ex);
        }
    }

    /// <summary>
    /// アプリ内の操作でアーカイブが変更された（画像を削除してZIPを更新した）あとに呼ぶ。
    /// 一覧を読み込み直し、再生成待ちになったサムネイルの作成を始める。
    /// </summary>
    public async Task OnArchiveChangedAsync()
    {
        await ReloadAsync();
        StartThumbnailProcessing();
    }

    /// <summary>ツリーで項目が選択されたときにViewから呼ばれる。</summary>
    public void OnFolderSelected(FolderTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        SelectedFolder = item;
        if (item.Id == _selectedFolderId)
        {
            return;
        }

        _selectedFolderId = item.Id;
        _ = RefreshArchivesAsync(null);
    }

    /// <summary>サムネイル表示の領域の幅が変わったときにViewから呼ばれる。1行に並べる枚数を決め直す。</summary>
    public void UpdateViewportWidth(double width)
    {
        ColumnCount = Math.Max(1, (int)(width / ThumbnailCellWidth));
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = dialogs.PickFolder();
        if (path is null)
        {
            return;
        }

        try
        {
            var result = await library.AddFolderAsync(path, confirmMerge: false, _cts.Token);
            if (result.Outcome == AddFolderOutcome.MergeRequired)
            {
                if (!dialogs.ConfirmMerge(path, result.MergeTargets))
                {
                    return;
                }

                result = await library.AddFolderAsync(path, confirmMerge: true, _cts.Token);
            }

            if (result.Outcome != AddFolderOutcome.Added)
            {
                return;
            }

            var libraryFolderId = result.LibraryFolderId!.Value;
            await ReloadAsync();

            // スキャンは待たずに開始する。UIはスキャン中も操作できる。
            _ = RunScanAsync(ct => scan.ScanAsync(libraryFolderId, ScanType.Initial, ct));
        }
        catch (OperationCanceledException)
        {
            // アプリ終了中
        }
        catch (Exception ex)
        {
            LogAddFailed(logger, ex, path);
        }
    }

    /// <summary>アーカイブを開く操作が行われた（ダブルクリック、Enter）。画面の切り替えは呼び出し側が行う。</summary>
    public event EventHandler<long>? OpenRequested;

    [RelayCommand]
    private void OpenArchive(ArchiveListItem? item)
    {
        if (item is not null)
        {
            OpenRequested?.Invoke(this, item.Id);
        }
    }

    /// <summary>
    /// 今の一覧の並び（選んでいるフォルダ、検索、並べ替えが反映されたもの）で、<paramref name="archiveId"/> の前後のアーカイブを探す。
    /// ビューアで開いたまま、次のアーカイブへ移るために使う。開けないことが分かっているもの（壊れている、画像がない、暗号化）は飛ばす。
    /// 見つからなかった場合は null を返し、<paramref name="message"/> に、利用者へ知らせる理由を入れる。
    /// 端で止まる（ぐるっと回らず、別のフォルダへも飛ばない）。「すべて」を選んでいるときは、全フォルダの1本の並びを進む。
    /// </summary>
    /// <param name="archiveId">今開いているアーカイブ。</param>
    /// <param name="direction">+1 が次、-1 が前。</param>
    public ArchiveListItem? FindNeighbor(long archiveId, int direction, out string message)
    {
        var list = Archives;
        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == archiveId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            message = "今のアーカイブが、ライブラリの一覧に見つかりません。一覧に戻って、開き直してください";
            return null;
        }

        var step = direction >= 0 ? 1 : -1;
        for (var i = index + step; i >= 0 && i < list.Count; i += step)
        {
            if (!list[i].IsKnownUnopenable)
            {
                message = string.Empty;
                return list[i];
            }
        }

        message = step > 0
            ? "これより後に、開けるアーカイブはありません"
            : "これより前に、開けるアーカイブはありません";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => RunScanAsync(ct => scan.ScanAllAsync(ScanType.Manual, ct));

    private bool CanRescan() => HasFolders && !IsScanning;

    // ---- 選択したアーカイブへの操作（UC-12〜15） ----

    private bool CanOperateOnSelection() => SelectedArchive is not null && !IsOperating;

    /// <summary>選択中のアーカイブを開く（Enter）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private void OpenSelected() => OpenArchive(SelectedArchive);

    /// <summary>選択中のアーカイブの名前を変更する（F2、UC-12）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private Task RenameSelectedAsync() => RunOperationAsync(async () =>
    {
        var item = SelectedArchive!;
        var newName = dialogs.PromptRename(item.FileName, text => archives.GetRenameError(item.Location, item.FileName, text));
        if (newName is null)
        {
            return false;
        }

        _selectAfterReloadId = item.Id;
        var result = await archives.RenameAsync(item.Id, newName, _cts.Token);
        return result.Status == OperationStatus.Success;
    });

    /// <summary>選択中のアーカイブを別のフォルダへ移動する（UC-13）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private Task MoveSelectedAsync() => RunOperationAsync(async () =>
    {
        var item = SelectedArchive!;
        var destination = dialogs.PickMoveDestination(item.Location);
        if (destination is null)
        {
            return false;
        }

        _selectAfterReloadId = item.Id;
        _selectFallbackId = NeighborId(item);
        var result = await archives.MoveAsync(item.Id, destination, _cts.Token);
        return result.Status is OperationStatus.Success or OperationStatus.Partial;
    });

    /// <summary>
    /// 選択中のアーカイブを削除する（Delete、UC-14）。通常は確認のうえ、ごみ箱へ移動する。
    /// ごみ箱を使えない保存先では、元に戻せないことを示して確認し、承認された場合だけ完全に削除する。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private Task DeleteSelectedAsync() => RunOperationAsync(async () =>
    {
        var item = SelectedArchive!;
        var blocked = archives.GetRecycleBlockReason(item.FullPath);
        // ごみ箱へ移す確認は、設定でオフにできる。元に戻せない完全削除の確認は、設定によらず必ず行う（要件定義 24.3）
        var confirmed = blocked is null
            ? settings?.Current.ConfirmBeforeDelete == false || dialogs.ConfirmDeleteArchive(item.FileName)
            : dialogs.ConfirmPermanentDelete(item.FileName, blocked);
        if (!confirmed)
        {
            return false;
        }

        _selectAfterReloadId = item.Id;
        _selectFallbackId = NeighborId(item);
        var result = await archives.DeleteAsync(item.Id, permanent: blocked is not null, _cts.Token);
        return result.Status is OperationStatus.Success or OperationStatus.Partial;
    });

    /// <summary>選択中のアーカイブを、エクスプローラーで選択した状態で表示する（UC-15）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private Task ShowSelectedInExplorerAsync() => RunOperationAsync(async () =>
    {
        _ = await archives.ShowInExplorerAsync(SelectedArchive!.Id, _cts.Token);
        return false;
    });

    /// <summary>「管理対象から外す」ボタンのツールチップ。押せないときは、その理由を示す。</summary>
    public string RemoveFolderToolTip =>
        IsScanning || IsOperating ? "スキャンや操作の実行中は外せません。終わってから操作してください"
        : SelectedFolder is not { IsLibraryRoot: true } ? "管理フォルダ（ツリーの最上位のフォルダ）を選ぶと外せます"
        : "選択中の管理フォルダを、管理対象から外します。ファイルは削除されません";

    private bool CanRemoveSelectedFolder() => SelectedFolder is { IsLibraryRoot: true } && !IsScanning && !IsOperating;

    /// <summary>
    /// 選択中の管理フォルダを管理対象から外す（UC-18）。ファイルは削除しない。スキャン中は実行できない。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedFolder))]
    private Task RemoveSelectedFolderAsync() => RunOperationAsync(async () =>
    {
        if (SelectedFolder is not { LibraryFolderId: { } libraryFolderId, FullPath: { } path })
        {
            return false;
        }

        var count = await library.GetArchiveCountAsync(libraryFolderId, _cts.Token) ?? 0;
        if (!dialogs.ConfirmRemoveLibraryFolder(path, count))
        {
            return false;
        }

        var result = await library.RemoveFolderAsync(libraryFolderId, _cts.Token);
        return result.Status == OperationStatus.Success;
    });

    /// <summary>
    /// 操作を実行する。実行中は重ねて実行できない。操作が成功した場合（<paramref name="operation"/> が true を返した場合）は、
    /// 一覧とフォルダツリーを読み込み直す。
    /// </summary>
    private async Task RunOperationAsync(Func<Task<bool>> operation)
    {
        if (IsOperating)
        {
            return;
        }

        IsOperating = true;
        try
        {
            if (await operation())
            {
                await ReloadAsync();
                StartThumbnailProcessing();
            }
        }
        catch (OperationCanceledException)
        {
            // アプリ終了中
        }
        catch (Exception ex)
        {
            LogOperationFailed(logger, ex);
        }
        finally
        {
            _selectAfterReloadId = null;
            _selectFallbackId = null;
            IsOperating = false;
        }
    }

    /// <summary>削除・移動で一覧から消える項目の、次に選ぶ項目（後ろ、なければ前）。</summary>
    private long? NeighborId(ArchiveListItem item)
    {
        var index = Archives.ToList().FindIndex(a => a.Id == item.Id);
        if (index < 0)
        {
            return null;
        }

        return index + 1 < Archives.Count ? Archives[index + 1].Id : index > 0 ? Archives[index - 1].Id : null;
    }

    // ---- 画面からの入力 ----

    /// <summary>サムネイルのカードがクリックされたときにViewから呼ばれる。選択し、ダブルクリックなら開く。</summary>
    public void OnThumbnailClicked(ArchiveListItem item, int clickCount)
    {
        ArgumentNullException.ThrowIfNull(item);

        SelectedArchive = item;
        if (clickCount >= 2)
        {
            OpenArchive(item);
        }
    }

    /// <summary>一覧の項目が右クリックされたときにViewから呼ばれる。メニューを開く前に、その項目を選択する。</summary>
    public void OnArchiveRightClicked(ArchiveListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        SelectedArchive = item;
    }

    /// <summary>
    /// 一覧でのキー操作を処理する。処理した場合は true。
    /// 共通: Enter で開く、F2 で名前変更、Delete で削除。
    /// サムネイル表示: 矢印キーで選択を移動（上下は1行分）、PageUp / PageDown で3行分、Home / End で先頭 / 末尾。
    /// （詳細表示の矢印キーによる選択は、一覧自体が処理する。）
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt))
        {
            return false;
        }

        switch (key)
        {
            case Key.Enter when SelectedArchive is not null:
                OpenSelectedCommand.Execute(null);
                return true;
            case Key.F2 when SelectedArchive is not null:
                RenameSelectedCommand.Execute(null);
                return true;
            case Key.Delete when SelectedArchive is not null:
                DeleteSelectedCommand.Execute(null);
                return true;
        }

        if (!IsThumbnailView || Archives.Count == 0)
        {
            return false;
        }

        var columns = Math.Max(1, ColumnCount);
        switch (key)
        {
            case Key.Right:
                MoveSelection(1);
                return true;
            case Key.Left:
                MoveSelection(-1);
                return true;
            case Key.Down:
                MoveSelection(columns);
                return true;
            case Key.Up:
                MoveSelection(-columns);
                return true;
            case Key.PageDown:
                MoveSelection(columns * PageStepRows);
                return true;
            case Key.PageUp:
                MoveSelection(-columns * PageStepRows);
                return true;
            case Key.Home:
                SelectIndex(0);
                return true;
            case Key.End:
                SelectIndex(Archives.Count - 1);
                return true;
            default:
                return false;
        }
    }

    // 何も選ばれていなければ、移動の向きに応じて先頭または末尾を選ぶ
    private void MoveSelection(int delta)
    {
        var current = SelectedArchive is { } selected ? Archives.ToList().FindIndex(a => a.Id == selected.Id) : -1;
        SelectIndex(current < 0 ? (delta > 0 ? 0 : Archives.Count - 1) : Math.Clamp(current + delta, 0, Archives.Count - 1));
    }

    private void SelectIndex(int index)
    {
        SelectedArchive = Archives[index];
        ScrollToRowRequested?.Invoke(this, index / Math.Max(1, ColumnCount));
    }

    partial void OnSelectedArchiveChanged(ArchiveListItem? oldValue, ArchiveListItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    public void Dispose()
    {
        thumbnails.ArchiveProcessed -= OnArchiveProcessed;
        _cts.Cancel();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _cts.Dispose();
    }

    partial void OnSearchTextChanged(string value) => _ = RefreshArchivesAsync(SearchDebounce);

    partial void OnSelectedSortOptionChanged(SortOption value) => _ = RefreshArchivesAsync(null);

    partial void OnIsDescendingChanged(bool value) => _ = RefreshArchivesAsync(null);

    partial void OnArchivesChanged(IReadOnlyList<ArchiveListItem> value) => RebuildThumbnailRows();

    partial void OnColumnCountChanged(int value) => RebuildThumbnailRows();

    partial void OnIsThumbnailViewChanged(bool value)
    {
        RebuildThumbnailRows();

        // 切り替えた時点で記憶する（次に起動したときも、この表示方式で始める）
        if (_initialized)
        {
            var mode = value ? LibraryViewMode.Thumbnail : LibraryViewMode.Detail;
            settings?.Update(s => s with { LibraryViewMode = mode });
        }
    }

    /// <inheritdoc />
    public string? ClearBlockedReason =>
        IsScanning ? "スキャン中は削除できません。終わってから操作してください"
        : IsGeneratingThumbnails ? "サムネイルの作成中は削除できません。終わってから操作してください"
        : IsOperating ? "他の操作の実行中は削除できません。終わってから操作してください"
        : null;

    /// <inheritdoc />
    public Task<long> GetCacheSizeAsync(CancellationToken ct = default) => thumbnails.GetCacheSizeAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ClearCacheAsync()
    {
        if (ClearBlockedReason is not null || !await thumbnails.ClearCacheAsync(_cts.Token))
        {
            return false;
        }

        // 記録を消したため、一覧は作成待ちの表示に戻る。順に作り直す
        await ReloadAsync();
        StartThumbnailProcessing();
        return true;
    }

    /// <summary>設定画面を開く（画面一覧 8.3）。</summary>
    [RelayCommand]
    private void OpenSettings() => dialogs.ShowSettings();

    /// <summary>
    /// サムネイル表示のときだけ、一覧を1行ずつにまとめる。詳細表示に戻したときは破棄する
    /// (使っていない間は、行のオブジェクトを保持しない)。
    /// </summary>
    private void RebuildThumbnailRows()
    {
        if (!IsThumbnailView)
        {
            ThumbnailRows = [];
            return;
        }

        var columns = Math.Max(1, ColumnCount);
        var source = Archives;
        var rows = new List<ThumbnailRow>((source.Count + columns - 1) / columns);
        for (var start = 0; start < source.Count; start += columns)
        {
            var count = Math.Min(columns, source.Count - start);
            rows.Add(new ThumbnailRow(new ArraySegmentList<ArchiveListItem>(source, start, count)));
        }

        ThumbnailRows = rows;
    }

    private async Task LoadTreeAsync(CancellationToken ct)
    {
        var nodes = await library.GetFolderTreeAsync(ct);

        // 再構築しても、展開状態と選択を維持する
        var previousRoots = FolderRoots;
        var expanded = new HashSet<long>();
        var previousRootIds = new HashSet<long>();
        foreach (var root in previousRoots)
        {
            root.CollectExpandedIds(expanded);
            if (root.Id is { } id)
            {
                previousRootIds.Add(id);
            }
        }

        var all = FolderTreeItem.CreateAll();
        var roots = new List<FolderTreeItem> { all };
        roots.AddRange(nodes.Select(FolderTreeItem.From));

        foreach (var root in roots)
        {
            root.ApplyExpandedIds(expanded);

            // 新しく現れた登録フォルダは展開して見せる
            if (root.Id is { } rootId && !previousRootIds.Contains(rootId))
            {
                root.IsExpanded = true;
            }
        }

        var target = _selectedFolderId is { } selectedId
            ? roots.Select(r => r.Find(selectedId)).FirstOrDefault(found => found is not null)
            : null;
        if (target is null)
        {
            // 選択していたフォルダがなくなった場合は「すべて」へ戻す
            _selectedFolderId = null;
            target = all;
        }

        target.IsSelected = true;
        SelectedFolder = target;
        HasFolders = nodes.Count > 0;
        FolderRoots = roots;
    }

    /// <summary>
    /// 現在の条件で一覧を取得し直す。連続して呼ばれた場合は、前の取得をキャンセルして最後のものだけを反映する。
    /// delay を指定すると、その時間だけ待ってから取得する(検索入力のデバウンス)。
    /// </summary>
    private async Task RefreshArchivesAsync(TimeSpan? delay)
    {
        var previous = _refreshCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _refreshCts = cts;
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        Dictionary<long, ArchiveProcessed> updates = [];
        try
        {
            if (delay is { } wait)
            {
                await Task.Delay(wait, cts.Token);
            }

            var query = new ArchiveQuery(
                _selectedFolderId,
                SearchText,
                SelectedSortOption.Key,
                IsDescending);
            lock (_itemsLock)
            {
                _updatesDuringQuery = updates;
            }

            var result = await library.QueryArchivesAsync(query, cts.Token);
            if (QueryCompletedForTesting is { } hook)
            {
                await hook();
            }

            cts.Token.ThrowIfCancellationRequested();

            var items = result.Items.Select(item => new ArchiveListItem(item, imageLoader)).ToList();
            var byId = items.ToDictionary(item => item.Id);
            lock (_itemsLock)
            {
                // 取得中に届いた結果を当ててから差し替える。差し替えたあとに届く結果は、新しい辞書の項目へ当たる
                foreach (var (archiveId, update) in updates)
                {
                    if (byId.TryGetValue(archiveId, out var item))
                    {
                        item.Apply(update);
                    }
                }

                _itemsById = byId;
            }

            Archives = items;

            // 取得し直しても、選択を維持する（操作の直後は、操作の対象や、次の項目を選ぶ）
            // 操作した項目が一覧にあればそれを、なければ（削除した、別のフォルダへ移したなど）次の項目を選ぶ
            var keepId = _selectAfterReloadId ?? SelectedArchive?.Id;
            var fallbackId = _selectFallbackId;
            _selectAfterReloadId = null;
            _selectFallbackId = null;
            SelectedArchive = keepId is { } id && _itemsById.TryGetValue(id, out var kept) ? kept
                : fallbackId is { } next && _itemsById.TryGetValue(next, out var neighbor) ? neighbor
                : null;
            TotalCount = result.TotalCount;
            CountText = string.Create(CultureInfo.CurrentCulture, $"{result.TotalCount:N0} 件");
            _hasLoadedArchives = true;
            UpdateEmptyMessage();
        }
        catch (OperationCanceledException)
        {
            // 新しい取得に置き換えられた、またはアプリ終了中
        }
        catch (Exception ex)
        {
            LogQueryFailed(logger, ex);
        }
        finally
        {
            // キャンセル・失敗で差し替えなかった場合も、記録をやめる（新しい取得が始まっていれば、そちらの記録は残す）
            lock (_itemsLock)
            {
                if (ReferenceEquals(_updatesDuringQuery, updates))
                {
                    _updatesDuringQuery = null;
                }
            }
        }
    }

    private void UpdateEmptyMessage()
    {
        if (!_hasLoadedArchives)
        {
            EmptyMessage = string.Empty;
        }
        else if (!HasFolders)
        {
            EmptyMessage = "管理フォルダがありません。「フォルダを追加」から登録してください。";
        }
        else
        {
            EmptyMessage = TotalCount == 0 ? "該当するアーカイブがありません" : string.Empty;
        }
    }

    private async Task RunScanAsync(Func<CancellationToken, Task> work)
    {
        _runningScans++;
        IsScanning = true;
        try
        {
            await Task.Run(() => work(_cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // アプリ終了によるキャンセル
        }
        catch (Exception ex)
        {
            LogScanFailed(logger, ex);
        }
        finally
        {
            _runningScans--;
            IsScanning = _runningScans > 0;
        }

        await ReloadAsync();

        // 新規・変更のアーカイブのサムネイルを作る
        StartThumbnailProcessing();
    }

    /// <summary>
    /// サムネイルの作成を開始する。作成中に再度呼ばれた場合は、終わったあとにもう一度確認する
    /// (作成中に新しく見つかったアーカイブを取りこぼさないため)。
    /// </summary>
    private void StartThumbnailProcessing()
    {
        if (_thumbnailLoopRunning)
        {
            _thumbnailRerun = true;
            return;
        }

        _ = RunThumbnailLoopAsync();
    }

    private async Task RunThumbnailLoopAsync()
    {
        _thumbnailLoopRunning = true;
        IsGeneratingThumbnails = true;
        try
        {
            // 報告は、UIスレッドへ順序を保って届ける（作成の終了後に、古い報告が表示を書き戻さないようにする）
            var progress = new ContextProgress<ThumbnailProgress>(
                _uiContext,
                p => ThumbnailProgressText = p.Total == 0
                    ? string.Empty
                    : string.Create(CultureInfo.CurrentCulture, $"サムネイルを作成中… {p.Completed:N0} / {p.Total:N0}"));

            do
            {
                _thumbnailRerun = false;
                await Task.Run(() => thumbnails.ProcessPendingAsync(progress, _cts.Token), _cts.Token);
            }
            while (_thumbnailRerun && !_cts.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // アプリ終了中
        }
        catch (Exception ex)
        {
            LogThumbnailsFailed(logger, ex);
        }
        finally
        {
            IsGeneratingThumbnails = false;
            ThumbnailProgressText = string.Empty;
            _thumbnailLoopRunning = false;
        }
    }

    // 作成スレッドから呼ばれるため、UIスレッドへ切り替えて、表示中の該当行だけを更新する
    private void OnArchiveProcessed(object? sender, ArchiveProcessed update)
    {
        void Apply()
        {
            ArchiveListItem? item;
            lock (_itemsLock)
            {
                // 一覧の取得中なら、取得し終えた一覧にも当てられるよう記録する（同じアーカイブは、後の結果で上書き）
                _updatesDuringQuery?[update.ArchiveId] = update;
                _ = _itemsById.TryGetValue(update.ArchiveId, out item);
            }

            item?.Apply(update);
        }

        if (_uiContext is null)
        {
            Apply();
        }
        else
        {
            _uiContext.Post(static state => ((Action)state!)(), (Action)Apply);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "フォルダツリーの読み込みに失敗しました。")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "アーカイブ一覧の取得に失敗しました。")]
    private static partial void LogQueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "アーカイブまたは管理フォルダに対する操作で、想定外のエラーが起きました。")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "管理フォルダの追加に失敗しました。Path={Path}")]
    private static partial void LogAddFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "スキャンに失敗しました。")]
    private static partial void LogScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "サムネイルの作成に失敗しました。")]
    private static partial void LogThumbnailsFailed(ILogger logger, Exception exception);
}

/// <summary>
/// 進捗の報告を、指定したコンテキスト（UIスレッド）へ、報告した順序のまま届ける。
/// コンテキストがない場合は、報告したスレッドで同期的に処理する。
/// <see cref="Progress{T}"/> は、コンテキストがないとスレッドプールへ投げるため、順序が保証されない。
/// </summary>
internal sealed class ContextProgress<T>(SynchronizationContext? context, Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        if (context is null)
        {
            handler(value);
        }
        else
        {
            context.Post(static state => ((Action)state!)(), () => handler(value));
        }
    }
}

/// <summary>元のリストの一部を、コピーせずに <see cref="IReadOnlyList{T}"/> として見せる。</summary>
internal sealed class ArraySegmentList<T>(IReadOnlyList<T> source, int start, int count) : IReadOnlyList<T>
{
    public int Count => count;

    public T this[int index] => index >= 0 && index < count
        ? source[start + index]
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < count; i++)
        {
            yield return source[start + i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
