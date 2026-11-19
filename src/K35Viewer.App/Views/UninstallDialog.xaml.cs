using System.Windows;
using System.Windows.Controls;
using K35Viewer.Infrastructure.Uninstall;
using K35Viewer.Services;
using K35Viewer.ViewModels;
using Microsoft.Win32;

namespace K35Viewer.Views;

/// <summary>
/// アンインストールの確認（GitHub の Issue #24）。「アンインストール」と入力し、バックアップを保存する場合は、
/// 保存先が正しいときだけ、「アンインストール」を押せる。既定のボタン（Enter）と Esc は、キャンセル。
/// </summary>
public partial class UninstallDialog : Window
{
    /// <summary>確認の入力に、書いてもらう言葉。</summary>
    public const string ConfirmWord = "アンインストール";

    private readonly Func<string, string?> _validate;

    public UninstallDialog(UninstallPlan plan, string defaultBackupFolder, Func<string, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InitializeComponent();
        _validate = validate;
        ProgramText.Text = string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"プログラム: {plan.Targets.ProgramDirectory}（{ByteSizeFormatter.Format(plan.ProgramBytes)}、{plan.ProgramFileCount:N0} ファイル）");
        DataText.Text = $"データ: {plan.Targets.DataDirectory}（{ByteSizeFormatter.Format(plan.DataBytes)}。DB、設定、サムネイル、ログ、バックアップ）";
        ShortcutText.Text = plan.Targets.ShortcutFiles.Count == 0
            ? "ショートカット: ありません"
            : $"ショートカット: {plan.Targets.ShortcutFiles.Count} 個（{string.Join("、", plan.Targets.ShortcutFiles)}）";
        FolderBox.Text = defaultBackupFolder;
        Refresh();
    }

    /// <summary>選ばれたバックアップの保存先。保存しない場合と、キャンセルの場合は null。</summary>
    public string? BackupFolder { get; private set; }

    private void OnChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "バックアップの保存先を選択",
            Multiselect = false,
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : string.Empty,
        };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void Refresh()
    {
        // 初期化の途中（XAML のイベントが先に届く場合）は、まだ項目がない
        if (BackupBox is null || BackupPanel is null || FolderBox is null || OkButton is null || ConfirmBox is null || FolderError is null)
        {
            return;
        }

        var backup = BackupBox.IsChecked == true;
        BackupPanel.IsEnabled = backup;
        var error = backup ? _validate(FolderBox.Text) : null;
        FolderError.Text = error ?? string.Empty;
        OkButton.IsEnabled = error is null && string.Equals(ConfirmBox.Text.Trim(), ConfirmWord, StringComparison.Ordinal);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        BackupFolder = BackupBox.IsChecked == true ? FolderBox.Text.Trim() : null;
        DialogResult = true;
    }
}
