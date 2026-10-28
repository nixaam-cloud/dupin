using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Models;

namespace WindowsNotch.ViewModels;

public partial class WaterViewModel : ObservableObject
{
    private readonly string _waterPath;
    private string _trackedDate = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private int _currentIntakeMl = 0;

    [ObservableProperty]
    private int _dailyGoalMl = 2000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReminderButtonText))]
    private bool _isReminderEnabled = true;

    [ObservableProperty]
    private string _statusMessage = "Today · 0% · Stay Hydrated";

    [ObservableProperty]
    private string _badgeText = "💧 0.0L";

    public string ReminderButtonText => IsReminderEnabled ? "🔔 Remind: ON" : "🔕 Remind: OFF";

    public event Action<string>? ReminderFired;

    private readonly DispatcherTimer _reminderTimer;

    public ObservableCollection<WaterHourStat> HourlyStats { get; } = new()
    {
        new WaterHourStat { HourLabel = "09", AmountMl = 0 },
        new WaterHourStat { HourLabel = "12", AmountMl = 0 },
        new WaterHourStat { HourLabel = "15", AmountMl = 0 },
        new WaterHourStat { HourLabel = "18", AmountMl = 0 },
        new WaterHourStat { HourLabel = "21", AmountMl = 0 }
    };

    [ObservableProperty]
    private double _intakePercentage = 0;

    public string IntakeLitersText => $"{(CurrentIntakeMl / 1000.0):0.0}";
    public string GoalLitersText => $"/ {(DailyGoalMl / 1000.0):0.0} L";
    public bool IsGoalReached => CurrentIntakeMl >= DailyGoalMl;

    public WaterViewModel()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinDir = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinDir);
        _waterPath = Path.Combine(dupinDir, "water.json");

        LoadState();
        UpdateStats(addedAmountMl: 0);

        _reminderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _reminderTimer.Tick += (s, e) =>
        {
            EnsureToday();
            if (IsReminderEnabled && !IsGoalReached)
            {
                try { SystemSounds.Asterisk.Play(); } catch { }
                ReminderFired?.Invoke($"Time to drink water! ({IntakeLitersText} / {(DailyGoalMl / 1000.0):0.0} L today) 💧");
            }
        };
        if (IsReminderEnabled)
        {
            _reminderTimer.Start();
        }
    }

    private void EnsureToday()
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (_trackedDate != today)
        {
            _trackedDate = today;
            CurrentIntakeMl = 0;
            foreach (var stat in HourlyStats) stat.AmountMl = 0;
            UpdateStats(addedAmountMl: 0);
            SaveState();
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(_waterPath))
            {
                string json = File.ReadAllText(_waterPath);
                var state = JsonSerializer.Deserialize<WaterStateDto>(json);
                if (state != null)
                {
                    DailyGoalMl = state.DailyGoalMl > 0 ? state.DailyGoalMl : 2000;
                    IsReminderEnabled = state.IsReminderEnabled;
                    string today = DateTime.Today.ToString("yyyy-MM-dd");
                    if (state.Date == today)
                    {
                        _trackedDate = today;
                        CurrentIntakeMl = Math.Max(0, state.CurrentIntakeMl);
                        if (state.HourlyAmounts != null)
                        {
                            for (int i = 0; i < Math.Min(HourlyStats.Count, state.HourlyAmounts.Count); i++)
                            {
                                HourlyStats[i].AmountMl = Math.Max(0, state.HourlyAmounts[i]);
                            }
                        }
                    }
                    else
                    {
                        _trackedDate = today;
                        CurrentIntakeMl = 0;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            var dto = new WaterStateDto
            {
                Date = _trackedDate,
                CurrentIntakeMl = CurrentIntakeMl,
                DailyGoalMl = DailyGoalMl,
                IsReminderEnabled = IsReminderEnabled,
                HourlyAmounts = HourlyStats.Select(h => h.AmountMl).ToList()
            };
            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_waterPath, json);
        }
        catch { }
    }

    [RelayCommand]
    public void Drink()
    {
        const int amount = 250;
        EnsureToday();
        CurrentIntakeMl += amount;
        UpdateStats(addedAmountMl: amount);
        SaveState();

        try { SystemSounds.Beep.Play(); } catch { }
    }

    [RelayCommand]
    public void Reset()
    {
        _trackedDate = DateTime.Today.ToString("yyyy-MM-dd");
        CurrentIntakeMl = 0;
        foreach (var stat in HourlyStats) stat.AmountMl = 0;
        UpdateStats(addedAmountMl: 0);
        SaveState();
    }

    [RelayCommand]
    public void ToggleReminder()
    {
        IsReminderEnabled = !IsReminderEnabled;
        if (IsReminderEnabled)
        {
            _reminderTimer.Stop();
            _reminderTimer.Start();
            StatusMessage = $"🔔 Remind ON (every 30m) · {IntakePercentage:0}%";
            try { SystemSounds.Asterisk.Play(); } catch { }
            ReminderFired?.Invoke($"Hydration reminders enabled (every 30m) · {IntakeLitersText}L logged today 💧");
        }
        else
        {
            _reminderTimer.Stop();
            StatusMessage = $"🔕 Remind OFF · {IntakePercentage:0}%";
        }
        SaveState();
    }

    private void UpdateStats(int addedAmountMl)
    {
        IntakePercentage = Math.Min(100.0, Math.Round((double)CurrentIntakeMl / Math.Max(1, DailyGoalMl) * 100));
        OnPropertyChanged(nameof(IntakeLitersText));
        OnPropertyChanged(nameof(GoalLitersText));
        OnPropertyChanged(nameof(IsGoalReached));

        if (IsGoalReached)
        {
            StatusMessage = "Today · 100% · Goal Reached! 🎉";
            BadgeText = "💧 Goal Reached";
        }
        else
        {
            StatusMessage = $"Today · {IntakePercentage:0}% · {(CurrentIntakeMl == 0 ? "Stay Hydrated" : "On Track")}";
            BadgeText = $"💧 {CurrentIntakeMl}ml";
        }

        if (addedAmountMl > 0)
        {
            int currentHour = DateTime.Now.Hour;
            int bucketIndex = currentHour switch
            {
                < 11 => 0,
                < 14 => 1,
                < 17 => 2,
                < 20 => 3,
                _ => 4
            };

            if (bucketIndex < HourlyStats.Count)
            {
                HourlyStats[bucketIndex].AmountMl = Math.Min(500, HourlyStats[bucketIndex].AmountMl + addedAmountMl);
            }
        }
    }

    private sealed class WaterStateDto
    {
        public string Date { get; set; } = string.Empty;
        public int CurrentIntakeMl { get; set; }
        public int DailyGoalMl { get; set; } = 2000;
        public bool IsReminderEnabled { get; set; } = true;
        public List<int>? HourlyAmounts { get; set; }
    }
}
