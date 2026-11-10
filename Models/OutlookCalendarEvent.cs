using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsNotch.Models;

public class OutlookCalendarEvent
{
    public string EntryId { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Location { get; init; } = string.Empty;
    public bool IsAllDay { get; init; }
    public bool IsRecurring { get; init; }
    public string Organizer { get; init; } = string.Empty;
    public string JoinUrl { get; init; } = string.Empty;

    public bool IsToday => Start.Date == DateTime.Today;
    public bool IsHappeningNow => DateTime.Now >= Start && DateTime.Now <= End;

    public string DayBadgeText
    {
        get
        {
            if (Start.Date == DateTime.Today) return "Today";
            if (Start.Date == DateTime.Today.AddDays(1)) return "Tomorrow";
            return Start.ToString("ddd, MMM d");
        }
    }

    public string TimeRangeText => IsAllDay
        ? "All Day"
        : $"{Start:HH:mm} – {End:HH:mm}";

    public string StatusBadgeText
    {
        get
        {
            var now = DateTime.Now;
            if (now >= Start && now <= End)
                return "NOW";
            if (Start > now && (Start - now).TotalMinutes <= 90)
                return $"in {Math.Max(1, (int)Math.Round((Start - now).TotalMinutes))}m";
            if (End < now && Start.Date == DateTime.Today)
                return "Done";
            return string.Empty;
        }
    }

    public bool HasStatusBadge => !string.IsNullOrEmpty(StatusBadgeText);
    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);
}

public partial class CalendarDayItem : ObservableObject
{
    public DateTime Date { get; init; }
    public string DayNumberText => Date.Day.ToString();
    public bool IsCurrentMonth { get; init; }
    public bool IsToday => Date.Date == DateTime.Today;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasEvents;

    [ObservableProperty]
    private int _eventCount;
}
