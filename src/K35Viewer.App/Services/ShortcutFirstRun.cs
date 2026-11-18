using K35Viewer.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>
/// はじめて起動したときの、ショートカットの確認（GitHub の Issue #23）。聞くのは、一度だけ。
/// 利用者が許可したものだけを作る（確認なしでは、作らない）。作らなかった場合も、あとから設定画面で作れる。
/// </summary>
public sealed partial class ShortcutFirstRun(
    SettingsService settings,
    IShortcutService shortcuts,
    IShortcutDialogs dialogs,
    INotificationService notifications,
    ILogger<ShortcutFirstRun> logger)
{
    /// <summary>
    /// 必要なら、確認を出して、選ばれたショートカットを作る。次の場合は、何も聞かない。
    /// ・すでに聞いた（<see cref="AppSettings.ShortcutPromptShown"/>）
    /// ・ショートカットを作れない場所から動いている（ZIP の中から直接起動した、など。聞いたことにはしない。あとで、正しい場所から起動したときに聞く）
    /// ・スタートメニューのショートカットが、すでに今の実行ファイルを指している（聞いたことにする）
    /// 例外は投げない。
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            if (settings.Current.ShortcutPromptShown)
            {
                return;
            }

            if (shortcuts.Availability != ShortcutAvailability.Available)
            {
                return;
            }

            if (shortcuts.GetState(ShortcutPlace.StartMenu) == ShortcutState.Current)
            {
                _ = settings.Update(s => s with { ShortcutPromptShown = true });
                return;
            }

            var request = dialogs.PromptFirstRun();

            // 答えたら、聞いたことにする（「今はしない」でも、次の起動で、また聞かない）
            _ = settings.Update(s => s with { ShortcutPromptShown = true });
            if (request is null || request.IsEmpty)
            {
                return;
            }

            ShortcutMessages.Report(notifications, await shortcuts.CreateAsync(request));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ショートカットの確認で、問題が起きました。")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>ショートカットを作った結果の、利用者への知らせ。</summary>
public static class ShortcutMessages
{
    public static string PlaceName(ShortcutPlace place) => place == ShortcutPlace.StartMenu ? "スタートメニュー" : "デスクトップ";

    /// <summary>結果を、通知（トースト）で知らせる。成功と失敗は、別の通知にする。</summary>
    public static void Report(INotificationService notifications, ShortcutOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Created.Count > 0)
        {
            notifications.ShowSuccess(
                string.Join("、", outcome.Created.Select(PlaceName)) + "にショートカットを作りました");
        }

        foreach (var failure in outcome.Failures)
        {
            notifications.ShowError($"{PlaceName(failure.Place)}にショートカットを作れませんでした（{failure.Message}）");
        }
    }
}
