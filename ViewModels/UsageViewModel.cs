using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Models;
using WindowsNotch.Services;

namespace WindowsNotch.ViewModels;

/// <summary>
/// Claude Code, Codex, and Antigravity usage tracking + AI coding workflow notifications.
/// </summary>
public partial class UsageViewModel : ObservableObject
{
    private readonly AiUsageTrackerService _tracker;
    private readonly string _settingsPath;
    private bool _loading;

    public ObservableCollection<AiToolUsage> Tools { get; } = new();
    public ObservableCollection<AiWorkflowEvent> RecentEvents { get; } = new();

    [ObservableProperty] private string _todayTotalText = "0";
    [ObservableProperty] private string _activeSummary = "No AI coding agents running";
    [ObservableProperty] private bool _anyActive;
    [ObservableProperty] private bool _hasEvents;
    [ObservableProperty] private bool _notifyOnComplete = true;
    [ObservableProperty] private bool _notifyClaude = true;
    [ObservableProperty] private bool _notifyCodex = true;
    [ObservableProperty] private bool _notifyAntigravity = true;

    /// <summary>Raised on the UI thread when a coding agent finishes and notifications are enabled.</summary>
    public event Action<AiWorkflowEvent>? WorkflowNotification;

    /// <summary>Raised on the UI thread after each usage refresh (used for the tray tooltip).</summary>
    public event Action? UsageRefreshed;

    public UsageViewModel()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dupin");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "usage.json");

        _tracker = new AiUsageTrackerService();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Tools.Add(new AiToolUsage { Key = AiUsageTrackerService.Claude, DisplayName = "Claude Code", AccentHex = "#D97757", DataPath = _tracker.GetRoot(AiUsageTrackerService.Claude) });
        Tools.Add(new AiToolUsage { Key = AiUsageTrackerService.Codex, DisplayName = "Codex", AccentHex = "#10A37F", DataPath = _tracker.GetRoot(AiUsageTrackerService.Codex) });
        Tools.Add(new AiToolUsage { Key = AiUsageTrackerService.Antigravity, DisplayName = "Antigravity", AccentHex = "#4F8DF7", DataPath = _tracker.GetRoot(AiUsageTrackerService.Antigravity) });

        LoadSettings();

        _tracker.SnapshotUpdated += snaps => Application.Current?.Dispatcher?.BeginInvoke(new Action(() => ApplySnapshot(snaps)));
        _tracker.WorkflowEvent += e => Application.Current?.Dispatcher?.BeginInvoke(new Action(() => OnWorkflowEvent(e)));
        _tracker.Start();
    }

    public AiToolUsage? Get(string key) => Tools.FirstOrDefault(t => t.Key == key);

    private void ApplySnapshot(IReadOnlyList<AiUsageTrackerService.ToolSnapshot> snaps)
    {
        long grand = snaps.Sum(s => s.TodayIn + s.TodayOut + s.TodayCache);
        foreach (var s in snaps)
        {
            var t = Get(s.Key);
            if (t == null) continue;
            t.IsInstalled = s.Installed;
            t.IsActive = s.Active;
            t.TodayInputTokens = s.TodayIn;
            t.TodayOutputTokens = s.TodayOut;
            t.TodayCacheTokens = s.TodayCache;
            t.TodayMessages = s.TodayTurns;
            t.TodaySessions = s.TodaySessions;
            t.WeekTotalTokens = s.WeekTotal;
            t.LastActivity = s.LastActivity;
            t.LastModel = s.LastModel;
            t.LastProject = s.LastProject;
            t.RateLimitText = s.RateLimit;
            t.TodayShare = grand > 0 ? (double)(s.TodayIn + s.TodayOut + s.TodayCache) / grand : 0;
            t.NotifyComputed();
        }

        TodayTotalText = AiToolUsage.FormatTokens(grand);
        var active = Tools.Where(t => t.IsActive).Select(t => t.DisplayName).ToList();
        AnyActive = active.Count > 0;
        ActiveSummary = AnyActive ? $"{string.Join(" + ", active)} working…" : "No AI coding agents running";
        UsageRefreshed?.Invoke();
    }

    private void OnWorkflowEvent(AiUsageTrackerService.WorkflowEventArgs e)
    {
        var tool = Get(e.ToolKey);
        var ev = new AiWorkflowEvent
        {
            ToolKey = e.ToolKey,
            ToolName = tool?.DisplayName ?? e.ToolKey,
            Title = e.Title,
            Message = e.Message,
            Project = e.Project,
            AccentHex = tool?.AccentHex ?? "#A78BFA",
            Timestamp = DateTime.Now
        };

        RecentEvents.Insert(0, ev);
        while (RecentEvents.Count > 30) RecentEvents.RemoveAt(RecentEvents.Count - 1);
        HasEvents = RecentEvents.Count > 0;

        bool toolEnabled = e.ToolKey switch
        {
            AiUsageTrackerService.Claude => NotifyClaude,
            AiUsageTrackerService.Codex => NotifyCodex,
            AiUsageTrackerService.Antigravity => NotifyAntigravity,
            _ => true
        };

        if (NotifyOnComplete && toolEnabled)
        {
            WorkflowNotification?.Invoke(ev);
        }
    }

    /// <summary>One-line summary used for the tray icon tooltip (max 127 chars).</summary>
    public string BuildTraySummary()
    {
        var parts = Tools.Where(t => t.IsInstalled && t.TodayTotalTokens > 0)
                         .Select(t => $"{ShortName(t.Key)} {t.TodayTotalText}{(t.IsActive ? "●" : "")}");
        string usage = string.Join(" • ", parts);
        string tip = string.IsNullOrEmpty(usage) ? "Dupin (Win + Ctrl + N)" : $"Dupin — today: {usage}";
        return tip.Length > 127 ? tip[..127] : tip;
    }

    private static string ShortName(string key) => key switch
    {
        AiUsageTrackerService.Claude => "Claude",
        AiUsageTrackerService.Codex => "Codex",
        AiUsageTrackerService.Antigravity => "AGY",
        _ => key
    };

    [RelayCommand]
    private void Refresh() => _tracker.ForceRefresh();

    [RelayCommand]
    private void ClearEvents()
    {
        RecentEvents.Clear();
        HasEvents = false;
    }

    [RelayCommand]
    private void OpenToolFolder(AiToolUsage? tool)
    {
        if (tool == null) return;
        try
        {
            string path = Directory.Exists(tool.DataPath) ? tool.DataPath : Path.GetDirectoryName(tool.DataPath) ?? tool.DataPath;
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            }
        }
        catch { }
    }

    partial void OnNotifyOnCompleteChanged(bool value) => SaveSettings();
    partial void OnNotifyClaudeChanged(bool value) => SaveSettings();
    partial void OnNotifyCodexChanged(bool value) => SaveSettings();
    partial void OnNotifyAntigravityChanged(bool value) => SaveSettings();

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                var r = doc.RootElement;
                if (r.TryGetProperty(nameof(NotifyOnComplete), out var a)) NotifyOnComplete = a.GetBoolean();
                if (r.TryGetProperty(nameof(NotifyClaude), out var b)) NotifyClaude = b.GetBoolean();
                if (r.TryGetProperty(nameof(NotifyCodex), out var c)) NotifyCodex = c.GetBoolean();
                if (r.TryGetProperty(nameof(NotifyAntigravity), out var d)) NotifyAntigravity = d.GetBoolean();
            }
        }
        catch { }
        finally { _loading = false; }
    }

    public void SaveSettings()
    {
        if (_loading) return;
        try
        {
            var data = new { NotifyOnComplete, NotifyClaude, NotifyCodex, NotifyAntigravity };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Dispose() => _tracker.Dispose();
}
