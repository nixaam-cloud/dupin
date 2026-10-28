using System;
using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WindowsNotch.ViewModels;

public partial class TimerViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private int _totalSeconds = 25 * 60;
    private int _remainingSeconds = 25 * 60;

    [ObservableProperty]
    private string _formattedTime = "25:00";

    [ObservableProperty]
    private string _badgeText = "25m";

    [ObservableProperty]
    private bool _isRunning = false;

    [ObservableProperty]
    private double _progress = 100.0;

    [ObservableProperty]
    private string _sessionTitle = "Pomodoro Focus";

    public event Action<string>? BadgeUpdated;

    public TimerViewModel()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        UpdateFormattedTime();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_remainingSeconds > 0)
        {
            _remainingSeconds--;
            UpdateFormattedTime();
        }
        else
        {
            _timer.Stop();
            IsRunning = false;
            SessionTitle = "Focus Complete!";
            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch { }
        }
    }

    private void UpdateFormattedTime()
    {
        int minutes = _remainingSeconds / 60;
        int seconds = _remainingSeconds % 60;
        FormattedTime = $"{minutes:D2}:{seconds:D2}";
        BadgeText = $"{minutes}m";
        Progress = _totalSeconds > 0 ? ((double)_remainingSeconds / _totalSeconds) * 100.0 : 0;
        BadgeUpdated?.Invoke(BadgeText);
    }

    [RelayCommand]
    public void ToggleStartPause()
    {
        if (IsRunning)
        {
            _timer.Stop();
            IsRunning = false;
        }
        else
        {
            if (_remainingSeconds <= 0)
            {
                _remainingSeconds = _totalSeconds;
            }
            _timer.Start();
            IsRunning = true;
            SessionTitle = "Deep Focus";
        }
    }

    [RelayCommand]
    public void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        _remainingSeconds = _totalSeconds;
        SessionTitle = "Pomodoro Focus";
        UpdateFormattedTime();
    }

    [RelayCommand]
    public void SelectPreset(string minutesStr)
    {
        if (int.TryParse(minutesStr, out int mins))
        {
            _timer.Stop();
            IsRunning = false;
            _totalSeconds = mins * 60;
            _remainingSeconds = _totalSeconds;
            SessionTitle = $"{mins}m Session";
            UpdateFormattedTime();
        }
    }
}
