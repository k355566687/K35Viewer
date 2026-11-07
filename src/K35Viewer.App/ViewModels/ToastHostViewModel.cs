using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using K35Viewer.Services;

namespace K35Viewer.ViewModels;

public sealed record ToastItem(NotificationLevel Level, string Message)
{
    /// <summary>色だけに頼らず種別を識別できるようにする記号。</summary>
    public string Symbol => Level switch
    {
        NotificationLevel.Success => "✓",
        NotificationLevel.Warning => "⚠",
        NotificationLevel.Error => "✕",
        _ => "ℹ",
    };
}

/// <summary>トースト通知の表示。通知は一定時間で自動的に消える。</summary>
public sealed class ToastHostViewModel : ObservableObject, IDisposable
{
    private const int MaxVisible = 5;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly NotificationService _notifications;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    public ToastHostViewModel(NotificationService notifications)
    {
        _notifications = notifications;
        _dispatcher = Application.Current.Dispatcher;
        _notifications.Notified += OnNotified;
    }

    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Dispose()
    {
        _notifications.Notified -= OnNotified;
        _cts.Cancel();
        _cts.Dispose();
    }

    // 通知はバックグラウンドスレッドから発行されるため、UIスレッドへ切り替えて追加する
    private void OnNotified(object? sender, Notification notification) =>
        _ = _dispatcher.InvokeAsync(() => Add(notification));

    private void Add(Notification notification)
    {
        var item = new ToastItem(notification.Level, notification.Message);
        Items.Add(item);
        while (Items.Count > MaxVisible)
        {
            Items.RemoveAt(0);
        }

        _ = RemoveLaterAsync(item);
    }

    private async Task RemoveLaterAsync(ToastItem item)
    {
        try
        {
            await Task.Delay(Lifetime, _cts.Token);
            Items.Remove(item);
        }
        catch (OperationCanceledException)
        {
            // 終了時
        }
    }
}
