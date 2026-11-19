using System.Runtime.InteropServices;

namespace K35Viewer.Infrastructure.Uninstall;

/// <summary>Windows の「既知のフォルダ」（<c>%USERPROFILE%\Downloads</c> は、移されていることがあるため、Windows に尋ねる）。</summary>
public static partial class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>「ダウンロード」フォルダ。求められなければ、ユーザーのフォルダの下の Downloads。</summary>
    public static string Downloads()
    {
        try
        {
            var id = DownloadsId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) == 0 && pointer != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // 下の既定の場所を使う
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
