using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Models;

namespace WindowsNotch.ViewModels;

public partial class TodoViewModel : ObservableObject
{
    private readonly string _tasksPath;

    [ObservableProperty]
    private string _newTaskTitle = string.Empty;

    [ObservableProperty]
    private string _ratioText = "0/0";

    [ObservableProperty]
    private string _badgeText = "✓ 0/0";

    public ObservableCollection<TodoItem> Items { get; } = new();

    public int CompletedCount => Items.Count(t => t.IsCompleted);
    public int TotalCount => Items.Count;

    public TodoViewModel()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinDir = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinDir);
        _tasksPath = Path.Combine(dupinDir, "tasks.json");

        LoadTasks();
        UpdateCounts();
    }

    private void LoadTasks()
    {
        try
        {
            if (File.Exists(_tasksPath))
            {
                string json = File.ReadAllText(_tasksPath);
                var loaded = JsonSerializer.Deserialize<List<TodoItemDto>>(json);
                if (loaded != null)
                {
                    foreach (var dto in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(dto.Title))
                        {
                            Items.Add(new TodoItem { Title = dto.Title, IsCompleted = dto.IsCompleted });
                        }
                    }
                    return;
                }
            }
        }
        catch { }

        // First launch starter tasks
        Items.Add(new TodoItem { Title = "Welcome to Dupin Companion", IsCompleted = true });
        Items.Add(new TodoItem { Title = "Press Win + Ctrl + N to toggle Dupin", IsCompleted = false });
        SaveTasks();
    }

    private void SaveTasks()
    {
        try
        {
            var list = Items.Select(i => new TodoItemDto { Title = i.Title, IsCompleted = i.IsCompleted }).ToList();
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_tasksPath, json);
        }
        catch { }
    }

    [RelayCommand]
    public void AddTask()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle)) return;

        Items.Add(new TodoItem
        {
            Title = NewTaskTitle.Trim(),
            IsCompleted = false
        });

        NewTaskTitle = string.Empty;
        UpdateCounts();
        SaveTasks();

        try { SystemSounds.Beep.Play(); } catch { }
    }

    [RelayCommand]
    public void ToggleTask(TodoItem item)
    {
        if (item == null) return;
        item.IsCompleted = !item.IsCompleted;
        UpdateCounts();
        SaveTasks();

        try { SystemSounds.Beep.Play(); } catch { }
    }

    [RelayCommand]
    public void RemoveTask(TodoItem item)
    {
        if (item == null) return;
        Items.Remove(item);
        UpdateCounts();
        SaveTasks();
    }

    [RelayCommand]
    public void ClearCompleted()
    {
        var completed = Items.Where(t => t.IsCompleted).ToList();
        foreach (var item in completed)
        {
            Items.Remove(item);
        }
        UpdateCounts();
        SaveTasks();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(TotalCount));

        RatioText = $"{CompletedCount}/{TotalCount}";
        BadgeText = $"✓ {RatioText}";
    }

    private sealed class TodoItemDto
    {
        public string Title { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
    }
}
