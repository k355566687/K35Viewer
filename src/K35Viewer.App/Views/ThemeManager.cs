using System.Windows;
using K35Viewer.Infrastructure.Settings;

namespace K35Viewer.Views;

/// <summary>
/// 外観（要件定義 24.2）を、アプリ全体に当てる。WPF の Fluent テーマの ThemeMode を切り替えるだけで、
/// 開いているすべてのウィンドウが、再起動せずに切り替わる（画面の色は、テーマの色を参照しているため。開発環境設計書 5.3）。
/// </summary>
public static class ThemeManager
{
    public static ThemeMode ToThemeMode(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeMode.Light,
        AppTheme.System => ThemeMode.System,
        _ => ThemeMode.Dark,
    };

    /// <summary>アプリ全体の外観を切り替える。アプリが動いていない（テストなど）場合は何もしない。</summary>
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.ThemeMode = ToThemeMode(theme);
        }
    }
}
