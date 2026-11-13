using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace K35Viewer.Tests.Services;

/// <summary>実際のWPFウィンドウを使うテストの共通処理。WPFのUIは、専用のSTAスレッドで扱う。</summary>
internal static class WpfTestHelper
{
    /// <summary>action を、専用のSTAスレッドで実行する。終わるまで待ち、例外はテストへ引き継ぐ。</summary>
    public static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>
    /// アプリと同じ見た目（Fluent テーマのダークと、アプリ独自の色）をウィンドウに当てる。
    /// テストではアプリ（App.xaml）を起動しないため、ウィンドウの単位で読み込む。
    /// </summary>
    public static T WithAppTheme<T>(T window, ThemeMode? theme = null)
        where T : Window
    {
        window.ThemeMode = theme ?? ThemeMode.Dark;
        window.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("/K35Viewer;component/Themes/AppColors.xaml", UriKind.Relative) });
        return window;
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>保留中のレイアウト・データバインディング・描画を処理する。</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>条件が満たされるまで、UIのメッセージを処理しながら待つ。</summary>
    public static void PumpUntil(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件が満たされるまで待機しましたが、タイムアウトしました。");
            }

            Pump();
            Thread.Sleep(10);
        }
    }
}
