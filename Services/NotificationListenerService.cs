using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowsNotch.Services;

public class AppNotificationItem
{
    public uint Id { get; set; }
    public string AppName { get; set; } = "Notification";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public string TimeText => ReceivedAt.ToString("HH:mm");
    public string AppInitial => string.IsNullOrWhiteSpace(AppName) ? "N" : AppName.Substring(0, 1).ToUpperInvariant();
}

public class NotificationListenerService
{
    private UserNotificationListener? _listener;
    public bool IsListening { get; private set; } = false;

    public event Action<AppNotificationItem>? NotificationReceived;
    public ObservableCollection<AppNotificationItem> RecentNotifications { get; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            if (UserNotificationListener.Current != null)
            {
                _listener = UserNotificationListener.Current;
                var status = await _listener.RequestAccessAsync();
                if (status == UserNotificationListenerAccessStatus.Allowed)
                {
                    _listener.NotificationChanged += OnNotificationChanged;
                    IsListening = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NotificationListener init error: {ex.Message}");
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added)
        {
            try
            {
                var userNotification = sender.GetNotification(args.UserNotificationId);
                if (userNotification != null)
                {
                    string appName = userNotification.AppInfo?.DisplayInfo?.DisplayName ?? "App";
                    string title = appName;
                    string body = string.Empty;

                    var toastBinding = userNotification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (toastBinding != null)
                    {
                        var textElements = toastBinding.GetTextElements().ToList();
                        if (textElements.Count > 0)
                        {
                            title = textElements[0].Text;
                        }
                        if (textElements.Count > 1)
                        {
                            body = textElements[1].Text;
                        }
                        else if (textElements.Count > 0 && textElements[0].Text != appName)
                        {
                            body = textElements[0].Text;
                            title = appName;
                        }
                    }

                    var item = new AppNotificationItem
                    {
                        Id = userNotification.Id,
                        AppName = appName,
                        Title = title,
                        Message = body,
                        ReceivedAt = DateTime.Now
                    };

                    App.Current.Dispatcher.Invoke(() =>
                    {
                        RecentNotifications.Insert(0, item);
                        if (RecentNotifications.Count > 20)
                        {
                            RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
                        }
                        NotificationReceived?.Invoke(item);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading notification: {ex.Message}");
            }
        }
    }

    // Helper to simulate / trigger custom or test notification (e.g., from notch UI or reminders)
    public void PushNotification(string appName, string title, string message)
    {
        var item = new AppNotificationItem
        {
            Id = (uint)new Random().Next(1000, 99999),
            AppName = appName,
            Title = title,
            Message = message,
            ReceivedAt = DateTime.Now
        };

        App.Current.Dispatcher.Invoke(() =>
        {
            RecentNotifications.Insert(0, item);
            if (RecentNotifications.Count > 20)
            {
                RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
            }
            NotificationReceived?.Invoke(item);
        });
    }

    public void ClearAll()
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            RecentNotifications.Clear();
        });
    }

    public void Remove(AppNotificationItem item)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            RecentNotifications.Remove(item);
        });
    }
}
