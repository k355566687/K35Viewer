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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        viewModel.Viewer.PropertyChanged += OnViewerPropertyChanged;
    }

    // ビューアの表示中だけ、キー操作をビューアへ渡す（ライブラリ画面の検索欄などの入力は妨げない）
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    // ビューアを開いたまま終了する場合も、最後に閲覧した位置を保存する
    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.Viewer.FlushForExit();

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
