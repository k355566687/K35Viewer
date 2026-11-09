using System.Collections.ObjectModel;
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
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);

    private readonly NotificationService _notifications;
    private readonly SynchronizationContext? _context;
    private readonly TimeSpan _lifetime;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    /// <param name="notifications">通知の発行元。</param>
    /// <param name="lifetime">通知を表示しておく時間。省略時は5秒。</param>
    public ToastHostViewModel(NotificationService notifications, TimeSpan? lifetime = null)
    {
        _notifications = notifications;
        _lifetime = lifetime ?? DefaultLifetime;
        _context = SynchronizationContext.Current; // 作成したスレッド（UIスレッド）へ表示の更新を戻す
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
    private void OnNotified(object? sender, Notification notification)
    {
        if (_context is null)
        {
            Add(notification);
        }
        else
        {
            _context.Post(static state => ((Action)state!)(), () => Add(notification));
        }
    }

    private void Add(Notification notification)
    {
        var item = new ToastItem(notification.Level, notification.Message);
        lock (_sync)
        {
            Items.Add(item);
            while (Items.Count > MaxVisible)
            {
                Items.RemoveAt(0);
            }
        }

        _ = RemoveLaterAsync(item);
    }

    private async Task RemoveLaterAsync(ToastItem item)
    {
        try
        {
            await Task.Delay(_lifetime, _cts.Token);

            // 画面では常にUIスレッドで動くが、UIのコンテキストがない環境でも、コレクションを壊さないよう排他する
            lock (_sync)
            {
                Items.Remove(item);
            }
        }
        catch (OperationCanceledException)
        {
            // 終了時
        }
    }
}
