using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
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
    public event Action<string, string>? QuickActionFeedback;
    public event Action<string>? SendToAiRequested;

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
    public void CopyFileContent(ShelfItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;

        try
        {
            string path = item.FilePath;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (File.Exists(path) && ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                Clipboard.SetImage(bmp);
                QuickActionFeedback?.Invoke("Copied Image", $"{item.FileName} copied to clipboard");
                return;
            }

            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (fi.Length <= 2 * 1024 * 1024 && IsLikelyTextFile(path, ext))
                {
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    Clipboard.SetText(content);
                    QuickActionFeedback?.Invoke("Copied Content", $"Text from {item.FileName} copied");
                    return;
                }
            }

            var dropList = new StringCollection { path };
            Clipboard.SetFileDropList(dropList);
            QuickActionFeedback?.Invoke("Copied File", $"{item.FileName} copied to clipboard");
        }
        catch (Exception ex)
        {
            QuickActionFeedback?.Invoke("Copy Error", ex.Message);
        }
    }

    private static bool IsLikelyTextFile(string path, string ext)
    {
        if (ext is ".txt" or ".md" or ".json" or ".xml" or ".csv" or ".log" or ".cs" or ".py" or ".js"
            or ".ts" or ".html" or ".css" or ".xaml" or ".yml" or ".yaml" or ".ini" or ".cfg" or ".sql" or ".sh" or ".bat" or ".ps1")
        {
            return true;
        }

        try
        {
            using var fs = File.OpenRead(path);
            int toRead = (int)Math.Min(512, fs.Length);
            byte[] buf = new byte[toRead];
            int read = fs.Read(buf, 0, toRead);
            for (int i = 0; i < read; i++)
            {
                if (buf[i] == 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    public void CompressToZip(ShelfItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;

        try
        {
            string sourcePath = item.FilePath;
            string parentDir = Path.GetDirectoryName(sourcePath) ?? Path.Combine(Path.GetTempPath(), "Dupin");
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Archive";

            string zipPath = Path.Combine(parentDir, $"{baseName}.zip");
            int suffix = 1;
            while (File.Exists(zipPath))
            {
                zipPath = Path.Combine(parentDir, $"{baseName}_{suffix++}.zip");
            }

            if (Directory.Exists(sourcePath))
            {
                ZipFile.CreateFromDirectory(sourcePath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
            }
            else if (File.Exists(sourcePath))
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.Optimal);
            }
            else
            {
                return;
            }

            AddFiles(new[] { zipPath });
            QuickActionFeedback?.Invoke("Compressed to Zip", $"Created {Path.GetFileName(zipPath)}");
        }
        catch (Exception ex)
        {
            QuickActionFeedback?.Invoke("Zip Error", ex.Message);
        }
    }

    [RelayCommand]
    public void SendToAiAnalysis(ShelfItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        SendToAiRequested?.Invoke(item.FilePath);
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
