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
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;
    private bool _isTransitioning;

    public MainViewModel(
        LibraryViewModel library,
        ViewerViewModel viewer,
        ToastHostViewModel toasts,
        NotificationService notifications,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        Library = library;
        Viewer = viewer;
        Toasts = toasts;
        _notifications = notifications;
        _dialogs = dialogs;
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

        // 削除予約が残っていれば、離れてよいか確認する（キャンセルならビューアに留まる）
        if (!ConfirmLeaveViewer())
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

    /// <summary>
    /// ビューアのアーカイブを離れてよいか確認する（ライブラリへ戻る場合、アプリを終了する場合）。
    /// 削除予約がなければ確認せずに true。削除予約があれば、変更確認ダイアログ（DLG-01）を表示する。
    /// 「変更を破棄」なら true（離れると削除予約は破棄され、ZIP本体は変更されない）、キャンセルなら false。
    /// ZIPの再構築（「更新する」）はPhase 8で実装するまで選べない。
    /// </summary>
    public bool ConfirmLeaveViewer()
    {
        if (!IsViewerVisible || Viewer.PendingDeleteCount == 0)
        {
            return true;
        }

        var choice = _dialogs.ConfirmLeaveArchive(Viewer.ArchiveName, Viewer.PendingDeleteCount, canUpdate: false);
        return choice == LeaveChoice.Discard;
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
