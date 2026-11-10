using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using K35Viewer.ViewModels;

namespace K35Viewer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private WindowState _stateBeforeFullScreen;
    private WindowStyle _styleBeforeFullScreen;
    private ResizeMode _resizeBeforeFullScreen;
    private bool _closeAfterUpdate;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Activated += (_, _) => _viewModel.OnWindowActivated();
        viewModel.Viewer.PropertyChanged += OnViewerPropertyChanged;
        viewModel.PropertyChanged += OnMainPropertyChanged;
    }

    // ビューアの表示中だけ、キー操作をビューアへ渡す（ライブラリ画面の検索欄などの入力は妨げない）
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // アーカイブの更新中は、画面の操作を受け付けない（Esc でキャンセルできる間はキャンセルする）
        if (_viewModel.IsUpdating)
        {
            if (e.Key == Key.Escape && _viewModel.CancelUpdateCommand.CanExecute(null))
            {
                _viewModel.CancelUpdateCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (!_viewModel.IsViewerVisible)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_viewModel.Viewer.HandleKey(key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    // アーカイブの更新中は、終わるまで終了を保留する。
    // 削除予約が残っていれば終了前に確認する（キャンセルなら終了しない。「更新する」なら、更新が終わってから終了する）。
    // ビューアを開いたまま終了する場合も、最後に閲覧した位置を保存する
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsUpdating)
        {
            e.Cancel = true;
            _closeAfterUpdate = true;
            return;
        }

        switch (_viewModel.DecideLeave())
        {
            case LeaveDecision.Stay:
                e.Cancel = true;
                return;
            case LeaveDecision.Update:
                e.Cancel = true;
                _closeAfterUpdate = true;
                _ = UpdateThenCloseAsync();
                return;
            default:
                _viewModel.Viewer.FlushForExit();
                return;
        }
    }

    private async Task UpdateThenCloseAsync()
    {
        _ = await _viewModel.UpdateArchiveAsync();
        CloseIfRequested();
    }

    // 更新が終わるのを待っていた終了要求を、続ける。更新に失敗した場合は、再び確認が表示される
    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsUpdating) && !_viewModel.IsUpdating)
        {
            CloseIfRequested();
        }
    }

    private void CloseIfRequested()
    {
        if (!_closeAfterUpdate || _viewModel.IsUpdating || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _closeAfterUpdate = false;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void OnViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewerViewModel.IsFullScreen)
            || Dispatcher.HasShutdownStarted
            || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            SetFullScreen(_viewModel.Viewer.IsFullScreen);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => SetFullScreen(_viewModel.Viewer.IsFullScreen));
        }
    }

    private void SetFullScreen(bool enable)
    {
        if (enable)
        {
            _stateBeforeFullScreen = WindowState;
            _styleBeforeFullScreen = WindowStyle;
            _resizeBeforeFullScreen = ResizeMode;

            // いったん通常の状態に戻してから最大化しないと、タスクバーを覆わない
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullScreen;
            ResizeMode = _resizeBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
        }
    }
}
