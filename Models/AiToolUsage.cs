using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsNotch.Models;

/// <summary>
/// Aggregated local usage stats for one AI coding tool (Claude Code, Codex, Antigravity).
/// </summary>
public partial class AiToolUsage : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Name => DisplayName;
    public string AccentHex { get; init; } = "#A78BFA";
    public string AccentBrush => AccentHex;
    public string DataPath { get; init; } = string.Empty;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private long _todayInputTokens;
    [ObservableProperty] private long _todayOutputTokens;
    [ObservableProperty] private long _todayCacheTokens;
    [ObservableProperty] private int _todayMessages;
    [ObservableProperty] private int _todaySessions;
    [ObservableProperty] private long _weekTotalTokens;
    [ObservableProperty] private DateTime? _lastActivity;
    [ObservableProperty] private string _lastModel = string.Empty;
    [ObservableProperty] private string _lastProject = string.Empty;
    [ObservableProperty] private string _rateLimitText = string.Empty;
    [ObservableProperty] private double _todayShare;

    public long TodayTotalTokens => TodayInputTokens + TodayOutputTokens + TodayCacheTokens;

    public string TodayTotalText => FormatTokens(TodayTotalTokens);
    public string TodayInOutText => $"in {FormatTokens(TodayInputTokens)} • out {FormatTokens(TodayOutputTokens)} • cache {FormatTokens(TodayCacheTokens)}";
    public string TodayBreakdownText => TodayInOutText;
    public string WeekTotalText => $"7d: {FormatTokens(WeekTotalTokens)}";
    public string SessionsText => $"{TodaySessions} sess • {TodayMessages} turns";
    public string TodayActivityText => $"{SessionsText} • {WeekTotalText}";

    public string StatusDotBrush => !IsInstalled ? "#64748B" : (IsActive ? "#34D399" : "#60A5FA");

    public string LastActivityText => LastActivity is DateTime t ? Relative(t) : "";

    public string StatusText
    {
        get
        {
            if (!IsInstalled) return "Not detected";
            if (IsActive) return "Active";
            return "Ready";
        }
    }

    public string SubtitleText
    {
        get
        {
            string model = string.IsNullOrWhiteSpace(LastModel) ? "" : LastModel;
            string proj = string.IsNullOrWhiteSpace(LastProject) ? "" : LastProject;
            if (model.Length > 0 && proj.Length > 0) return $"{model} • {proj}";
            if (model.Length > 0) return model;
            if (proj.Length > 0) return proj;
            return IsInstalled ? "No recent activity today" : "CLI / IDE logs not found";
        }
    }

    public string ModelAndProjectText => SubtitleText;

    public double ShareBarWidth => Math.Round(Math.Clamp(TodayShare, 0, 1) * 180.0, 1);

    public void NotifyComputed()
    {
        OnPropertyChanged(nameof(TodayTotalTokens));
        OnPropertyChanged(nameof(TodayTotalText));
        OnPropertyChanged(nameof(TodayInOutText));
        OnPropertyChanged(nameof(TodayBreakdownText));
        OnPropertyChanged(nameof(WeekTotalText));
        OnPropertyChanged(nameof(SessionsText));
        OnPropertyChanged(nameof(TodayActivityText));
        OnPropertyChanged(nameof(StatusDotBrush));
        OnPropertyChanged(nameof(LastActivityText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(ModelAndProjectText));
        OnPropertyChanged(nameof(ShareBarWidth));
    }

    public static string FormatTokens(long n)
    {
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000.0:0.##}B";
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.##}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}K";
        return n.ToString();
    }

    private static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}

/// <summary>
/// A detected AI coding workflow event (turn finished, task complete, etc.).
/// </summary>
public class AiWorkflowEvent
{
    public string ToolKey { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string AccentHex { get; init; } = "#A78BFA";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string TimeText => Timestamp.ToString("HH:mm");
}

