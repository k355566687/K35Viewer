using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace K35Viewer.Infrastructure.FileSystem;

public enum RecycleBinStatus
{
    /// <summary>ごみ箱へ移動した。</summary>
    Moved,

    /// <summary>ごみ箱を使えない保存先（ネットワーク、リムーバブル、ごみ箱が無効など）。何も変更していない。</summary>
    Unavailable,

    /// <summary>移動に失敗した（他のプログラムが使用中など）。</summary>
    Failed,
}

public sealed record RecycleBinResult(RecycleBinStatus Status, string? Message = null)
{
    public bool Moved => Status == RecycleBinStatus.Moved;
}

/// <summary>
/// Windowsのごみ箱への移動の境界（内部アーキテクチャ設計書 7.5）。
/// ごみ箱を使えない場合に、完全削除へ自動で切り替えない。
/// </summary>
public interface IRecycleBinService
{
    /// <summary>
    /// 指定したファイルをごみ箱へ移動できるか確認する。移動できる見込みなら null、できなければ理由（利用者向けの文）を返す。
    /// </summary>
    string? CheckAvailability(string path);

    /// <summary>ファイルをごみ箱へ移動する。利用できない場合は、何もせず <see cref="RecycleBinStatus.Unavailable"/> を返す。</summary>
    RecycleBinResult MoveToRecycleBin(string path);
}

/// <summary>
/// SHFileOperation で、ファイルをごみ箱へ移動する。
/// ネットワーク上のファイルやごみ箱が無効なドライブでは、この関数は確認なしで完全削除してしまうため、
/// 呼び出す前に利用できるかを確認し、利用できない場合は実行しない。
/// </summary>
public sealed partial class RecycleBinService : IRecycleBinService
{
    private const string VolumeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public string? CheckAvailability(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath;
        string root;
        try
        {
            fullPath = Path.GetFullPath(path);
            root = Path.GetPathRoot(fullPath) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return "ファイルの場所を確認できません";
        }

        if (root.Length == 0 || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "ネットワーク上の保存先のため、ごみ箱を使えません";
        }

        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                return "この保存先（ネットワークドライブまたはリムーバブルドライブ）では、ごみ箱を使えません";
            }
        }
        catch (ArgumentException)
        {
            return "ファイルの場所を確認できません";
        }

        // ドライブごとのごみ箱の設定（「ごみ箱に移動しないで、削除と同時にファイルを消去する」、最大サイズ）
        var settings = ReadVolumeSettings(root);
        if (settings.NukeOnDelete)
        {
            return "このドライブでは、ごみ箱に移動しない設定になっています";
        }

        if (settings.MaxCapacityMegabytes is { } max)
        {
            try
            {
                var size = new FileInfo(fullPath).Length;
                if (size > max * 1024L * 1024L)
                {
                    return string.Create(
                        CultureInfo.CurrentCulture,
                        $"ファイルがごみ箱の最大サイズ（{max:N0} MB）を超えています");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "ファイルのサイズを確認できません";
            }
        }

        return null;
    }

    public RecycleBinResult MoveToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var reason = CheckAvailability(path);
        if (reason is not null)
        {
            return new RecycleBinResult(RecycleBinStatus.Unavailable, reason);
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new RecycleBinResult(RecycleBinStatus.Failed, "ファイルが見つかりません");
        }

        // 複数のパスを区切る終端（二重のNUL）を付ける
        var from = Marshal.StringToHGlobalUni(fullPath + "\0");
        try
        {
            var operation = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = from,
                fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent),
            };

            var code = SHFileOperationW(ref operation);
            if (code != 0 || operation.fAnyOperationsAborted != 0)
            {
                return new RecycleBinResult(
                    RecycleBinStatus.Failed,
                    string.Create(CultureInfo.InvariantCulture, $"ごみ箱へ移動できませんでした（コード 0x{code:X}）"));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }

        // 移動したはずのファイルが残っていれば、失敗として扱う
        return File.Exists(fullPath)
            ? new RecycleBinResult(RecycleBinStatus.Failed, "ごみ箱へ移動できませんでした")
            : new RecycleBinResult(RecycleBinStatus.Moved);
    }

    private static (bool NukeOnDelete, int? MaxCapacityMegabytes) ReadVolumeSettings(string root)
    {
        // ボリュームのGUID（\\?\Volume{GUID}\）から、設定のキー名を決める
        var volumeName = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(root, volumeName, (uint)volumeName.Length))
        {
            return (false, null);
        }

        var name = new string(volumeName).TrimEnd('\0');
        var open = name.IndexOf('{', StringComparison.Ordinal);
        var close = name.IndexOf('}', StringComparison.Ordinal);
        if (open < 0 || close < open)
        {
            return (false, null);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{VolumeKeyPath}\{name[open..(close + 1)]}");
            if (key is null)
            {
                return (false, null); // 設定がなければ既定（ごみ箱を使う）
            }

            var nuke = key.GetValue("NukeOnDelete") is int nukeValue && nukeValue != 0;
            int? max = key.GetValue("MaxCapacity") is int maxValue && maxValue > 0 ? maxValue : null;
            return (nuke, max);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (false, null);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperationW(ref ShFileOpStruct operation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }
}
