namespace K35Viewer.Infrastructure.Shortcuts;

/// <summary>
/// ショートカットを置くフォルダ。Windows に場所を尋ねる（OneDrive などでリダイレクトされたデスクトップも、正しく求まる）。
/// 現在の利用者用の場所だけを使う（管理者権限は要らない）。
/// </summary>
public sealed record ShortcutLocations(string StartMenuDirectory, string DesktopDirectory)
{
    public static ShortcutLocations FromSystem() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
}
