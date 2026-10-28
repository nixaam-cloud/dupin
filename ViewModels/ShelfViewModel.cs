using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Models;

namespace WindowsNotch.ViewModels;

public partial class ShelfViewModel : ObservableObject
{
    private readonly string _shelfPath;
    private bool _isLoading = false;

    [ObservableProperty]
    private ObservableCollection<ShelfItem> _items = new();

    [ObservableProperty]
    private bool _hasItems = false;

    [ObservableProperty]
    private int _itemCount = 0;

    public event Action<int>? ItemCountChanged;

    public ShelfViewModel()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinDir = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinDir);
        _shelfPath = Path.Combine(dupinDir, "shelf.json");

        Items.CollectionChanged += (s, e) =>
        {
            ItemCount = Items.Count;
            HasItems = Items.Count > 0;
            ItemCountChanged?.Invoke(ItemCount);
            if (!_isLoading)
            {
                SaveShelf();
            }
        };

        LoadShelf();
    }

    private void LoadShelf()
    {
        _isLoading = true;
        try
        {
            if (File.Exists(_shelfPath))
            {
                string json = File.ReadAllText(_shelfPath);
                var paths = JsonSerializer.Deserialize<List<string>>(json);
                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                        {
                            Items.Add(ShelfItem.FromFilePath(p));
                        }
                    }
                }
            }
        }
        catch { }
        finally
        {
            _isLoading = false;
        }
    }

    private void SaveShelf()
    {
        try
        {
            var paths = Items.Select(i => i.FilePath).ToList();
            string json = JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_shelfPath, json);
        }
        catch { }
    }

    public void AddFiles(string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                bool exists = false;
                foreach (var existing in Items)
                {
                    if (string.Equals(existing.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    Items.Insert(0, ShelfItem.FromFilePath(path));
                }
            }
        }
    }

    [RelayCommand]
    public void RemoveItem(ShelfItem item)
    {
        Items.Remove(item);
    }

    [RelayCommand]
    public void ClearAll()
    {
        Items.Clear();
    }
}
