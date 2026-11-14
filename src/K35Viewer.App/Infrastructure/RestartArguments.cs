using System.Diagnostics;

namespace K35Viewer.Infrastructure;

/// <summary>
/// アプリの再起動（復元の入れ替えのため）の引数と、起動したときの待ち。
/// 新しいプロセスは、単一起動の仕組み（ミューテックス）に止められないよう、古いプロセスが終わるまで待ってから始める。
/// </summary>
public static class RestartArguments
{
    public const string WaitForExit = "--wait-for-exit";

    /// <summary>古いプロセスの終了を待つ最長の時間。</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>新しいプロセスへ渡す引数（終了を待つプロセスの番号）。</summary>
    public static string ForProcess(int processId) => $"{WaitForExit} {processId}";

    /// <summary>引数に、終了を待つプロセスの番号があれば、それを返す。</summary>
    public static int? ParseWaitProcessId(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], WaitForExit, StringComparison.Ordinal)
                && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>引数で指定されたプロセスが終わるまで待つ（最長 <see cref="MaxWait"/>）。すでに終わっていれば、すぐ戻る。</summary>
    public static void WaitForPreviousProcess(IReadOnlyList<string> args)
    {
        if (ParseWaitProcessId(args) is not { } id)
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(id);
            _ = previous.WaitForExit(MaxWait);
        }
        catch (ArgumentException)
        {
            // すでに終了している
        }
        catch (InvalidOperationException)
        {
            // すでに終了している
        }
    }
}