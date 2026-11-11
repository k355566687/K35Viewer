namespace K35Viewer.Tests.Services;

/// <summary>
/// アプリの画面（XAML）を読み込むテストのまとまり。互いに並行して実行しない。
/// 複数のスレッドが同時にアプリの XAML（BAML）を同じアセンブリから読み込むと、.NET の内部
/// （System.IO.Packaging の PackagePart）で競合が起き、まれに例外になるため。
/// 実際のアプリは画面のスレッドが1本なので、この競合は起きない（テストだけの制約）。
/// 他のテストとは並行して実行してよい。
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfViewsGroup
{
    public const string Name = "WpfViews";
}
