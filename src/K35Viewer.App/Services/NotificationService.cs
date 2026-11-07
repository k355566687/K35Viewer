namespace K35Viewer.Services;

public enum NotificationLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record Notification(NotificationLevel Level, string Message);

/// <summary>アプリ内トースト通知の共通窓口。</summary>
public interface INotificationService
{
    void ShowInfo(string message);

    void ShowSuccess(string message);

    void ShowWarning(string message);

    void ShowError(string message);
}

/// <summary>
/// 通知を <see cref="Notified"/> イベントとして発行する。
/// バックグラウンドスレッドから呼ばれるため、購読側(UI)でスレッドを切り替えること。
/// </summary>
public sealed class NotificationService : INotificationService
{
    public event EventHandler<Notification>? Notified;

    public void ShowInfo(string message) => Publish(NotificationLevel.Info, message);

    public void ShowSuccess(string message) => Publish(NotificationLevel.Success, message);

    public void ShowWarning(string message) => Publish(NotificationLevel.Warning, message);

    public void ShowError(string message) => Publish(NotificationLevel.Error, message);

    private void Publish(NotificationLevel level, string message) =>
        Notified?.Invoke(this, new Notification(level, message));
}
