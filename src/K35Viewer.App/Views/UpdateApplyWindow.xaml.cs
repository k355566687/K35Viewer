using System.ComponentModel;
using System.Windows;

namespace K35Viewer.Views;

/// <summary>
/// アプリのフォルダを入れ替えている間だけ出す、「更新しています」のウィンドウ。入れ替えを行う新しい版のプロセスが出す。
/// 操作できず、閉じられない。入れ替えが終わって、アプリのフォルダの実行ファイルを起動し、その画面が出るまで、残す。
/// </summary>
public partial class UpdateApplyWindow : Window
{
    private bool _allowClose;

    /// <param name="versionText">更新後の版（例: 0.2.1）。</param>
    public UpdateApplyWindow(string versionText)
    {
        InitializeComponent();
        TitleText.Text = $"35Viewer を v{versionText} に更新しています";
        Closing += OnClosing;
    }

    /// <summary>入れ替えが終わったので、閉じてよい。</summary>
    public void AllowClose() => _allowClose = true;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
        }
    }
}
