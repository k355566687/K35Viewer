using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>ビューアのアーカイブを離れる前の確認（DLG-01）の結果。</summary>
public enum LeaveDecision
{
    /// <summary>そのまま離れてよい（削除予約がない、または「変更を破棄」が選ばれた）。</summary>
    Proceed,

    /// <summary>離れるのをやめて、ビューアに留まる。</summary>
    Stay,

    /// <summary>「更新する」が選ばれた。<see cref="MainViewModel.UpdateArchiveAsync"/> で更新してから離れる。</summary>
    Update,
}

/// <summary>
/// メインウィンドウのViewModel。ライブラリ画面とビューア画面を、同じウィンドウ内で切り替える。
/// どちらの画面も表示を切り替えるだけで作り直さないため、ライブラリへ戻ったときに
/// スクロール位置や検索条件がそのまま残る。
/// ビューアを離れるときの変更確認と、削除予約を反映するZIP更新の進行も担当する。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const string ProductName = "35Viewer";

    private readonly NotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ArchiveService _archives;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SettingsService? _settings;
    private bool _confirmNextLeave;
    private CancellationTokenSource? _updateCts;
    private ViewerSession? _externalChangeWarnedFor;
    private bool _isTransitioning;

    public MainViewModel(
        LibraryViewModel library,
        ViewerViewModel viewer,
        ToastHostViewModel toasts,
        NotificationService notifications,
        IDialogService dialogs,
        ArchiveService archives,
        ILogger<MainViewModel> logger,
        SettingsService? settings = null)
    {
        _settings = settings;
        Library = library;
        Viewer = viewer;
        Toasts = toasts;
        _notifications = notifications;
        _dialogs = dialogs;
        _archives = archives;
        _logger = logger;

        Library.OpenRequested += OnOpenRequested;
        Viewer.CloseRequested += OnCloseRequested;
        Viewer.NavigateArchiveRequested += OnNavigateArchiveRequested;
        Viewer.PropertyChanged += OnViewerPropertyChanged;
    }

    public LibraryViewModel Library { get; }

    public ViewerViewModel Viewer { get; }

    public ToastHostViewModel Toasts { get; }

    /// <summary>
    /// 画面の更新（進行状況の表示など）をUIスレッドへ届けるためのコンテキスト。作成したスレッドのものを使う。
    /// テストで、画面のスレッドと別のスレッドから作った場合に差し替える。
    /// </summary>
    internal SynchronizationContext? UiContext { get; set; } = SynchronizationContext.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isViewerVisible;

    /// <summary>アーカイブを更新（ZIPを再構築）している間は true。この間は、画面の操作を受け付けない。</summary>
    [ObservableProperty]
    private bool _isUpdating;

    /// <summary>更新の進行状況の説明。</summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    /// <summary>更新をキャンセルできるか。元のZIPのごみ箱への退避を始めたあと（安全境界を越えたあと）は false。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    private bool _canCancelUpdate;

    public bool IsLibraryVisible => !IsViewerVisible;

    /// <summary>ウィンドウのタイトル。ビューアを開いている間は、アーカイブ名を含める。</summary>
    public string Title => IsViewerVisible && Viewer.ArchiveName.Length > 0
        ? $"{Viewer.ArchiveName} - {ProductName}"
        : ProductName;

    /// <summary>
    /// ビューアのアーカイブを離れてよいか確認する（ライブラリへ戻る場合、アプリを終了する場合）。
    /// 削除予約がなければ確認せずに <see cref="LeaveDecision.Proceed"/>。あれば変更確認ダイアログ（DLG-01）を表示する。
    /// 「変更を破棄」なら Proceed（離れると削除予約は破棄され、ZIP本体は変更されない）、キャンセルなら Stay、
    /// 「更新する」なら Update。アーカイブが外部で変更されている、ごみ箱を使えない保存先、などの場合は「更新する」を選べない。
    /// 設定で「離れるときの確認」をオフにしている場合は、更新できるときに限り、ダイアログを出さずに Update を返す。
    /// </summary>
    public LeaveDecision DecideLeave()
    {
        if (!IsViewerVisible || Viewer.PendingDeleteCount == 0 || Viewer.Session?.CreateUpdateRequest() is not { } request)
        {
            return LeaveDecision.Proceed;
        }

        var reason = _archives.GetUpdateBlockReason(request);
        if (reason is null && _settings?.Current.ConfirmBeforeLeave == false && !_confirmNextLeave)
        {
            // 確認の設定がオフなら、確認せずに更新して離れる（要件定義 24.3）。
            // 更新できない場合（reason がある）は、オフでも確認ダイアログを表示する
            return LeaveDecision.Update;
        }

        // 直前の更新が失敗・キャンセルされた場合は、オフでも1回は確認する（失敗する更新を、確認なしで繰り返さない）
        _confirmNextLeave = false;
        var choice = _dialogs.ConfirmLeaveArchive(Viewer.ArchiveName, request.EntriesToRemove.Count, reason);
        return choice switch
        {
            LeaveChoice.Discard => LeaveDecision.Proceed,
            LeaveChoice.Update when reason is null => LeaveDecision.Update,
            _ => LeaveDecision.Stay,
        };
    }

    /// <summary>
    /// 削除予約を反映して、ビューアで開いているアーカイブを更新する（UC-11）。成功した場合は true
    /// （呼び出し側はこのあとビューアを閉じる）。失敗・キャンセルの場合は false で、元のアーカイブと削除予約が残る。
    /// 置き換えの途中で失敗して元のアーカイブに戻せない場合も true（ビューアの内容が古いため、閉じる必要がある）。
    /// 結果は通知される。更新中は <see cref="IsUpdating"/> が true になり、キャンセルできる間は <see cref="CancelUpdateCommand"/> が使える。
    /// </summary>
    public async Task<bool> UpdateArchiveAsync()
    {
        if (IsUpdating || Viewer.Session?.CreateUpdateRequest() is not { } request)
        {
            return false;
        }

        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        IsUpdating = true;
        CanCancelUpdate = true;
        UpdateStatusText = DescribePhase(ArchiveUpdatePhase.Preparing);
        Viewer.SuspendPositionSaving();
        var mustLeave = false;
        try
        {
            var progress = new ContextProgress<ArchiveUpdatePhase>(UiContext, OnUpdatePhase);
            var result = await _archives.ApplyDeletionsAsync(request, progress, cts.Token);
            mustLeave = result.Succeeded || !result.OriginalPreserved;
            if (mustLeave)
            {
                Viewer.MarkArchiveUpdated();
            }

            return mustLeave;
        }
        catch (Exception ex)
        {
            LogUpdateFailed(_logger, ex);
            _notifications.ShowError("アーカイブを更新できませんでした");
            return false;
        }
        finally
        {
            _confirmNextLeave = !mustLeave;
            if (!mustLeave)
            {
                Viewer.ResumePositionSaving();
            }

            IsUpdating = false;
            CanCancelUpdate = false;
            UpdateStatusText = string.Empty;
            _updateCts = null;
        }
    }

    /// <summary>
    /// ウィンドウが前面になったときに呼ぶ。ビューアで開いているアーカイブが、外部で変更されていれば、
    /// 閲覧内容が古い可能性を通知する（同じアーカイブにつき1回だけ）。
    /// </summary>
    public void OnWindowActivated()
    {
        if (!IsViewerVisible || IsUpdating || Viewer.Session is not { } session
            || ReferenceEquals(_externalChangeWarnedFor, session))
        {
            return;
        }

        if (_archives.IsChangedExternally(session.ArchivePath, session.OpenedFileState))
        {
            _externalChangeWarnedFor = session;
            _notifications.ShowWarning(
                $"「{session.ArchiveName}」が外部で変更されました。表示内容が古い可能性があります。ライブラリへ戻って開き直してください");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelUpdate))]
    private void CancelUpdate() => _updateCts?.Cancel();

    private void OnUpdatePhase(ArchiveUpdatePhase phase)
    {
        UpdateStatusText = DescribePhase(phase);
        CanCancelUpdate = phase is ArchiveUpdatePhase.Preparing or ArchiveUpdatePhase.Rebuilding or ArchiveUpdatePhase.Verifying;
    }

    private static string DescribePhase(ArchiveUpdatePhase phase) => phase switch
    {
        ArchiveUpdatePhase.Preparing => "更新できるか確認しています…",
        ArchiveUpdatePhase.Rebuilding => "削除する画像を除いたアーカイブを作成しています…",
        ArchiveUpdatePhase.Verifying => "作成したアーカイブを確認しています…",
        ArchiveUpdatePhase.Replacing => "元のアーカイブをごみ箱へ移動して、置き換えています…（この処理は中止できません）",
        _ => "記録を更新しています…（この処理は中止できません）",
    };

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
        if (_isTransitioning || !IsViewerVisible || IsUpdating)
        {
            return;
        }

        // 削除予約が残っていれば、離れてよいか確認する（キャンセルならビューアに留まる）
        var decision = DecideLeave();
        if (decision == LeaveDecision.Stay)
        {
            return;
        }

        _isTransitioning = true;
        try
        {
            var updated = false;
            if (decision == LeaveDecision.Update)
            {
                // 失敗・キャンセルなら、ビューアに留まる（元のアーカイブと削除予約を保持している）
                if (!await UpdateArchiveAsync())
                {
                    return;
                }

                updated = true;
            }

            // 画面は先に切り替え、最後に閲覧した位置の保存などの後片付けはあとから行う
            IsViewerVisible = false;
            await Viewer.CloseAsync();

            if (updated)
            {
                // 内容が変わったアーカイブの表示と、サムネイルを更新する
                await Library.OnArchiveChangedAsync();
            }
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
    /// ビューアで開いたまま、ライブラリの一覧の並びでの前後のアーカイブへ移る（↑ / ↓）。
    /// 削除予約が残っていれば、ライブラリへ戻るときと同じ確認（DLG-01）を行う。キャンセルなら、今のアーカイブに留まる。
    /// 移り先は、確認や更新で一覧が並べ替わっても、最初に決めたものを開く。
    /// 次のアーカイブを開けなかった場合（理由は通知済み）は、ライブラリへ戻る（今のアーカイブは、すでに閉じているため）。
    /// </summary>
    private async void OnNavigateArchiveRequested(object? sender, int direction)
    {
        if (_isTransitioning || !IsViewerVisible || IsUpdating || Viewer.Session is not { } session)
        {
            return;
        }

        var target = Library.FindNeighbor(session.ArchiveId, direction, out var reason);
        if (target is null)
        {
            _notifications.ShowInfo(reason);
            return;
        }

        var decision = DecideLeave();
        if (decision == LeaveDecision.Stay)
        {
            return;
        }

        _isTransitioning = true;
        try
        {
            var updated = false;
            if (decision == LeaveDecision.Update)
            {
                // 失敗・キャンセルなら、今のアーカイブに留まる（元のアーカイブと削除予約を保持している）
                if (!await UpdateArchiveAsync())
                {
                    return;
                }

                updated = true;
            }

            await Viewer.CloseAsync();
            if (updated)
            {
                // 内容が変わったアーカイブの表示と、サムネイルを更新する
                await Library.OnArchiveChangedAsync();
            }

            // ライブラリへ戻ったときに、今見ていたアーカイブの位置にいるよう、選んでおく
            Library.SelectedArchive = Library.Archives.FirstOrDefault(a => a.Id == target.Id) ?? Library.SelectedArchive;
            if (!await Viewer.OpenAsync(target.Id))
            {
                IsViewerVisible = false;
            }
        }
        catch (Exception ex)
        {
            LogNavigateFailed(_logger, ex);
            _notifications.ShowError("次のアーカイブを開けませんでした");
            IsViewerVisible = false;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "前後のアーカイブへ移る処理に失敗しました。")]
    private static partial void LogNavigateFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "ビューアを閉じる処理に失敗しました。")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "アーカイブの更新で想定外のエラーが起きました。")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception);
}
