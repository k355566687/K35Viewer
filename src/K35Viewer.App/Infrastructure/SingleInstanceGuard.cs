using System.Diagnostics;
using System.Runtime.InteropServices;

namespace K35Viewer.Infrastructure;

/// <summary>
/// 同じユーザーで二重に起動しないようにする。2つ目は、すでに動いている方のウィンドウを前面に出して終了する。
/// 二重に起動すると、同じDBに対してスキャンやサムネイルの作成が同時に動き、ログファイルも2つ目からは書けなくなるため。
/// </summary>
public sealed partial class SingleInstanceGuard : IDisposable
{
    private const int SwRestore = 9;

    private readonly Mutex _mutex;
    private readonly bool _owned;

    private SingleInstanceGuard(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, name, out _owned);
    }

    /// <summary>このプロセスが最初の起動か。false なら、すでに別のプロセスが動いている。</summary>
    public bool IsFirstInstance => _owned;

    /// <summary>名前つきのミューテックスで、起動済みかを確かめる。名前はユーザーのセッションごと（Local\）。</summary>
    public static SingleInstanceGuard Acquire(string name = @"Local\K35Viewer.SingleInstance") => new(name);

    /// <summary>すでに動いている方のウィンドウを、元の大きさに戻して前面に出す。見つからなければ何もしない。</summary>
    public static void ActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                if (IsIconic(other.MainWindowHandle))
                {
                    _ = ShowWindow(other.MainWindowHandle, SwRestore);
                }

                _ = SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);
}
