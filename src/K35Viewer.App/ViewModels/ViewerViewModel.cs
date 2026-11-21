using System.Globalization;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Infrastructure.Settings;
using K35Viewer.Infrastructure.Thumbnails;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>
/// 画像を表紙にする求め（Issue #15）。Index と EntryName は、開いているアーカイブでの画像の位置と名前。
/// Crop が true なら、切り抜きの画面を出してから表紙にする。
/// </summary>
public sealed record CoverRequest(long ArchiveId, int Index, string EntryName, bool Crop);

/// <summary>画像の拡大率の決め方。</summary>
public enum ZoomMode
{
    /// <summary>ウィンドウに収まる大きさにする。小さい画像は拡大しない。</summary>
    Fit,

    /// <summary>原寸（画像の1画素を画面の1画素で表示）。</summary>
    Actual,

    /// <summary>段階式の拡大・縮小で選んだ倍率。</summary>
    Custom,
}

/// <summary>
/// ビューア画面（SCR-02）のViewModel。1枚表示と一覧表示（サムネイルグリッド）、前後移動、拡大・縮小、
/// フィット・原寸、全画面、画像の選択、最終閲覧位置の記録を扱う。
/// 「現在位置」は、1枚表示と一覧表示で同じ値（<see cref="ViewerSession.CurrentIndex"/>）を使う。
/// 画像の表示はデコード済みの画像を受け取るだけで、ZIPを直接操作しない。
/// </summary>
public sealed partial class ViewerViewModel(
    ViewerService viewerService,
    ViewerImageDecoder decoder,
    ThumbnailGenerator thumbnailGenerator,
    ILogger<ViewerViewModel> logger,
    SettingsService? settings = null) : ObservableObject, IDisposable
{
    /// <summary>一覧表示の1枚の幅（余白を含む）。この幅で1行に並べる枚数を決める。</summary>
    public const double PageCellWidth = 176;

    /// <summary>一覧表示で、PageUp / PageDown が動く行数。</summary>
    public const int PageStepRows = 3;

    private const string SingleViewHelp =
        "←/→ 前後の頁　↑/↓ 前後のアーカイブ　+/− 拡大・縮小　0 フィット　1 原寸　Delete 削除予約　G 一覧表示　F11 全画面　Esc ライブラリへ戻る";

    private const string ListViewHelp =
        "矢印キー 移動　Shift/Ctrl+クリック 複数選択　Space 選択の切替　Ctrl+A 全選択　Delete 削除予約/解除　Enter 開く　Ctrl+↑/↓ 前後のアーカイブ　G 1枚表示　Esc ライブラリへ戻る";

    /// <summary>拡大・縮小の段階。</summary>
    private static readonly double[] ZoomSteps =
        [0.1, 0.15, 0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0];

    /// <summary>頁を移動してから、最後に閲覧した位置を保存するまでの待ち時間（めくるたびに書き込まないため）。</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly HashSet<int> _selected = [];
    private ViewerSession? _session;
    private PageImageLoader? _loader;
    private PageThumbnailSource? _thumbnailSource;
    private CancellationTokenSource? _saveCts;
    private bool _positionSaveSuppressed;
    private int _showVersion;
    private int _savedIndex = -1;
    private int _anchorIndex;
    private int _markedCurrentIndex = -1;
    private int _contextIndex = -1;
    private ZoomMode _zoomMode = ZoomMode.Fit;
    private double _customScale = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _dpiScale = 1.0;

    /// <summary>
    /// アーカイブを開いた（引数は Archive ID）。「新着」の印は、開くときにDBから外している。
    /// 呼び出し側（<see cref="MainViewModel"/>）が、ライブラリの一覧の表示から、印を外す。
    /// </summary>
    public event EventHandler<long>? ArchiveOpened;

    /// <summary>ライブラリへ戻る操作が行われた（Esc、戻るボタン）。画面の切り替えは呼び出し側が行う。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 前後のアーカイブへ移る操作が行われた（↑ / ↓）。値は向き（+1 が次、-1 が前）。
    /// ライブラリの一覧の並びでの前後を、呼び出し側（<see cref="MainViewModel"/>）が決めて、開く。
    /// </summary>
    public event EventHandler<int>? NavigateArchiveRequested;

    /// <summary>
    /// 画像の削除予約ができなかった（RAR / 7z は、閲覧だけで、作り直せない）。値は、利用者へ知らせる理由。
    /// 通知は、呼び出し側（<see cref="MainViewModel"/>）が出す。
    /// </summary>
    public event EventHandler<string>? DeleteBlocked;

    /// <summary>
    /// 開いている RAR / 7z を ZIP に変換する操作が行われた（ツールバーの「ZIP に変換」）。
    /// 確認、閉じる、変換、開き直す、の流れは、呼び出し側（<see cref="MainViewModel"/>）が行う。
    /// </summary>
    public event EventHandler? ConvertToZipRequested;

    /// <summary>
    /// 画像を、アーカイブの表紙にする操作が行われた（右クリックの「この画像を表紙にする」「表紙を切り抜く…」。Issue #15）。
    /// 切り抜きの画面と、表紙の記録は、呼び出し側（<see cref="MainViewModel"/>）が行う。
    /// </summary>
    public event EventHandler<CoverRequest>? CoverRequested;

    /// <summary>
    /// 一覧表示で、指定した行（0始まり）を画面に表示してほしい。現在位置が動いたとき、または一覧表示に切り替えたときに発行する。
    /// </summary>
    public event EventHandler<int>? ScrollToRowRequested;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _archiveName = string.Empty;

    /// <summary>開いているアーカイブが、閲覧だけの形式（RAR / 7z）か。ツールバーに「ZIP に変換」を出す。</summary>
    [ObservableProperty]
    private bool _isReadOnlyArchive;

    /// <summary>「42 / 186」の形式の現在位置。</summary>
    [ObservableProperty]
    private string _positionText = string.Empty;

    /// <summary>現在の画像のアーカイブ内の名前。</summary>
    [ObservableProperty]
    private string _entryName = string.Empty;

    [ObservableProperty]
    private BitmapSource? _currentImage;

    /// <summary>現在の画像を読み込めなかったときの説明。読み込めていれば空。</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _zoomText = "フィット";

    /// <summary>画像の表示サイズ（画面上の論理単位）。ウィンドウの拡大率（DPI）を反映した値。</summary>
    [ObservableProperty]
    private double _displayWidth;

    [ObservableProperty]
    private double _displayHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolbar))]
    private bool _isFullScreen;

    /// <summary>true なら一覧表示（サムネイルグリッド）、false なら1枚表示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleView))]
    [NotifyPropertyChangedFor(nameof(ViewModeToggleText))]
    [NotifyPropertyChangedFor(nameof(HelpText))]
    private bool _isListView;

    /// <summary>アーカイブ内のすべての画像（一覧表示の1枚ずつ）。アーカイブを開いている間だけ入る。</summary>
    [ObservableProperty]
    private IReadOnlyList<PageItem> _pages = [];

    /// <summary>一覧表示用に、画像を1行ぶんずつにまとめたもの。一覧表示のときだけ作る。</summary>
    [ObservableProperty]
    private IReadOnlyList<PageRow> _pageRows = [];

    /// <summary>一覧表示で1行に並べる枚数。表示領域の幅から決まる。</summary>
    [ObservableProperty]
    private int _columnCount = 6;

    /// <summary>選択されている画像の数。</summary>
    [ObservableProperty]
    private int _selectedCount;

    /// <summary>「選択 3枚」の形式。選択がなければ空。</summary>
    [ObservableProperty]
    private string _selectionText = string.Empty;

    /// <summary>削除予約されている画像の数。0 でなければ、アーカイブを離れるときに確認が必要。</summary>
    [ObservableProperty]
    private int _pendingDeleteCount;

    /// <summary>「削除予定 3枚」の形式。削除予約がなければ空。</summary>
    [ObservableProperty]
    private string _pendingDeleteText = string.Empty;

    /// <summary>現在の画像が削除予約されているか（一覧から、予約済みの画像を1枚表示した場合などに表示する）。</summary>
    [ObservableProperty]
    private bool _isCurrentPendingDelete;

    /// <summary>上部のツールバーを表示するか。全画面では画像を最大に使うため隠す。</summary>
    public bool ShowToolbar => !IsFullScreen;

    public bool IsSingleView => !IsListView;

    /// <summary>表示方式を切り替えるボタンの文字（切り替え先を示す）。</summary>
    public string ViewModeToggleText => IsListView ? "1枚表示（G）" : "一覧表示（G）";

    /// <summary>キー操作の案内。表示方式によって異なる。</summary>
    public string HelpText => IsListView ? ListViewHelp : SingleViewHelp;

    public ZoomMode ZoomMode => _zoomMode;

    /// <summary>現在の拡大率（画像の1画素を画面の何画素で表示するか）。画像がなければ 1。</summary>
    public double EffectiveScale { get; private set; } = 1.0;

    /// <summary>選択されている画像の位置（昇順）。削除予約などの操作の対象になる。</summary>
    public IReadOnlyList<int> SelectedIndices => [.. _selected.Order()];

    /// <summary>開いているアーカイブの閲覧状態。開いていなければ null。ZIP更新の依頼を作るために使う。</summary>
    public ViewerSession? Session => _session;

    /// <summary>
    /// アーカイブの更新（削除予約の反映）を始める前に呼ぶ。更新が終わるまで、閲覧位置を保存しない
    /// （更新で決めた位置を、更新前の古い位置で上書きしないため）。
    /// </summary>
    public void SuspendPositionSaving()
    {
        _positionSaveSuppressed = true;
        _saveCts?.Cancel();
    }

    /// <summary>更新が失敗・キャンセルされ、ビューアで閲覧を続けるときに呼ぶ。閲覧位置の保存を再開する。</summary>
    public void ResumePositionSaving()
    {
        _positionSaveSuppressed = false;
        ScheduleSave();
    }

    /// <summary>
    /// アーカイブの更新（削除予約の反映）が済んだことを伝える。アーカイブの内容が変わったため、削除予約の表示をなくす。
    /// 閲覧位置の保存は止めたままにする。呼び出し側は、このあとビューアを閉じる。
    /// </summary>
    public void MarkArchiveUpdated()
    {
        if (_session is not { } session)
        {
            return;
        }

        _positionSaveSuppressed = true;
        foreach (var index in session.PendingDeleteIndices)
        {
            if (index < Pages.Count)
            {
                Pages[index].IsPendingDelete = false;
            }
        }

        session.ClearPendingDelete();
        PendingDeleteCount = 0;
        PendingDeleteText = string.Empty;
        IsCurrentPendingDelete = false;
    }

    /// <summary>
    /// アーカイブを開いて最初の頁を表示する。開けなかった場合（理由はトーストで通知済み）は false。
    /// すでに開いている場合は何もせず false。
    /// </summary>
    public async Task<bool> OpenAsync(long archiveId, CancellationToken ct = default)
    {
        if (_session is not null)
        {
            return false;
        }

        var result = await viewerService.OpenAsync(archiveId, ct);
        if (result.Session is not { } session)
        {
            return false;
        }

        _session = session;
        ArchiveOpened?.Invoke(this, archiveId);
        _loader = new PageImageLoader(session, decoder);
        _thumbnailSource = new PageThumbnailSource(session, thumbnailGenerator);
        _positionSaveSuppressed = false;
        _savedIndex = -1;
        _markedCurrentIndex = -1;
        _contextIndex = -1;
        _anchorIndex = session.CurrentIndex;
        Pages = [.. session.Entries.Select((entry, index) => new PageItem(index, entry.FullName, _thumbnailSource))];
        ArchiveName = session.ArchiveName;
        IsListView = false;
        IsReadOnlyArchive = !session.CanModify;
        IsOpen = true;
        UpdatePosition();

        // 前回の表示方式で始める（要件定義 24.4）。一覧表示では、画像本体を読み込まない
        if (settings?.Current.ViewerViewMode == ViewerViewMode.List)
        {
            EnterListView();
        }
        else
        {
            await ShowCurrentAsync();
        }

        return true;
    }

    /// <summary>閲覧を終える。最後に閲覧した位置を保存し、アーカイブを閉じて、表示を片付ける。</summary>
    public async Task CloseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        // 以降の表示更新を止める
        _session = null;
        _showVersion++;
        _saveCts?.Cancel();

        var loader = _loader;
        var thumbnailSource = _thumbnailSource;
        _loader = null;
        _thumbnailSource = null;
        try
        {
            await SaveNowAsync(session);
        }
        finally
        {
            thumbnailSource?.Dispose();
            loader?.Dispose();
            session.Dispose();
        }

        IsOpen = false;
        IsReadOnlyArchive = false;
        IsListView = false;
        Pages = [];
        PageRows = [];
        _selected.Clear();
        SelectedCount = 0;
        SelectionText = string.Empty;
        PendingDeleteCount = 0;
        PendingDeleteText = string.Empty;
        IsCurrentPendingDelete = false;
        CurrentImage = null;
        ErrorText = string.Empty;
        IsLoading = false;
        ArchiveName = string.Empty;
        PositionText = string.Empty;
        EntryName = string.Empty;
        IsFullScreen = false;
        DisplayWidth = 0;
        DisplayHeight = 0;
        NotifyMoveCommands();
    }

    /// <summary>
    /// アプリ終了時に、最後に閲覧した位置を保存する（同期的に待つ。待ち時間に上限がある）。
    /// 保存に失敗しても終了は妨げない。
    /// </summary>
    public void FlushForExit()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _saveCts?.Cancel();
        try
        {
            Task.Run(() => SaveNowAsync(session)).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            LogSaveFailed(logger, ex);
        }
    }

    /// <summary>画像を表示する領域の大きさと、画面の拡大率（DPI÷96）が変わったときにViewから呼ばれる（1枚表示）。</summary>
    public void UpdateViewport(double width, double height, double dpiScale)
    {
        _viewportWidth = Math.Max(0, width);
        _viewportHeight = Math.Max(0, height);
        _dpiScale = dpiScale > 0 ? dpiScale : 1.0;
        RecalculateDisplay();
    }

    /// <summary>一覧表示の領域の幅が変わったときにViewから呼ばれる。1行に並べる枚数を決め直す。</summary>
    public void UpdateListViewport(double width)
    {
        ColumnCount = Math.Max(1, (int)(width / PageCellWidth));
    }

    /// <summary>
    /// キー操作を処理する。処理した場合は true（呼び出し側は、キーを他へ渡さない）。
    /// 共通: G で表示方式の切替、F11 で全画面、Esc でライブラリへ戻る。
    /// 1枚表示: ←/→・PageUp/PageDown・Space で前後、Home/End で先頭/末尾、+/− で拡大・縮小、0 でフィット、1 で原寸。
    /// ↑/↓（一覧表示では Ctrl+↑/↓）で、ライブラリの一覧の並びでの前後のアーカイブへ移る（<see cref="NavigateArchiveRequested"/>）。
    /// 一覧表示: 矢印キーで移動（Shift で範囲選択）、Space で選択の切替、Ctrl+A で全選択、Enter で1枚表示。
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (!IsOpen)
        {
            return false;
        }

        switch (key)
        {
            case Key.G when !modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt):
                ToggleViewMode();
                return true;
            case Key.F11:
                ToggleFullScreen();
                return true;
            // キーボードの「戻る」キー（マウスの戻るボタンが、ドライバーによってはこのキーとして届く。Issue #28）
            case Key.Escape or Key.BrowserBack:
                RequestClose();
                return true;
            case Key.Delete:
                ToggleDelete();
                return true;
            // 前後のアーカイブへ。一覧表示では、↑ / ↓ が画像の選択に使われているため、Ctrl を押したときだけにする
            case Key.Up or Key.Down when !modifiers.HasFlag(ModifierKeys.Alt) && (!IsListView || modifiers.HasFlag(ModifierKeys.Control)):
                NavigateArchiveRequested?.Invoke(this, key == Key.Down ? 1 : -1);
                return true;
            default:
                return IsListView ? HandleListKey(key, modifiers) : HandleSingleKey(key, modifiers);
        }
    }

    /// <summary>
    /// 一覧表示で画像がクリックされたときにViewから呼ばれる。
    /// 通常のクリックはその1枚だけを選択、Ctrl+クリックは選択の切替、Shift+クリックは範囲選択。
    /// ダブルクリックは、その画像から1枚表示にする。いずれも現在位置がその画像に移る。
    /// </summary>
    public void OnPageClicked(int index, ModifierKeys modifiers, int clickCount)
    {
        if (!IsOpen || !IsListView || _session is null || index < 0 || index >= _session.Count)
        {
            return;
        }

        MoveCursor(index);
        if (clickCount >= 2)
        {
            EnterSingleView();
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectRange(_anchorIndex, index);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleSelection(index);
        }
        else
        {
            SelectOnly(index);
        }
    }

    /// <summary>
    /// 一覧表示で画像が右クリックされたときにViewから呼ばれる（右クリックメニューを開く前）。
    /// すでに選択されている画像なら選択を保ち（複数選択のままメニューを使える）、そうでなければその1枚だけを選択する。
    /// </summary>
    public void OnPageRightClicked(int index)
    {
        // 右クリックした画像を覚えておく（「この画像を表紙にする」の対象。複数選択のままでも、右クリックした1枚）
        _contextIndex = index;
        if (!IsOpen || !IsListView || !IsValid(index) || _selected.Contains(index))
        {
            return;
        }

        MoveCursor(index);
        SelectOnly(index);
    }

    /// <summary>選択を、指定した1枚だけにする（範囲選択の起点もその1枚になる）。</summary>
    public void SelectOnly(int index)
    {
        if (!IsValid(index))
        {
            return;
        }

        SetSelection([index]);
        _anchorIndex = index;
    }

    /// <summary>指定した1枚の選択を切り替える（範囲選択の起点もその1枚になる）。</summary>
    public void ToggleSelection(int index)
    {
        if (!IsValid(index))
        {
            return;
        }

        var next = new HashSet<int>(_selected);
        if (!next.Remove(index))
        {
            next.Add(index);
        }

        SetSelection(next);
        _anchorIndex = index;
    }

    /// <summary>起点から指定した位置までを選択する（それまでの選択は置き換える。起点は変えない）。</summary>
    public void SelectRange(int anchor, int index)
    {
        if (!IsValid(anchor) || !IsValid(index))
        {
            return;
        }

        var from = Math.Min(anchor, index);
        var to = Math.Max(anchor, index);
        SetSelection(Enumerable.Range(from, to - from + 1));
    }

    public void SelectAll()
    {
        if (_session is { } session)
        {
            SetSelection(Enumerable.Range(0, session.Count));
        }
    }

    public void ClearSelection() => SetSelection([]);

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void Next() => MoveAndShow(session => session.MoveNext());

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    private void Previous() => MoveAndShow(session => session.MovePrevious());

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    private void First() => MoveAndShow(session => session.MoveFirst());

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void Last() => MoveAndShow(session => session.MoveLast());

    [RelayCommand]
    private void ZoomIn()
    {
        var next = ZoomSteps.FirstOrDefault(step => step > EffectiveScale * 1.0001);
        SetCustomScale(next > 0 ? next : ZoomSteps[^1]);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var previous = ZoomSteps.LastOrDefault(step => step < EffectiveScale * 0.9999);
        SetCustomScale(previous > 0 ? previous : ZoomSteps[0]);
    }

    [RelayCommand]
    private void Fit()
    {
        _zoomMode = ZoomMode.Fit;
        RecalculateDisplay();
    }

    [RelayCommand]
    private void ActualSize()
    {
        _zoomMode = ZoomMode.Actual;
        RecalculateDisplay();
    }

    /// <summary>
    /// 削除予約の追加と解除を切り替える（Delete）。対象は、1枚表示では現在の画像、一覧表示では選択中の画像
    /// （選択がなければ現在の画像）。対象がすべて予約済みなら解除し、そうでなければ予約する。ZIP本体は変更しない。
    /// </summary>
    [RelayCommand]
    private void ToggleDelete()
    {
        if (_session is not { } session)
        {
            return;
        }

        var targets = DeleteTargets(session);
        ApplyPendingDelete(session, targets, !targets.All(session.IsPendingDelete));
    }

    /// <summary>対象の画像を削除予約する（右クリックメニュー「削除予約」）。</summary>
    [RelayCommand]
    private void ReserveDelete()
    {
        if (_session is { } session)
        {
            ApplyPendingDelete(session, DeleteTargets(session), true);
        }
    }

    /// <summary>対象の画像の削除予約を解除する（右クリックメニュー「削除予約を解除」）。</summary>
    [RelayCommand]
    private void ReleaseDelete()
    {
        if (_session is { } session)
        {
            ApplyPendingDelete(session, DeleteTargets(session), false);
        }
    }

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void ToggleViewMode()
    {
        if (!IsOpen)
        {
            return;
        }

        if (IsListView)
        {
            EnterSingleView();
        }
        else
        {
            EnterListView();
        }

        // 切り替え（G、ツールバーのボタン）を記憶し、次に開いたときもこの表示方式で始める（要件定義 24.4）。
        // 一覧でダブルクリックして1枚表示にするのは「その画像を見る」操作のため、記憶しない
        var mode = IsListView ? ViewerViewMode.List : ViewerViewMode.SinglePage;
        settings?.Update(s => s with { ViewerViewMode = mode });
    }

    [RelayCommand]
    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// マウスの戻るボタン（XButton1）が押された（Issue #28）。開いていれば、Esc と同じくライブラリへ戻るよう求める
    /// （削除予約の確認は、呼び出し側が行う）。処理した場合は true。
    /// </summary>
    public bool HandleBackButton()
    {
        if (!IsOpen)
        {
            return false;
        }

        RequestClose();
        return true;
    }

    /// <summary>対象の画像（1枚表示では今の頁、一覧表示では右クリックした画像）を、そのまま表紙にするよう求める。</summary>
    [RelayCommand]
    private void SetCover() => RequestCover(crop: false);

    /// <summary>対象の画像を切り抜いて、表紙にするよう求める（切り抜きの画面を出す）。</summary>
    [RelayCommand]
    private void CropCover() => RequestCover(crop: true);

    private void RequestCover(bool crop)
    {
        if (_session is not { } session)
        {
            return;
        }

        var index = IsListView && IsValid(_contextIndex) ? _contextIndex : session.CurrentIndex;
        if (!IsValid(index))
        {
            return;
        }

        CoverRequested?.Invoke(
            this, new CoverRequest(session.ArchiveId, index, session.Entries[index].FullName, crop));
    }

    /// <summary>開いている RAR / 7z を ZIP に変換するよう求める（ZIP を開いているときは、何もしない）。</summary>
    [RelayCommand]
    private void RequestConvertToZip()
    {
        if (_session is { CanModify: false })
        {
            ConvertToZipRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        // 開いたまま終了する場合に備えて、位置を保存してから後片付けをする（保存の中で予約をキャンセルする）
        FlushForExit();
        _saveCts?.Dispose();
        _saveCts = null;
        _thumbnailSource?.Dispose();
        _loader?.Dispose();
        _session?.Dispose();
        _thumbnailSource = null;
        _loader = null;
        _session = null;
    }

    partial void OnColumnCountChanged(int value) => RebuildRows();

    partial void OnPagesChanged(IReadOnlyList<PageItem> value) => RebuildRows();

    partial void OnIsListViewChanged(bool value) => RebuildRows();

    private bool CanMoveNext() => _session?.CanMoveNext ?? false;

    private bool CanMovePrevious() => _session?.CanMovePrevious ?? false;

    private bool IsValid(int index) => _session is { } session && index >= 0 && index < session.Count;

    private bool HandleSingleKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Right or Key.PageDown:
                Next();
                return true;
            case Key.Left or Key.PageUp:
                Previous();
                return true;
            case Key.Space:
                if (modifiers.HasFlag(ModifierKeys.Shift))
                {
                    Previous();
                }
                else
                {
                    Next();
                }

                return true;
            case Key.Home:
                First();
                return true;
            case Key.End:
                Last();
                return true;
            case Key.Add or Key.OemPlus:
                ZoomIn();
                return true;
            case Key.Subtract or Key.OemMinus:
                ZoomOut();
                return true;
            case Key.D0 or Key.NumPad0:
                Fit();
                return true;
            case Key.D1 or Key.NumPad1:
                ActualSize();
                return true;
            default:
                return false;
        }
    }

    private bool HandleListKey(Key key, ModifierKeys modifiers)
    {
        var extend = modifiers.HasFlag(ModifierKeys.Shift);
        var columns = Math.Max(1, ColumnCount);
        switch (key)
        {
            case Key.Right:
                MoveCursorBy(1, extend);
                return true;
            case Key.Left:
                MoveCursorBy(-1, extend);
                return true;
            case Key.Down:
                MoveCursorBy(columns, extend);
                return true;
            case Key.Up:
                MoveCursorBy(-columns, extend);
                return true;
            case Key.PageDown:
                MoveCursorBy(columns * PageStepRows, extend);
                return true;
            case Key.PageUp:
                MoveCursorBy(-columns * PageStepRows, extend);
                return true;
            case Key.Home:
                MoveCursorTo(0, extend);
                return true;
            case Key.End:
                MoveCursorTo((_session?.Count ?? 1) - 1, extend);
                return true;
            case Key.Space:
                ToggleSelection(_session?.CurrentIndex ?? 0);
                return true;
            case Key.Enter:
                EnterSingleView();
                return true;
            case Key.A when modifiers.HasFlag(ModifierKeys.Control):
                SelectAll();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 一覧表示に切り替える。閲覧していた画像を選択状態にする。すでにその画像を含む複数選択があれば、
    /// その選択を保つ（一覧と1枚表示を行き来しても、選択が失われない）。
    /// </summary>
    private void EnterListView()
    {
        if (_session is not { } session)
        {
            return;
        }

        _showVersion++; // 読み込み中の1枚表示の結果は、画面に反映しない
        IsLoading = false;
        IsListView = true;
        if (!_selected.Contains(session.CurrentIndex))
        {
            SelectOnly(session.CurrentIndex);
        }

        _anchorIndex = session.CurrentIndex;
        RequestScrollToCurrent();
    }

    /// <summary>1枚表示に切り替える。一覧表示で選んだ現在位置の画像を表示する。選択は保つ。</summary>
    private void EnterSingleView()
    {
        if (_session is null)
        {
            return;
        }

        IsListView = false;
        _ = ShowCurrentAsync();
    }

    private void MoveCursorBy(int delta, bool extend)
    {
        if (_session is { } session)
        {
            MoveCursorTo(Math.Clamp(session.CurrentIndex + delta, 0, session.Count - 1), extend);
        }
    }

    private void MoveCursorTo(int index, bool extend)
    {
        if (!IsValid(index))
        {
            return;
        }

        MoveCursor(index);
        if (extend)
        {
            SelectRange(_anchorIndex, index);
        }
        else
        {
            SelectOnly(index);
        }
    }

    /// <summary>現在位置を動かす。一覧表示では画像は読み込まず、位置の表示と保存の予約だけを行う。</summary>
    private void MoveCursor(int index)
    {
        if (_session is not { } session)
        {
            return;
        }

        if (session.MoveTo(index))
        {
            UpdatePosition();
            ScheduleSave();
        }

        RequestScrollToCurrent();
    }

    private void RequestScrollToCurrent()
    {
        if (_session is { } session && IsListView)
        {
            ScrollToRowRequested?.Invoke(this, session.CurrentIndex / Math.Max(1, ColumnCount));
        }
    }

    private void SetSelection(IEnumerable<int> indices)
    {
        var next = new HashSet<int>(indices);
        if (next.SetEquals(_selected))
        {
            return;
        }

        foreach (var index in _selected.Except(next).ToList())
        {
            Pages[index].IsSelected = false;
        }

        foreach (var index in next.Except(_selected).ToList())
        {
            Pages[index].IsSelected = true;
        }

        _selected.Clear();
        _selected.UnionWith(next);
        SelectedCount = _selected.Count;
        SelectionText = _selected.Count > 0
            ? string.Create(CultureInfo.CurrentCulture, $"選択 {_selected.Count:N0}枚")
            : string.Empty;
    }

    /// <summary>
    /// 一覧表示のときだけ、画像を1行ずつにまとめる。1枚表示に戻したときは破棄する
    /// （使っていない間は、行のオブジェクトを保持しない）。
    /// </summary>
    private void RebuildRows()
    {
        if (!IsListView || Pages.Count == 0)
        {
            PageRows = [];
            return;
        }

        var columns = Math.Max(1, ColumnCount);
        var source = Pages;
        var rows = new List<PageRow>((source.Count + columns - 1) / columns);
        for (var start = 0; start < source.Count; start += columns)
        {
            var count = Math.Min(columns, source.Count - start);
            rows.Add(new PageRow(new ArraySegmentList<PageItem>(source, start, count)));
        }

        PageRows = rows;
        RequestScrollToCurrent();
    }

    private IReadOnlyList<int> DeleteTargets(ViewerSession session) =>
        IsListView && _selected.Count > 0 ? SelectedIndices : [session.CurrentIndex];

    /// <summary>
    /// 削除予約を追加・解除し、表示に反映する。1枚表示で現在の画像を予約した場合は、
    /// 次の（なければ前の）予約されていない画像を表示する。移動先がなければ、予約した画像をそのまま
    /// 「削除予定」の表示で残す。
    /// </summary>
    private void ApplyPendingDelete(ViewerSession session, IReadOnlyList<int> targets, bool pending)
    {
        // RAR / 7z は、閲覧だけ。予約しても、確定（ZIP の作り直し）できないため、予約させずに、理由を知らせる
        if (pending && !session.CanModify)
        {
            DeleteBlocked?.Invoke(
                this,
                "このアーカイブ（RAR / 7z）は、閲覧だけで、画像を削除できません。削除するには、ツールバーの「ZIP に変換」で、ZIP に変換してください");
            return;
        }

        session.SetPendingDelete(targets, pending);
        foreach (var index in targets)
        {
            if (index >= 0 && index < Pages.Count)
            {
                Pages[index].IsPendingDelete = session.IsPendingDelete(index);
            }
        }

        PendingDeleteCount = session.PendingDeleteCount;
        PendingDeleteText = session.PendingDeleteCount > 0
            ? string.Create(CultureInfo.CurrentCulture, $"削除予定 {session.PendingDeleteCount:N0}枚")
            : string.Empty;

        if (!IsListView && pending && session.IsPendingDelete(session.CurrentIndex)
            && (session.MoveNext() || session.MovePrevious()))
        {
            _ = ShowCurrentAsync();
            return;
        }

        UpdatePosition();
    }

    private void MoveAndShow(Func<ViewerSession, bool> move)
    {
        if (_session is { } session && move(session))
        {
            _ = ShowCurrentAsync();
        }
    }

    private void SetCustomScale(double scale)
    {
        _zoomMode = ZoomMode.Custom;
        _customScale = scale;
        RecalculateDisplay();
    }

    private void UpdatePosition()
    {
        if (_session is not { } session)
        {
            return;
        }

        PositionText = string.Create(CultureInfo.CurrentCulture, $"{session.CurrentIndex + 1:N0} / {session.Count:N0}");
        EntryName = session.CurrentEntry.FullName;
        IsCurrentPendingDelete = session.IsPendingDelete(session.CurrentIndex);

        // 一覧表示で「現在位置」として強調する画像を切り替える
        if (_markedCurrentIndex != session.CurrentIndex)
        {
            if (_markedCurrentIndex >= 0 && _markedCurrentIndex < Pages.Count)
            {
                Pages[_markedCurrentIndex].IsCurrent = false;
            }

            if (session.CurrentIndex < Pages.Count)
            {
                Pages[session.CurrentIndex].IsCurrent = true;
            }

            _markedCurrentIndex = session.CurrentIndex;
        }

        NotifyMoveCommands();
    }

    private void NotifyMoveCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        FirstCommand.NotifyCanExecuteChanged();
        LastCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 現在の位置の画像を表示する。操作が連続した場合は、最後に要求したものだけを画面に反映する。
    /// 表示のあとで、前後の頁を先読みし、閲覧位置の保存を予約する。
    /// </summary>
    private async Task ShowCurrentAsync()
    {
        var session = _session;
        var loader = _loader;
        if (session is null || loader is null)
        {
            return;
        }

        var version = ++_showVersion;
        var index = session.CurrentIndex;
        UpdatePosition();

        try
        {
            var task = loader.GetAsync(index);
            loader.Prefetch(index);
            if (!task.IsCompleted)
            {
                IsLoading = true;
            }

            var page = await task;
            if (version != _showVersion || !ReferenceEquals(session, _session))
            {
                return; // 追い越された、または閉じられた
            }

            IsLoading = false;
            ErrorText = page.Error is null
                ? string.Empty
                : BuildImageErrorText(session.Entries[index].FullName);
            CurrentImage = page.Image;
            RecalculateDisplay();
            ScheduleSave();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 閉じられた
        }
        catch (Exception ex)
        {
            if (version == _showVersion)
            {
                IsLoading = false;
            }

            LogShowFailed(logger, ex);
        }
    }

    private void RecalculateDisplay()
    {
        var modeText = _zoomMode switch
        {
            ZoomMode.Fit => "フィット",
            _ => string.Empty,
        };

        if (CurrentImage is not { } image || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            EffectiveScale = _zoomMode == ZoomMode.Custom ? _customScale : 1.0;
            DisplayWidth = 0;
            DisplayHeight = 0;
            ZoomText = modeText.Length > 0 ? modeText : FormatPercent(EffectiveScale);
            return;
        }

        var scale = ScaleFor(image.PixelWidth, image.PixelHeight);
        EffectiveScale = scale;
        DisplayWidth = image.PixelWidth * scale / _dpiScale;
        DisplayHeight = image.PixelHeight * scale / _dpiScale;
        ZoomText = modeText.Length > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{modeText}（{FormatPercent(scale)}）")
            : FormatPercent(scale);
    }

    private double ScaleFor(int pixelWidth, int pixelHeight)
    {
        switch (_zoomMode)
        {
            case ZoomMode.Actual:
                return 1.0;
            case ZoomMode.Custom:
                return _customScale;
            default:
                if (_viewportWidth <= 0 || _viewportHeight <= 0)
                {
                    return 1.0;
                }

                // ウィンドウ（画面の画素数）に収まる倍率。小さい画像は拡大しない。
                var fit = Math.Min(
                    _viewportWidth * _dpiScale / pixelWidth,
                    _viewportHeight * _dpiScale / pixelHeight);
                return Math.Min(1.0, fit);
        }
    }

    private static string FormatPercent(double scale) => scale.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>画像を読めなかったときの表示。WebP は、Windows の拡張機能がない環境では読めないため、理由を添える。</summary>
    private static string BuildImageErrorText(string entryName)
    {
        var text = string.Create(CultureInfo.CurrentCulture, $"画像を読み込めません: {entryName}");
        return entryName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            ? text + "\n(この環境では WebP を読めない可能性があります)"
            : text;
    }

    private void ScheduleSave()
    {
        var session = _session;
        if (session is null || _positionSaveSuppressed || session.CurrentIndex == _savedIndex)
        {
            return;
        }

        var previous = _saveCts;
        var cts = new CancellationTokenSource();
        _saveCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        _ = SaveAfterDelayAsync(session, cts.Token);
    }

    private async Task SaveAfterDelayAsync(ViewerSession session, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDelay, ct);
            await SaveNowAsync(session);
        }
        catch (OperationCanceledException)
        {
            // 次の移動で予約し直された、または閉じられた
        }
    }

    private async Task SaveNowAsync(ViewerSession session)
    {
        var index = session.CurrentIndex;
        if (_positionSaveSuppressed || index == _savedIndex)
        {
            return;
        }

        try
        {
            await viewerService.SaveLastViewAsync(session, CancellationToken.None);
            _savedIndex = index;
        }
        catch (Exception ex)
        {
            // 閲覧位置を保存できなくても、閲覧は続けられる
            LogSaveFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "頁の表示に失敗しました。")]
    private static partial void LogShowFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "最後に閲覧した位置を保存できませんでした。")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
