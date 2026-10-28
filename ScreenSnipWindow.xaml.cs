using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsNotch;

public partial class ScreenSnipWindow : Window
{
    private Point _startPointDip;
    private Point _startPointScreenPx;
    private bool _isDragging = false;

    public byte[]? CapturedPngBytes { get; private set; }
    public BitmapSource? CapturedBitmap { get; private set; }
    public string? SavedTempFilePath { get; private set; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    private const int SRCCOPY = 0x00CC0020;

    public ScreenSnipWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void RootGrid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _startPointDip = e.GetPosition(SelectionCanvas);
        _startPointScreenPx = PointToScreen(_startPointDip);
        RootGrid.CaptureMouse();

        SelectionBorder.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, _startPointDip.X);
        Canvas.SetTop(SelectionBorder, _startPointDip.Y);
        SelectionBorder.Width = 0;
        SelectionBorder.Height = 0;
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        Point currentDip = e.GetPosition(SelectionCanvas);

        double x = Math.Min(_startPointDip.X, currentDip.X);
        double y = Math.Min(_startPointDip.Y, currentDip.Y);
        double w = Math.Abs(currentDip.X - _startPointDip.X);
        double h = Math.Abs(currentDip.Y - _startPointDip.Y);

        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = w;
        SelectionBorder.Height = h;

        SizeText.Text = $"{(int)Math.Round(w)} × {(int)Math.Round(h)}";
        Canvas.SetLeft(SizeBadge, x);
        Canvas.SetTop(SizeBadge, Math.Max(4, y - 24));
    }

    private void RootGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        RootGrid.ReleaseMouseCapture();

        Point endPointDip = e.GetPosition(SelectionCanvas);
        Point endPointScreenPx = PointToScreen(endPointDip);

        int pxLeft = (int)Math.Round(Math.Min(_startPointScreenPx.X, endPointScreenPx.X));
        int pxTop = (int)Math.Round(Math.Min(_startPointScreenPx.Y, endPointScreenPx.Y));
        int pxWidth = (int)Math.Round(Math.Abs(endPointScreenPx.X - _startPointScreenPx.X));
        int pxHeight = (int)Math.Round(Math.Abs(endPointScreenPx.Y - _startPointScreenPx.Y));

        if (pxWidth < 8 || pxHeight < 8)
        {
            DialogResult = false;
            Close();
            return;
        }

        // Hide the snip overlay first so we capture pure, un-tinted screen pixels
        this.Opacity = 0;
        this.Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(40);

        if (TryCaptureRegion(pxLeft, pxTop, pxWidth, pxHeight, out var bitmap, out var pngBytes))
        {
            CapturedBitmap = bitmap;
            CapturedPngBytes = pngBytes;

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Dupin");
                Directory.CreateDirectory(tempDir);
                string filePath = Path.Combine(tempDir, $"Dupin_Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                File.WriteAllBytes(filePath, pngBytes);
                SavedTempFilePath = filePath;
            }
            catch { }

            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }

        Close();
    }

    private static bool TryCaptureRegion(int x, int y, int width, int height, out BitmapSource? bitmapSource, out byte[] pngBytes)
    {
        bitmapSource = null;
        pngBytes = Array.Empty<byte>();

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcDest = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            hdcDest = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            hOld = SelectObject(hdcDest, hBitmap);

            if (!BitBlt(hdcDest, 0, 0, width, height, hdcScreen, x, y, SRCCOPY))
                return false;

            SelectObject(hdcDest, hOld);
            hOld = IntPtr.Zero;

            var bs = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            bitmapSource = bs;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bs));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            pngBytes = ms.ToArray();
            return pngBytes.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcDest != IntPtr.Zero) SelectObject(hdcDest, hOld);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcDest != IntPtr.Zero) DeleteDC(hdcDest);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}

