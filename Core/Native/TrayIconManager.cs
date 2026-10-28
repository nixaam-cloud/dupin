using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WindowsNotch.Services;

namespace WindowsNotch.Core.Native;

public class TrayIconManager : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 101;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    private static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    private readonly IntPtr _hwnd;
    private readonly Window _mainWindow;
    private NOTIFYICONDATA _nid;
    private bool _isCreated = false;
    private IntPtr _extractedIcon = IntPtr.Zero;

    public event Action? ToggleRequested;

    public TrayIconManager(Window mainWindow, IntPtr hwnd)
    {
        _mainWindow = mainWindow;
        _hwnd = hwnd;
    }

    public void Initialize()
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                IntPtr[] smallIcons = new IntPtr[1];
                if (ExtractIconEx(exePath, 0, null, smallIcons, 1) > 0 && smallIcons[0] != IntPtr.Zero)
                {
                    _extractedIcon = smallIcons[0];
                    hIcon = _extractedIcon;
                }
            }
        }
        catch { }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        }

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "Dupin (Win + Ctrl + N)"
        };

        _isCreated = Shell_NotifyIcon(NIM_ADD, ref _nid);
    }

    public void ProcessTrayMessage(IntPtr lParam)
    {
        int msg = lParam.ToInt32();
        if (msg == WM_LBUTTONUP)
        {
            ToggleRequested?.Invoke();
        }
        else if (msg == WM_RBUTTONUP)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();

        var toggleItem = new MenuItem { Header = "Toggle Dupin (Win + Ctrl + N)" };
        toggleItem.Click += (s, e) => ToggleRequested?.Invoke();
        menu.Items.Add(toggleItem);

        menu.Items.Add(new Separator());

        var startupItem = new MenuItem
        {
            Header = "Launch Dupin at Windows Startup",
            IsCheckable = true,
            IsChecked = StartupService.IsRunAtStartupEnabled()
        };
        startupItem.Click += (s, e) =>
        {
            bool willEnable = !startupItem.IsChecked;
            StartupService.SetRunAtStartup(willEnable);
            startupItem.IsChecked = willEnable;
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit Dupin" };
        exitItem.Click += (s, e) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_isCreated)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isCreated = false;
        }
        if (_extractedIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_extractedIcon);
            _extractedIcon = IntPtr.Zero;
        }
    }
}
