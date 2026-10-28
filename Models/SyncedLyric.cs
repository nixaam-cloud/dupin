using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsNotch.Models;

public partial class SyncedLyric : ObservableObject
{
    public TimeSpan Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isPast;

    public SyncedLyric(TimeSpan timestamp, string text)
    {
        Timestamp = timestamp;
        Text = text;
    }
}
