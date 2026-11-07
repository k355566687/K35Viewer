using CommunityToolkit.Mvvm.ComponentModel;

namespace K35Viewer.ViewModels;

/// <summary>メインウィンドウのViewModel。画面切替(ライブラリ/ビューア)はPhase 5以降で追加する。</summary>
public sealed partial class MainViewModel(LibraryViewModel library, ToastHostViewModel toasts) : ObservableObject
{
    [ObservableProperty]
    private string _title = "35Viewer";

    public LibraryViewModel Library { get; } = library;

    public ToastHostViewModel Toasts { get; } = toasts;
}
