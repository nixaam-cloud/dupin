using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.Appointments;
using WindowsNotch.Models;

namespace WindowsNotch.ViewModels;

public partial class CalendarViewModel : ObservableObject
{
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly string _cachePath;
    private List<OutlookCalendarEvent> _allFetchedEvents = new();
    private readonly HashSet<string> _notifiedEventKeys = new();
    private DateTime _lastKnownToday = DateTime.Today;

    [ObservableProperty]
    private string _liveClockText = DateTime.Now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _liveAmPmText = DateTime.Now.ToString("tt", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _liveDateText = DateTime.Now.ToString("dddd, MMM d, yyyy", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private DateTime _displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private string _monthHeaderText = DateTime.Today.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _syncStatusText = "Ready";

    [ObservableProperty]
    private bool _isSyncing = false;

    [ObservableProperty]
    private bool _hasTodayEvents = false;

    [ObservableProperty]
    private string _nextEventSummary = "No upcoming events today";

    [ObservableProperty]
    private string _scheduleHeaderText = "Today & Upcoming";

    public ObservableCollection<CalendarDayItem> Days { get; } = new();
    public ObservableCollection<OutlookCalendarEvent> FilteredEvents { get; } = new();

    public bool HasFilteredEvents => FilteredEvents.Count > 0;

    public event Action<string, string>? MeetingReminderFired;

    public CalendarViewModel()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinDir = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinDir);
        _cachePath = Path.Combine(dupinDir, "calendar_cache.json");

        LoadCachedEvents();
        RebuildMonthGrid();
        RefreshFilteredEvents();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (s, e) =>
        {
            var now = DateTime.Now;
            LiveClockText = now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);
            LiveAmPmText = now.ToString("tt", CultureInfo.InvariantCulture);
            LiveDateText = now.ToString("dddd, MMM d, yyyy", CultureInfo.InvariantCulture);

            // Automatically roll SelectedDate & Month Grid forward when midnight passes
            if (now.Date != _lastKnownToday)
            {
                bool wasOnYesterday = SelectedDate.Date == _lastKnownToday;
                _lastKnownToday = now.Date;
                if (wasOnYesterday)
                {
                    SelectedDate = now.Date;
                    DisplayedMonth = new DateTime(now.Year, now.Month, 1);
                }
                RebuildMonthGrid();
                RefreshFilteredEvents();
            }

            CheckUpcomingMeetingReminders(now);
        };
        _clockTimer.Start();

        // Gentle 30-minute background sync that only reads when Outlook is actively open
        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _syncTimer.Tick += async (s, e) => await SyncOutlookInternalAsync(userInitiated: false);
        _syncTimer.Start();

        _ = SyncOutlookInternalAsync(userInitiated: false);
    }

    private void LoadCachedEvents()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                string json = File.ReadAllText(_cachePath);
                var cached = JsonSerializer.Deserialize<List<OutlookCalendarEvent>>(json);
                if (cached != null && cached.Count > 0)
                {
                    _allFetchedEvents = cached.OrderBy(e => e.Start).ToList();
                    SyncStatusText = $"Cached ({_allFetchedEvents.Count})";
                }
            }
        }
        catch { }
    }

    private void SaveCachedEvents()
    {
        try
        {
            string json = JsonSerializer.Serialize(_allFetchedEvents, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch { }
    }

    private void RebuildMonthGrid()
    {
        MonthHeaderText = DisplayedMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        Days.Clear();

        DateTime firstOfMonth = new(DisplayedMonth.Year, DisplayedMonth.Month, 1);
        int startOffset = (int)firstOfMonth.DayOfWeek; // Sunday = 0
        DateTime gridStart = firstOfMonth.AddDays(-startOffset);

        for (int i = 0; i < 42; i++)
        {
            DateTime d = gridStart.AddDays(i);
            int evCount = _allFetchedEvents.Count(ev => ev.Start.Date == d.Date);
            Days.Add(new CalendarDayItem
            {
                Date = d,
                IsCurrentMonth = d.Month == DisplayedMonth.Month && d.Year == DisplayedMonth.Year,
                IsSelected = d.Date == SelectedDate.Date,
                HasEvents = evCount > 0,
                EventCount = evCount
            });
        }
    }

    private void UpdateGridEventDots()
    {
        foreach (var day in Days)
        {
            int count = _allFetchedEvents.Count(ev => ev.Start.Date == day.Date.Date);
            day.EventCount = count;
            day.HasEvents = count > 0;
            day.IsSelected = day.Date.Date == SelectedDate.Date;
        }
    }

    private void RefreshFilteredEvents()
    {
        FilteredEvents.Clear();

        if (SelectedDate.Date == DateTime.Today)
        {
            ScheduleHeaderText = "Today & Upcoming (14d)";
            var upcoming = _allFetchedEvents
                .Where(e => e.End >= DateTime.Today && e.Start <= DateTime.Today.AddDays(14))
                .OrderBy(e => e.Start)
                .ToList();

            foreach (var ev in upcoming)
                FilteredEvents.Add(ev);
        }
        else
        {
            ScheduleHeaderText = SelectedDate.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
            var onDay = _allFetchedEvents
                .Where(e => e.Start.Date == SelectedDate.Date)
                .OrderBy(e => e.Start)
                .ToList();

            foreach (var ev in onDay)
                FilteredEvents.Add(ev);
        }

        var todayEvents = _allFetchedEvents
            .Where(e => e.Start.Date == DateTime.Today && e.End >= DateTime.Now)
            .OrderBy(e => e.Start)
            .ToList();

        HasTodayEvents = todayEvents.Count > 0;
        var nextEv = todayEvents.FirstOrDefault();
        if (nextEv != null)
        {
            NextEventSummary = $"Next: {nextEv.Subject} ({nextEv.Start:HH:mm})";
        }
        else
        {
            var futureEv = _allFetchedEvents.Where(e => e.Start > DateTime.Now).OrderBy(e => e.Start).FirstOrDefault();
            NextEventSummary = futureEv != null
                ? $"Next: {futureEv.Subject} ({futureEv.DayBadgeText} {futureEv.Start:HH:mm})"
                : "No upcoming events";
        }

        OnPropertyChanged(nameof(HasFilteredEvents));
    }

    private void CheckUpcomingMeetingReminders(DateTime now)
    {
        foreach (var ev in _allFetchedEvents)
        {
            if (ev.IsAllDay) continue;
            double mins = (ev.Start - now).TotalMinutes;
            if (mins > 0 && mins <= 10.0)
            {
                string key = $"{ev.Subject}_{ev.Start:yyyyMMddHHmm}";
                if (_notifiedEventKeys.Add(key))
                {
                    MeetingReminderFired?.Invoke(
                        $"Outlook in {(int)Math.Ceiling(mins)}m",
                        $"{ev.Subject} ({ev.Start:HH:mm})");
                }
            }
        }
    }

    [RelayCommand]
    public void PreviousMonth()
    {
        DisplayedMonth = DisplayedMonth.AddMonths(-1);
        RebuildMonthGrid();
        RefreshFilteredEvents();
    }

    [RelayCommand]
    public void NextMonth()
    {
        DisplayedMonth = DisplayedMonth.AddMonths(1);
        RebuildMonthGrid();
        RefreshFilteredEvents();
    }

    [RelayCommand]
    public void JumpToToday()
    {
        SelectedDate = DateTime.Today;
        DisplayedMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RebuildMonthGrid();
        RefreshFilteredEvents();
    }

    [RelayCommand]
    public void SelectDay(CalendarDayItem? item)
    {
        if (item == null) return;
        SelectedDate = item.Date.Date;
        if (SelectedDate.Month != DisplayedMonth.Month || SelectedDate.Year != DisplayedMonth.Year)
        {
            DisplayedMonth = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);
            RebuildMonthGrid();
        }
        else
        {
            UpdateGridEventDots();
        }
        RefreshFilteredEvents();
    }

    [RelayCommand]
    public async Task SyncOutlookAsync() => await SyncOutlookInternalAsync(userInitiated: true);

    private async Task SyncOutlookInternalAsync(bool userInitiated)
    {
        if (IsSyncing) return;
        IsSyncing = true;

        try
        {
            DateTime rangeStart = new DateTime(DisplayedMonth.Year, DisplayedMonth.Month, 1).AddDays(-7);
            DateTime rangeEnd = new DateTime(DisplayedMonth.Year, DisplayedMonth.Month, 1).AddMonths(1).AddDays(14);
            if (DateTime.Today.AddDays(-1) < rangeStart)
                rangeStart = DateTime.Today.AddDays(-1);
            if (DateTime.Today.AddDays(21) > rangeEnd)
                rangeEnd = DateTime.Today.AddDays(21);

            var events = new List<OutlookCalendarEvent>();
            bool mapiReadSucceeded = false;

            // Only attach to an already-running Outlook instance (never spawn background OUTLOOK.EXE that locks .ost!)
            if (IsOutlookRunningWithWindow())
            {
                SyncStatusText = "Syncing Outlook...";
                var mapiTask = Task.Run(() => FetchOutlookMapiEventsSafe(rangeStart, rangeEnd, out mapiReadSucceeded));
                if (await Task.WhenAny(mapiTask, Task.Delay(3500)) == mapiTask)
                {
                    events = await mapiTask;
                }
            }

            // Also merge Windows AppointmentStore events (never touches Classic Outlook MAPI)
            try
            {
                var winEvents = await FetchWindowsStoreAppointmentsAsync(rangeStart, rangeEnd);
                foreach (var we in winEvents)
                {
                    if (!events.Any(e => e.Subject == we.Subject && Math.Abs((e.Start - we.Start).TotalMinutes) < 5))
                    {
                        events.Add(we);
                    }
                }
            }
            catch { }

            if (events.Count > 0 || mapiReadSucceeded)
            {
                _allFetchedEvents = events.OrderBy(e => e.Start).ToList();
                SaveCachedEvents();
                UpdateGridEventDots();
                RefreshFilteredEvents();
                SyncStatusText = $"Outlook Synced ({_allFetchedEvents.Count})";
            }
            else if (_allFetchedEvents.Count > 0)
            {
                UpdateGridEventDots();
                RefreshFilteredEvents();
                SyncStatusText = $"Cached ({_allFetchedEvents.Count})";
            }
            else
            {
                SyncStatusText = IsOutlookRunningWithWindow() ? "Outlook Synced (0)" : "Open Outlook to Sync";
            }
        }
        catch
        {
            SyncStatusText = _allFetchedEvents.Count > 0 ? $"Cached ({_allFetchedEvents.Count})" : "Outlook Idle";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private static bool IsOutlookRunningWithWindow()
    {
        try
        {
            var procs = Process.GetProcessesByName("OUTLOOK");
            bool hasActive = false;
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        hasActive = true;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            return hasActive;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);

    private static object? TryGetRunningOutlookApp()
    {
        try
        {
            if (CLSIDFromProgID("Outlook.Application", out Guid clsid) == 0)
            {
                if (GetActiveObject(ref clsid, IntPtr.Zero, out object? obj) == 0 && obj != null)
                {
                    return obj;
                }
            }
        }
        catch { }
        return null;
    }

    private static object? Call(object comObj, string method, params object[] args)
        => comObj.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, comObj, args);

    private static object? GetProp(object comObj, string prop)
        => comObj.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, comObj, null);

    private static void SetProp(object comObj, string prop, object value)
        => comObj.GetType().InvokeMember(prop, BindingFlags.SetProperty, null, comObj, new[] { value });

    private static void ReleaseCom(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try { Marshal.FinalReleaseComObject(comObj); } catch { }
        }
    }

    private static List<OutlookCalendarEvent> FetchOutlookMapiEventsSafe(DateTime rangeStart, DateTime rangeEnd, out bool succeeded)
    {
        succeeded = false;
        var results = new List<OutlookCalendarEvent>();

        object? outlookApp = null;
        object? ns = null;
        object? calendarFolder = null;
        object? items = null;
        object? restricted = null;

        try
        {
            // Attach ONLY to running Outlook in ROT — never CoCreateInstance!
            outlookApp = TryGetRunningOutlookApp();
            if (outlookApp == null) return results;

            ns = Call(outlookApp, "GetNamespace", "MAPI");
            if (ns == null) return results;

            calendarFolder = Call(ns, "GetDefaultFolder", 9); // olFolderCalendar = 9
            if (calendarFolder == null) return results;

            items = GetProp(calendarFolder, "Items");
            if (items == null) return results;

            SetProp(items, "IncludeRecurrences", true);
            Call(items, "Sort", "[Start]");

            string startStr = rangeStart.ToString("MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture);
            string endStr = rangeEnd.ToString("MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture);
            string filter = $"[Start] >= '{startStr}' AND [Start] <= '{endStr}'";

            restricted = Call(items, "Restrict", filter);
            if (restricted == null) return results;

            succeeded = true;
            object? item = Call(restricted, "GetFirst");
            int safetyCount = 0;

            while (item != null && safetyCount < 120)
            {
                safetyCount++;
                object? nextItem = null;
                try
                {
                    if (GetProp(item, "Start") is DateTime itemStart)
                    {
                        DateTime itemEnd = GetProp(item, "End") is DateTime e ? e : itemStart.AddMinutes(30);
                        if (itemEnd >= rangeStart && itemStart <= rangeEnd)
                        {
                            string subject = GetProp(item, "Subject") as string ?? "Untitled Event";
                            string location = string.Empty;
                            try { location = GetProp(item, "Location") as string ?? string.Empty; } catch { }

                            bool isAllDay = false;
                            try { if (GetProp(item, "AllDayEvent") is bool b) isAllDay = b; } catch { }

                            bool isRecurring = false;
                            try { if (GetProp(item, "IsRecurring") is bool r) isRecurring = r; } catch { }

                            string organizer = string.Empty;
                            try { organizer = GetProp(item, "Organizer") as string ?? string.Empty; } catch { }

                            string entryId = string.Empty;
                            try { entryId = GetProp(item, "EntryID") as string ?? string.Empty; } catch { }

                            results.Add(new OutlookCalendarEvent
                            {
                                EntryId = entryId,
                                Subject = subject.Trim(),
                                Start = itemStart,
                                End = itemEnd,
                                Location = location.Trim(),
                                IsAllDay = isAllDay,
                                IsRecurring = isRecurring,
                                Organizer = organizer.Trim()
                            });
                        }
                    }

                    nextItem = Call(restricted, "GetNext");
                }
                catch
                {
                    nextItem = null;
                }
                finally
                {
                    // Deterministically release each AppointmentItem immediately so MAPI handles never accumulate!
                    ReleaseCom(item);
                }

                item = nextItem;
            }

            if (item != null)
            {
                ReleaseCom(item);
            }
        }
        catch { }
        finally
        {
            ReleaseCom(restricted);
            ReleaseCom(items);
            ReleaseCom(calendarFolder);
            ReleaseCom(ns);
            ReleaseCom(outlookApp);
        }

        return results;
    }

    private static async Task<List<OutlookCalendarEvent>> FetchWindowsStoreAppointmentsAsync(DateTime rangeStart, DateTime rangeEnd)
    {
        var list = new List<OutlookCalendarEvent>();
        try
        {
            var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (store != null)
            {
                var appts = await store.FindAppointmentsAsync(rangeStart, rangeEnd - rangeStart);
                foreach (var a in appts)
                {
                    list.Add(new OutlookCalendarEvent
                    {
                        EntryId = a.LocalId ?? string.Empty,
                        Subject = string.IsNullOrWhiteSpace(a.Subject) ? "Calendar Event" : a.Subject,
                        Start = a.StartTime.LocalDateTime,
                        End = a.StartTime.LocalDateTime.Add(a.Duration),
                        Location = a.Location ?? string.Empty,
                        IsAllDay = a.AllDay,
                        Organizer = a.Organizer?.DisplayName ?? string.Empty
                    });
                }
            }
        }
        catch { }
        return list;
    }

    [RelayCommand]
    public void CreateNewOutlookEvent()
    {
        Task.Run(() =>
        {
            object? outlookApp = null;
            object? appt = null;
            try
            {
                outlookApp = TryGetRunningOutlookApp();
                if (outlookApp != null)
                {
                    appt = Call(outlookApp, "CreateItem", 1); // olAppointmentItem = 1
                    if (appt != null)
                    {
                        SetProp(appt, "Start", SelectedDate.Date.AddHours(DateTime.Now.Hour + 1));
                        SetProp(appt, "End", SelectedDate.Date.AddHours(DateTime.Now.Hour + 2));
                        Call(appt, "Display", false);
                        return;
                    }
                }
            }
            catch { }
            finally
            {
                ReleaseCom(appt);
                ReleaseCom(outlookApp);
            }

            try
            {
                Process.Start(new ProcessStartInfo("outlookcal:") { UseShellExecute = true });
            }
            catch { }
        });
    }

    [RelayCommand]
    public void OpenOutlookEvent(OutlookCalendarEvent? ev)
    {
        if (ev == null) return;

        Task.Run(() =>
        {
            object? outlookApp = null;
            object? ns = null;
            object? item = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(ev.EntryId))
                {
                    outlookApp = TryGetRunningOutlookApp();
                    if (outlookApp != null)
                    {
                        ns = Call(outlookApp, "GetNamespace", "MAPI");
                        if (ns != null)
                        {
                            item = Call(ns, "GetItemFromID", ev.EntryId);
                            if (item != null)
                            {
                                Call(item, "Display", false);
                                return;
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                ReleaseCom(item);
                ReleaseCom(ns);
                ReleaseCom(outlookApp);
            }

            try
            {
                Process.Start(new ProcessStartInfo("outlookcal:") { UseShellExecute = true });
            }
            catch { }
        });
    }
}
