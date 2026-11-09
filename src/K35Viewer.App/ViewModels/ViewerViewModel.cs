using System.Globalization;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Infrastructure.Viewer;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

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
/// ビューア画面（SCR-02）のViewModel。1枚表示、前後移動、拡大・縮小、フィット・原寸、全画面、
/// 最終閲覧位置の記録を扱う。画像の表示はデコード済みの画像を受け取るだけで、ZIPを直接操作しない。
/// </summary>
public sealed partial class ViewerViewModel(
    ViewerService viewerService,
    ViewerImageDecoder decoder,
    ILogger<ViewerViewModel> logger) : ObservableObject, IDisposable
{
    /// <summary>キー操作の案内（画面に表示する）。</summary>
    public const string KeyHelp =
        "←/→ 前後の頁　+/− 拡大・縮小　0 フィット　1 原寸　F11 全画面　Esc ライブラリへ戻る";

    /// <summary>拡大・縮小の段階。</summary>
    private static readonly double[] ZoomSteps =
        [0.1, 0.15, 0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0];

    /// <summary>頁を移動してから、最後に閲覧した位置を保存するまでの待ち時間（めくるたびに書き込まないため）。</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private ViewerSession? _session;
    private PageImageLoader? _loader;
    private CancellationTokenSource? _saveCts;
    private int _showVersion;
    private int _savedIndex = -1;
    private ZoomMode _zoomMode = ZoomMode.Fit;
    private double _customScale = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _dpiScale = 1.0;

    /// <summary>ライブラリへ戻る操作が行われた（Esc、戻るボタン）。画面の切り替えは呼び出し側が行う。</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _archiveName = string.Empty;

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

    /// <summary>上部のツールバーを表示するか。全画面では画像を最大に使うため隠す。</summary>
    public bool ShowToolbar => !IsFullScreen;

    public string HelpText => KeyHelp;

    public ZoomMode ZoomMode => _zoomMode;

    /// <summary>現在の拡大率（画像の1画素を画面の何画素で表示するか）。画像がなければ 1。</summary>
    public double EffectiveScale { get; private set; } = 1.0;

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
        _loader = new PageImageLoader(session, decoder);
        _savedIndex = -1;
        ArchiveName = session.ArchiveName;
        IsOpen = true;
        UpdatePosition();
        await ShowCurrentAsync();
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
        _loader = null;
        try
        {
            await SaveNowAsync(session);
        }
        finally
        {
            loader?.Dispose();
            session.Dispose();
        }

        IsOpen = false;
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

    /// <summary>画像を表示する領域の大きさと、画面の拡大率（DPI÷96）が変わったときにViewから呼ばれる。</summary>
    public void UpdateViewport(double width, double height, double dpiScale)
    {
        _viewportWidth = Math.Max(0, width);
        _viewportHeight = Math.Max(0, height);
        _dpiScale = dpiScale > 0 ? dpiScale : 1.0;
        RecalculateDisplay();
    }

    /// <summary>
    /// キー操作を処理する。処理した場合は true（呼び出し側は、キーを他へ渡さない）。
    /// ←/→・PageUp/PageDown・Space で前後、Home/End で先頭/末尾、+/− で拡大・縮小、0 でフィット、
    /// 1 で原寸、F11 で全画面、Esc でライブラリへ戻る。
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (!IsOpen)
        {
            return false;
        }

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
            case Key.F11:
                ToggleFullScreen();
                return true;
            case Key.Escape:
                RequestClose();
                return true;
            default:
                return false;
        }
    }

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

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        // 開いたまま終了する場合に備えて、位置を保存してから後片付けをする（保存の中で予約をキャンセルする）
        FlushForExit();
        _saveCts?.Dispose();
        _saveCts = null;
        _loader?.Dispose();
        _session?.Dispose();
        _loader = null;
        _session = null;
    }

    private bool CanMoveNext() => _session?.CanMoveNext ?? false;

    private bool CanMovePrevious() => _session?.CanMovePrevious ?? false;

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
                : string.Create(CultureInfo.CurrentCulture, $"画像を読み込めません: {session.Entries[index].FullName}");
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

    private void ScheduleSave()
    {
        var session = _session;
        if (session is null || session.CurrentIndex == _savedIndex)
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
        if (index == _savedIndex)
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
