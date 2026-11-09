using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using K35Viewer.ViewModels;

namespace K35Viewer.Views;

public partial class ViewerView : UserControl
{
    private Point _dragStart;
    private double _dragStartHorizontal;
    private double _dragStartVertical;
    private bool _isDragging;

    public ViewerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ViewerViewModel? ViewModel => DataContext as ViewerViewModel;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateViewport();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ViewerViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is ViewerViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateViewport();
        }
    }

    // 別の頁に切り替わったら、左上から表示し直す。
    // 画面の終了処理中や別スレッドからの変更でも、終了済みのUIを操作しないようにする
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewerViewModel.CurrentImage)
            || Dispatcher.HasShutdownStarted
            || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            PageScroll.ScrollToHome();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(PageScroll.ScrollToHome);
        }
    }

    private void OnPageScrollSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();

    // 画像が表示領域より小さいときも中央に置けるよう、内容の最小の大きさを表示領域に合わせる
    private void OnPageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            PageHost.MinWidth = PageScroll.ViewportWidth;
            PageHost.MinHeight = PageScroll.ViewportHeight;
        }
    }

    private void UpdateViewport()
    {
        // 端数でスクロールバーが出入りしないよう、少し小さめに見積もる
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ViewModel?.UpdateViewport(PageScroll.ActualWidth - 2, PageScroll.ActualHeight - 2, dpiScale);
    }

    // Ctrl+ホイールで拡大・縮小。ホイールだけなら頁送り（スクロールは、ドラッグかスクロールバーで行う）
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Delta == 0)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Delta > 0)
            {
                viewModel.ZoomInCommand.Execute(null);
            }
            else
            {
                viewModel.ZoomOutCommand.Execute(null);
            }
        }
        else if (e.Delta > 0)
        {
            viewModel.PreviousCommand.Execute(null);
        }
        else
        {
            viewModel.NextCommand.Execute(null);
        }

        e.Handled = true;
    }

    // 拡大して表示領域からはみ出しているときは、ドラッグで画像を動かせる
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PageScroll.ScrollableWidth <= 0 && PageScroll.ScrollableHeight <= 0)
        {
            return;
        }

        _isDragging = true;
        _dragStart = e.GetPosition(PageScroll);
        _dragStartHorizontal = PageScroll.HorizontalOffset;
        _dragStartVertical = PageScroll.VerticalOffset;
        PageScroll.CaptureMouse();
        PageScroll.Cursor = Cursors.SizeAll;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var position = e.GetPosition(PageScroll);
        PageScroll.ScrollToHorizontalOffset(_dragStartHorizontal - (position.X - _dragStart.X));
        PageScroll.ScrollToVerticalOffset(_dragStartVertical - (position.Y - _dragStart.Y));
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        PageScroll.ReleaseMouseCapture();
        PageScroll.Cursor = Cursors.Arrow;
    }
}
