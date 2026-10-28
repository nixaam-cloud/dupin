using System;
using System.Runtime.InteropServices;
using System.Windows;
using WindowsNotch.Core.Native;

namespace WindowsNotch.Core.Helpers;

public static class ScreenHelper
{
    public static double PrimaryScreenWidth => SystemParameters.PrimaryScreenWidth;
    public static double PrimaryScreenHeight => SystemParameters.PrimaryScreenHeight;

    public static Point GetTopCenterPosition(double windowWidth, double yOffset = 0)
    {
        double screenWidth = PrimaryScreenWidth;
        double x = (screenWidth - windowWidth) / 2.0;
        return new Point(x, yOffset);
    }

    /// <summary>
    /// Dynamically computes the top-center position on whichever monitor currently contains the cursor.
    /// Equivalent to Screen.FromPoint(Cursor.Position) with per-monitor DPI awareness.
    /// </summary>
    public static Point GetTopCenterPositionForCursor(double windowWidth, out IntPtr monitorHandle, double yOffset = 0)
    {
        monitorHandle = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                monitorHandle = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitorHandle != IntPtr.Zero)
                {
                    var mi = new NativeMethods.MONITORINFO
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
                    };
                    if (NativeMethods.GetMonitorInfo(monitorHandle, ref mi))
                    {
                        double dpiScaleX = 1.0;
                        double dpiScaleY = 1.0;
                        try
                        {
                            if (NativeMethods.GetDpiForMonitor(monitorHandle, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                            {
                                dpiScaleX = dpiX / 96.0;
                                dpiScaleY = dpiY / 96.0;
                            }
                        }
                        catch { }

                        double monitorLeftDip = mi.rcMonitor.Left / dpiScaleX;
                        double monitorTopDip = mi.rcMonitor.Top / dpiScaleY;
                        double monitorWidthDip = mi.rcMonitor.Width / dpiScaleX;

                        double x = monitorLeftDip + (monitorWidthDip - windowWidth) / 2.0;
                        double y = monitorTopDip + yOffset;
                        return new Point(x, y);
                    }
                }
            }
        }
        catch { }

        return GetTopCenterPosition(windowWidth, yOffset);
    }

    /// <summary>
    /// Returns true if the user is currently holding the left mouse button down (e.g. dragging a browser tab or window).
    /// </summary>
    public static bool IsLeftMouseButtonDown()
    {
        try
        {
            return (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }
}
