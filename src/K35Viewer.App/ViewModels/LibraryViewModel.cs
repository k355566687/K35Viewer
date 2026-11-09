using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>並べ替え項目の選択肢。</summary>
public sealed record SortOption(ArchiveSortKey Key, string Label);

/// <summary>
/// ライブラリ画面のViewModel(SCR-01)。フォルダツリー、アーカイブ一覧(詳細 / サムネイル表示)、検索・ソート、
/// 管理フォルダの追加、再スキャン、起動時の更新チェック、サムネイルの生成を扱う。
/// 一覧はSQLiteの検索結果とサムネイルキャッシュだけで表示し、ZIP本体は開かない。
/// </summary>
public sealed partial class LibraryViewModel(
    LibraryService library,
    ScanService scan,
    ThumbnailService thumbnails,
    ThumbnailImageLoader imageLoader,
    IDialogService dialogs,
    ILogger<LibraryViewModel> logger) : ObservableObject, IDisposable
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
    private int _runningScans;
    private bool _initialized;
    private bool _hasLoadedArchives;
    private bool _thumbnailLoopRunning;
    private bool _thumbnailRerun;
    private long? _selectedFolderId;

    public IReadOnlyList<SortOption> SortOptions => AllSortOptions;

    /// <summary>左ナビゲーションのツリー。先頭は「すべて」、続いて登録フォルダ。</summary>
    [ObservableProperty]
    private IReadOnlyList<FolderTreeItem> _folderRoots = [];

    /// <summary>中央の一覧(詳細表示)。1万件でも項目ごとの変更通知を出さないよう、検索のたびに丸ごと差し替える。</summary>
    [ObservableProperty]
    private IReadOnlyList<ArchiveListItem> _archives = [];

    /// <summary>サムネイル表示用に、一覧を1行ぶんずつにまとめたもの。サムネイル表示のときだけ作る。</summary>
    [ObservableProperty]
    private IReadOnlyList<ThumbnailRow> _thumbnailRows = [];

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
    private bool _isScanning;

    /// <summary>サムネイルを作成中かどうか。</summary>
    [ObservableProperty]
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

    /// <summary>ツリーで項目が選択されたときにViewから呼ばれる。</summary>
    public void OnFolderSelected(FolderTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

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

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => RunScanAsync(ct => scan.ScanAllAsync(ScanType.Manual, ct));

    private bool CanRescan() => HasFolders && !IsScanning;

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

    partial void OnIsThumbnailViewChanged(bool value) => RebuildThumbnailRows();

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
            var result = await library.QueryArchivesAsync(query, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            var items = result.Items.Select(item => new ArchiveListItem(item, imageLoader)).ToList();
            _itemsById = items.ToDictionary(item => item.Id);
            Archives = items;
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
            if (_itemsById.TryGetValue(update.ArchiveId, out var item))
            {
                item.Apply(update);
            }
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
