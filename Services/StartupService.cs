using System;
using Microsoft.Win32;

namespace WindowsNotch.Services;

public static class StartupService
{
    private const string AppName = "Dupin";
    private const string LegacyAppName = "WindowsNotch";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppName) != null || key?.GetValue(LegacyAppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            key.DeleteValue(LegacyAppName, false);

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }
}
