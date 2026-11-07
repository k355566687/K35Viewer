using CommunityToolkit.Mvvm.ComponentModel;

namespace K35Viewer.ViewModels;

/// <summary>メインウィンドウのViewModel。現在は空の殻で、画面切替は後続Phaseで追加する。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "35Viewer";
}
