using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsNotch.Core.Native;

namespace WindowsNotch.Models;

public class ShelfItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileSizeFormatted { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }

    public static ShelfItem FromFilePath(string path)
    {
        var item = new ShelfItem
        {
            FilePath = path,
            FileName = Path.GetFileName(path)
        };

        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                item.FileSizeFormatted = FormatBytes(fi.Length);
            }
            else if (Directory.Exists(path))
            {
                item.FileSizeFormatted = "Folder";
            }
            else
            {
                item.FileSizeFormatted = "File";
            }

            var shinfo = new NativeMethods.SHFILEINFO();
            IntPtr ptr = NativeMethods.SHGetFileInfo(
                path,
                0,
                ref shinfo,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

            if (shinfo.hIcon != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                item.Icon = bs;
                NativeMethods.DestroyIcon(shinfo.hIcon);
            }
        }
        catch
        {
            item.FileSizeFormatted = "Item";
        }

        return item;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
