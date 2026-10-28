using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsNotch.Models;

public partial class TodoItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isCompleted = false;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
