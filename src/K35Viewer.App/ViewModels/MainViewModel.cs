using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>
/// メインウィンドウのViewModel。ライブラリ画面とビューア画面を、同じウィンドウ内で切り替える。
/// どちらの画面も表示を切り替えるだけで作り直さないため、ライブラリへ戻ったときに
/// スクロール位置や検索条件がそのまま残る。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const string ProductName = "35Viewer";

    private readonly NotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private bool _isTransitioning;

    public MainViewModel(
        LibraryViewModel library,
        ViewerViewModel viewer,
        ToastHostViewModel toasts,
        NotificationService notifications,
        ILogger<MainViewModel> logger)
    {
        Library = library;
        Viewer = viewer;
        Toasts = toasts;
        _notifications = notifications;
        _logger = logger;

        Library.OpenRequested += OnOpenRequested;
        Viewer.CloseRequested += OnCloseRequested;
        Viewer.PropertyChanged += OnViewerPropertyChanged;
    }

    public LibraryViewModel Library { get; }

    public ViewerViewModel Viewer { get; }

    public ToastHostViewModel Toasts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isViewerVisible;

    public bool IsLibraryVisible => !IsViewerVisible;

    /// <summary>ウィンドウのタイトル。ビューアを開いている間は、アーカイブ名を含める。</summary>
    public string Title => IsViewerVisible && Viewer.ArchiveName.Length > 0
        ? $"{Viewer.ArchiveName} - {ProductName}"
        : ProductName;

    // 画面操作から呼ばれるイベント。例外でアプリを落とさないよう、中で処理する
    private async void OnOpenRequested(object? sender, long archiveId)
    {
        if (_isTransitioning || IsViewerVisible)
        {
            return;
        }

        _isTransitioning = true;
        try
        {
            // 開けなかった場合は、理由がトーストで通知され、ライブラリ画面に留まる
            if (await Viewer.OpenAsync(archiveId))
            {
                IsViewerVisible = true;
            }
        }
        catch (Exception ex)
        {
            LogOpenFailed(_logger, ex, archiveId);
            _notifications.ShowError("アーカイブを開けませんでした");
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    private async void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_isTransitioning || !IsViewerVisible)
        {
            return;
        }

        _isTransitioning = true;
        try
        {
            // 画面は先に切り替え、最後に閲覧した位置の保存などの後片付けはあとから行う
            IsViewerVisible = false;
            await Viewer.CloseAsync();
        }
        catch (Exception ex)
        {
            LogCloseFailed(_logger, ex);
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    private void OnViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.ArchiveName))
        {
            OnPropertyChanged(nameof(Title));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ビューアを開けませんでした。ArchiveId={Id}")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception, long id);

    [LoggerMessage(Level = LogLevel.Error, Message = "ビューアを閉じる処理に失敗しました。")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}
