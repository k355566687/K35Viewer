using System.ComponentModel;
using System.Windows;
using K35Viewer.Infrastructure.Uninstall;

namespace K35Viewer.Views;

/// <summary>
/// アンインストールの間と、結果を出すウィンドウ。消す処理を行う「控え」のプロセスが出す。
/// 消している間は、閉じられない。終わると、結果（消した、消せなかったもの、残したもの）を出し、「閉じる」を押せる。
/// </summary>
public partial class UninstallWindow : Window
{
    private bool _allowClose;

    public UninstallWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>今していることを、表示する。</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>終わった。結果を出して、閉じられるようにする。</summary>
    public void ShowReport(UninstallReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Bar.Visibility = Visibility.Collapsed;
        var lines = new List<string>();

        if (report.Aborted)
        {
            TitleText.Text = "アンインストールを、中止しました";
            StatusText.Text = "何も削除していません。";
            lines.AddRange(report.Blockers);
        }
        else if (report.Succeeded)
        {
            TitleText.Text = "35Viewer をアンインストールしました";
            StatusText.Text = "アーカイブには、さわっていません。ご利用ありがとうございました。";
        }
        else
        {
            TitleText.Text = "一部を、削除できませんでした";
            StatusText.Text = "削除できなかった場所は、手で削除してください。アーカイブには、さわっていません。";
            lines.Add("削除できなかったもの:");
            lines.AddRange(report.Failures.Select(f => $"  {f.Path}（{f.Message}）"));
        }

        if (report.LeftoverPaths.Count > 0)
        {
            lines.Add("35Viewer のものではないファイルがあったため、データのフォルダは残しました:");
            lines.AddRange(report.LeftoverPaths.Select(p => "  " + p));
        }

        if (lines.Count > 0)
        {
            DetailBox.Text = string.Join(Environment.NewLine, lines);
            DetailBox.Visibility = Visibility.Visible;
        }

        CloseButton.Visibility = Visibility.Visible;
        _allowClose = true;
        _ = CloseButton.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
        }
    }
}
