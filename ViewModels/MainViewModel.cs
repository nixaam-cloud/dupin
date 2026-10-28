using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Models;
using WindowsNotch.Services;

namespace WindowsNotch.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private NotchState _state = NotchState.Collapsed;

    [ObservableProperty]
    private NotchTab _activeTab = NotchTab.Media;

    [ObservableProperty]
    private bool _isTimerRunning = false;

    [ObservableProperty]
    private string _timerBadge = "25m";

    [ObservableProperty]
    private int _shelfCount = 0;

    [ObservableProperty]
    private bool _isFullscreenSuppressed = false;

    [ObservableProperty]
    private bool _isForceHidden = false;

    // Peek notification state (for Charger, Bluetooth, CapsLock, Water Reminder)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormalCollapsed))]
    private bool _isPeeking = false;

    [ObservableProperty]
    private string _peekTitle = string.Empty;

    [ObservableProperty]
    private string _peekSubtitle = string.Empty;

    // Windows System & App Notification State
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormalCollapsed))]
    private bool _isNotificationActive = false;

    [ObservableProperty]
    private AppNotificationItem? _activeNotification;

    [ObservableProperty]
    private int _notificationCount = 0;

    // Hardware HUD Interceptor State (Volume, Mic-Mute, Brightness)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormalCollapsed))]
    private bool _isHardwareHudActive = false;

    [ObservableProperty]
    private string _hudTitle = "Volume";

    [ObservableProperty]
    private string _hudValueText = "50%";

    [ObservableProperty]
    private int _hudPercentage = 50;

    [ObservableProperty]
    private double _hudBarWidth = 36.0;

    [ObservableProperty]
    private bool _isHudVolume = true;

    [ObservableProperty]
    private bool _isHudVolumeMuted = false;

    [ObservableProperty]
    private bool _isHudMicMuted = false;

    [ObservableProperty]
    private bool _isHudMicUnmuted = false;

    [ObservableProperty]
    private bool _isHudBrightness = false;

    public bool IsNormalCollapsed => !IsNotificationActive && !IsPeeking && !IsHardwareHudActive;

    // Battery / Power
    [ObservableProperty]
    private int _batteryPercent = 100;

    [ObservableProperty]
    private bool _isCharging = true;

    // Bluetooth / Headphones Status
    [ObservableProperty]
    private string _bluetoothDeviceName = "AirBuds Pro";

    [ObservableProperty]
    private bool _isBluetoothConnected = true;

    [ObservableProperty]
    private int _bluetoothBatteryPercent = 70;

    // Resting Mascot mode (when no music is playing and idle)
    [ObservableProperty]
    private bool _isRestingMascot = true;

    [ObservableProperty]
    private bool _isMinimal = false;

    [ObservableProperty]
    private string _currentTimeText = DateTime.Now.ToString("HH:mm");

    public event Action? WakeUpRequested;
    public event Action<AppNotificationItem>? NotificationBannerTriggered;
    public event Action? NotificationDismissed;
    public event Action? HardwareHudTriggered;
    public event Action? HardwareHudDismissed;
    public event Action? RequestExpandToAi;

    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _hudTimer;

    public MediaViewModel Media { get; }
    public TimerViewModel Timer { get; }
    public WaterViewModel Water { get; }
    public WeatherViewModel Weather { get; }
    public TodoViewModel Todo { get; }
    public ShelfViewModel Shelf { get; }
    public AiAssistantViewModel AI { get; }
    public SystemStatusService SystemStatus { get; }
    public NotificationListenerService NotificationListener { get; }
    public HardwareHudService HardwareHud { get; }

    public bool IsExpanded => State == NotchState.Expanded;

    public MainViewModel()
    {
        var mediaService = new MediaSessionService();
        var lyricsService = new LyricsService();
        var aiService = new AiClientService();
        SystemStatus = new SystemStatusService();
        NotificationListener = new NotificationListenerService();
        HardwareHud = new HardwareHudService();

        Media = new MediaViewModel(mediaService, lyricsService);
        Timer = new TimerViewModel();
        Water = new WaterViewModel();
        Weather = new WeatherViewModel();
        Todo = new TodoViewModel();
        Shelf = new ShelfViewModel();
        AI = new AiAssistantViewModel(aiService);

        Timer.BadgeUpdated += badge => TimerBadge = badge;
        Shelf.ItemCountChanged += count => ShelfCount = count;
        Shelf.QuickActionFeedback += (title, sub) => TriggerPeek(title, sub);
        Shelf.SendToAiRequested += path =>
        {
            ActiveTab = NotchTab.AI;
            RequestExpandToAi?.Invoke();
            _ = AI.AnalyzeFileFromShelfAsync(path);
        };
        AI.ExportToShelfRequested += path => Shelf.AddFiles(new[] { path });
        HardwareHud.HudTriggered += OnHardwareHudTriggered;

        Water.ReminderFired += reminder =>
        {
            TriggerPeek("Water Reminder", reminder);
            NotificationListener.PushNotification("Water", "Hydration Reminder 💧", reminder);
        };
        SystemStatus.PeekNotificationTriggered += (title, sub) => TriggerPeek(title, sub);
        SystemStatus.StatusUpdated += () =>
        {
            BatteryPercent = SystemStatus.BatteryPercent;
            IsCharging = SystemStatus.IsCharging;
        };

        NotificationListener.RecentNotifications.CollectionChanged += (s, e) =>
        {
            NotificationCount = NotificationListener.RecentNotifications.Count;
        };
        NotificationListener.NotificationReceived += OnNotificationReceived;
        _ = NotificationListener.InitializeAsync();
        NotificationCount = NotificationListener.RecentNotifications.Count;

        Media.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Media.IsPlaying))
            {
                IsRestingMascot = !Media.IsPlaying;
                if (Media.IsPlaying)
                {
                    IsMinimal = false;
                    WakeUpRequested?.Invoke();
                }
            }
        };

        _peekTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        _peekTimer.Tick += (s, e) =>
        {
            _peekTimer.Stop();
            IsPeeking = false;
        };

        _notificationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5.0)
        };
        _notificationTimer.Tick += (s, e) =>
        {
            _notificationTimer.Stop();
            DismissActiveNotification();
        };

        _hudTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.0)
        };
        _hudTimer.Tick += (s, e) =>
        {
            _hudTimer.Stop();
            IsHardwareHudActive = false;
            HardwareHudDismissed?.Invoke();
        };

        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clockTimer.Tick += (s, e) => CurrentTimeText = DateTime.Now.ToString("HH:mm");
        clockTimer.Start();

        _ = mediaService.InitializeAsync();
    }

    private void OnHardwareHudTriggered(HardwareHudEventArgs e)
    {
        HudTitle = e.Title;
        HudValueText = e.ValueText;
        HudPercentage = Math.Clamp(e.Percentage, 0, 100);
        HudBarWidth = Math.Round((HudPercentage / 100.0) * 72.0, 1);

        IsHudVolume = e.HudType == HardwareHudType.Volume;
        IsHudVolumeMuted = e.HudType == HardwareHudType.VolumeMuted;
        IsHudMicMuted = e.HudType == HardwareHudType.MicMuted;
        IsHudMicUnmuted = e.HudType == HardwareHudType.MicUnmuted;
        IsHudBrightness = e.HudType == HardwareHudType.Brightness;

        IsHardwareHudActive = true;
        IsMinimal = false;
        HardwareHudTriggered?.Invoke();

        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void OnNotificationReceived(AppNotificationItem item)
    {
        ActiveNotification = item;
        NotificationCount = NotificationListener.RecentNotifications.Count;
        IsNotificationActive = true;
        IsMinimal = false;
        WakeUpRequested?.Invoke();

        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }

        NotificationBannerTriggered?.Invoke(item);

        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    [RelayCommand]
    public void DismissActiveNotification()
    {
        _notificationTimer.Stop();
        IsNotificationActive = false;
        NotificationDismissed?.Invoke();
    }

    [RelayCommand]
    public void OpenNotificationsTab()
    {
        DismissActiveNotification();
        ActiveTab = NotchTab.Notifications;
        Expand();
    }

    [RelayCommand]
    public void TriggerTestNotification()
    {
        string[] apps = { "Slack", "WhatsApp", "Outlook", "Teams", "Discord", "GitHub" };
        string[] senders = { "Sarah Connor", "Alex Vance", "Design Team", "System Monitor", "David Kim", "Security Ops" };
        string[] messages = {
            "Can you check the latest pull request? Build passed! 🚀",
            "Hey! The new live notch animation looks unreal ✨",
            "Meeting scheduled for 3:00 PM: Product Showcase 📅",
            "New mention in #windows-notch-releases: v2.0 is live",
            "Deployment to production completed successfully! ⚡",
            "Shared a file: 'Project_Assets_Final.zip' (14.2 MB) 📦"
        };

        var rng = new Random();
        int idx = rng.Next(apps.Length);
        NotificationListener.PushNotification(apps[idx], senders[idx], messages[idx]);
    }

    [RelayCommand]
    public void ClearNotifications()
    {
        NotificationListener.ClearAll();
        NotificationCount = 0;
        DismissActiveNotification();
    }

    [RelayCommand]
    public void RemoveNotification(AppNotificationItem item)
    {
        if (item != null)
        {
            NotificationListener.Remove(item);
            NotificationCount = NotificationListener.RecentNotifications.Count;
            if (ActiveNotification == item)
            {
                DismissActiveNotification();
            }
        }
    }

    [RelayCommand]
    public void CopyNotification(AppNotificationItem item)
    {
        if (item != null)
        {
            try
            {
                System.Windows.Clipboard.SetText($"{item.AppName} - {item.Title}: {item.Message}");
                TriggerPeek("Copied", $"{item.AppName} notification copied");
            }
            catch { }
        }
    }

    public void TriggerPeek(string title, string subtitle)
    {
        PeekTitle = title;
        PeekSubtitle = subtitle;
        IsPeeking = true;
        IsMinimal = false;
        WakeUpRequested?.Invoke();

        _peekTimer.Stop();
        _peekTimer.Start();
    }

    [RelayCommand]
    public void Expand()
    {
        if (State != NotchState.Expanded)
        {
            State = NotchState.Expanded;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    [RelayCommand]
    public void Collapse()
    {
        if (State != NotchState.Collapsed)
        {
            State = NotchState.Collapsed;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    [RelayCommand]
    public void ToggleExpand()
    {
        if (State == NotchState.Expanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    [RelayCommand]
    public void SelectTab(string tabName)
    {
        if (Enum.TryParse<NotchTab>(tabName, true, out var tab))
        {
            ActiveTab = tab;
        }
    }

    [RelayCommand]
    public void ToggleForceHide()
    {
        IsForceHidden = !IsForceHidden;
    }
}
