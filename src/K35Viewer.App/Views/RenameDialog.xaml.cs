using System.Windows;
using System.Windows.Controls;

namespace K35Viewer.Views;

/// <summary>
/// 名前の変更ダイアログ（UC-12）。入力が不正なときは、理由を表示して「変更」を押せなくする。
/// 開いたときは、拡張子を除いた部分を選択する。
/// </summary>
public partial class RenameDialog : Window
{
    private readonly Func<string, string?> _validate;
    private readonly string _currentFileName;

    public RenameDialog(string currentFileName, Func<string, string?> validate)
    {
        InitializeComponent();
        _currentFileName = currentFileName;
        _validate = validate;
        NameBox.Text = currentFileName;
        NameBox.Select(0, Path.GetFileNameWithoutExtension(currentFileName).Length);
        Validate();
    }

    /// <summary>入力された名前。キャンセルされた場合は null。</summary>
    public string? NewName { get; private set; }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        // 変更していない間は、問題として表示せず、確定だけを防ぐ
        var unchanged = string.Equals(NameBox.Text, _currentFileName, StringComparison.Ordinal);
        var error = unchanged ? null : _validate(NameBox.Text);
        ErrorText.Text = error ?? string.Empty;
        OkButton.IsEnabled = !unchanged && error is null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        NewName = NameBox.Text;
        DialogResult = true;
    }
}
