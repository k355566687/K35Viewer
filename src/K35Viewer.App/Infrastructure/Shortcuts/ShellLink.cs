using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace K35Viewer.Infrastructure.Shortcuts;

/// <summary>
/// ショートカット（.lnk）の作成と読み取り。Windows の IShellLink を使う（追加のパッケージは要らない）。
/// ショートカットの作成は、利用者が許可した操作のあとだけに使う（GitHub の Issue #23）。
/// </summary>
public static class ShellLink
{
    private const int MaxPath = 1024;
    private const uint RawPath = 4; // SLGP_RAWPATH: 環境変数を展開せず、記録のまま返す
    private const int ReadMode = 0; // STGM_READ

    /// <summary>ショートカットを作る（同じ名前があれば、上書きする）。保存先のフォルダは、あらかじめ作っておく。</summary>
    public static void Create(string linkPath, string targetPath, string workingDirectory, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            // アイコンは、リンク先の実行ファイルのもの（実行ファイルのアイコンが変われば、ショートカットにも反映される）
            link.SetIconLocation(targetPath, 0);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>ショートカットのリンク先を返す。ファイルがない、または読めないときは null。</summary>
    public static string? ReadTarget(string linkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        if (!File.Exists(linkPath))
        {
            return null;
        }

        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            ((IPersistFile)link).Load(linkPath, ReadMode);
            var buffer = new StringBuilder(MaxPath);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, RawPath);
            return buffer.Length == 0 ? null : buffer.ToString();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxChars, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxChars);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxChars);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxChars);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxChars, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(IntPtr window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
