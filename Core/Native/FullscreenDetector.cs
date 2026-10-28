using System;
using System.Text;
using System.Windows.Threading;

namespace WindowsNotch.Core.Native;

public class FullscreenDetector
{
    private readonly DispatcherTimer _pollTimer;
    private readonly IntPtr _notchHwnd;
    private bool _isFullscreen;

    public event Action<bool>? FullscreenStateChanged;
    public bool IsFullscreen => _isFullscreen;

    public FullscreenDetector(IntPtr notchHwnd)
    {
        _notchHwnd = notchHwnd;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _pollTimer.Tick += OnPollTick;
    }

    public void Start() => _pollTimer.Start();
    public void Stop() => _pollTimer.Stop();

    private void OnPollTick(object? sender, EventArgs e)
    {
        bool currentFullscreen = CheckIsForegroundFullscreen();
        if (currentFullscreen != _isFullscreen)
        {
            _isFullscreen = currentFullscreen;
            FullscreenStateChanged?.Invoke(_isFullscreen);
        }
    }

    private bool CheckIsForegroundFullscreen()
    {
        IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero || fgHwnd == _notchHwnd)
        {
            return false;
        }

        // Exclude Desktop and Shell/Taskbar
        IntPtr desktopHwnd = NativeMethods.GetDesktopWindow();
        IntPtr shellHwnd = NativeMethods.GetShellWindow();
        if (fgHwnd == desktopHwnd || fgHwnd == shellHwnd)
        {
            return false;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(fgHwnd, className, className.Capacity);
        string name = className.ToString();
        if (name == "WorkerW" || name == "Progman" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(fgHwnd, out var fgRect))
        {
            return false;
        }

        IntPtr hMon = NativeMethods.MonitorFromWindow(fgHwnd, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(hMon, ref mi))
        {
            // If the foreground window bounds match or cover the monitor bounds
            if (fgRect.Left <= mi.rcMonitor.Left &&
                fgRect.Top <= mi.rcMonitor.Top &&
                fgRect.Right >= mi.rcMonitor.Right &&
                fgRect.Bottom >= mi.rcMonitor.Bottom)
            {
                return true;
            }
        }

        return false;
    }
}
