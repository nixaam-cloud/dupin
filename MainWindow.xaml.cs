using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WindowsNotch.Core.Helpers;
using WindowsNotch.Core.Native;
using WindowsNotch.ViewModels;

namespace WindowsNotch;

public partial class MainWindow : Window
{
    private const int HOTKEY_ID = 9001;

    // Animation dimensions
    private const double MINIMAL_WIDTH = 76;
    private const double MINIMAL_HEIGHT = 22;
    private const double COLLAPSED_WIDTH = 180;
    private const double COLLAPSED_HEIGHT = 32;
    private const double HUD_WIDTH = 228;
    private const double HUD_HEIGHT = 34;
    private const double NOTIFICATION_WIDTH = 320;
    private const double NOTIFICATION_HEIGHT = 38;
    private const double EXPANDED_WIDTH = 600;
    private const double EXPANDED_HEIGHT = 224;

    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _inactivityTimer;
    private readonly DispatcherTimer _cursorMonitorTimer;
    private FullscreenDetector? _fullscreenDetector;
    private TrayIconManager? _trayManager;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _currentMonitorHandle = IntPtr.Zero;
    private bool _isExpanded = false;
    private bool _isMinimal = false;
    private bool _isForceHidden = false;

    public MainWindow()
    {
        InitializeComponent();

        _collapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        _collapseTimer.Tick += OnCollapseTimerTick;

        _inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5.5)
        };
        _inactivityTimer.Tick += OnInactivityTimerTick;

        // Multi-Monitor Cursor Tracking timer
        _cursorMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _cursorMonitorTimer.Tick += OnCursorMonitorTimerTick;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdatePosition(force: true);
        _cursorMonitorTimer.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (DataContext is MainViewModel vm)
        {
            if (string.Equals(vm.AI.SelectedProvider, "Claude", StringComparison.OrdinalIgnoreCase))
            {
                AiProviderClaude.IsChecked = true;
            }
            else if (string.Equals(vm.AI.SelectedProvider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                AiProviderOpenAi.IsChecked = true;
            }
            else
            {
                AiProviderGemini.IsChecked = true;
            }

            vm.Media.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MediaViewModel.CurrentLyricIndex))
                {
                    ScrollToActiveLyric(vm.Media.CurrentLyricIndex);
                }
            };

            vm.WakeUpRequested += () =>
            {
                if (_isMinimal)
                {
                    AnimateToCollapsed();
                }
            };

            vm.NotificationBannerTriggered += item =>
            {
                Dispatcher.Invoke(() =>
                {
                    PlayEyesAlertAnimation();
                    if (!_isExpanded)
                    {
                        AnimateToNotificationBanner();
                    }
                });
            };

            vm.NotificationDismissed += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!_isExpanded)
                    {
                        AnimateToCollapsed();
                    }
                });
            };

            vm.HardwareHudTriggered += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!_isExpanded)
                    {
                        AnimateToHardwareHudPill();
                    }
                });
            };

            vm.HardwareHudDismissed += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!_isExpanded)
                    {
                        AnimateToCollapsed();
                    }
                });
            };

            vm.RequestExpandToAi += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (AiTabButton != null) AiTabButton.IsChecked = true;
                    if (!_isExpanded)
                    {
                        AnimateExpand();
                    }
                });
            };

            vm.Weather.WeatherEffectChanged += () =>
            {
                Dispatcher.Invoke(() => ApplyWeatherEyeGlow(vm.Weather.ActiveVisualEffect));
            };
            ApplyWeatherEyeGlow(vm.Weather.ActiveVisualEffect);
        }

        ResetInactivityTimer();
    }

    private void ApplyWeatherEyeGlow(string effect)
    {
        try
        {
            string hex = effect switch
            {
                "Sun" => "#FBBF24",
                "Rain" => "#38BDF8",
                "Cloud" => "#93C5FD",
                _ => "#78BEFF"
            };
            var targetColor = (Color)ColorConverter.ConvertFromString(hex);
            var anim = new ColorAnimation
            {
                To = targetColor,
                Duration = TimeSpan.FromMilliseconds(450)
            };

            LeftEyeGlow?.BeginAnimation(DropShadowEffect.ColorProperty, anim);
            RightEyeGlow?.BeginAnimation(DropShadowEffect.ColorProperty, anim);
            PreviewLeftEyeGlow?.BeginAnimation(DropShadowEffect.ColorProperty, anim);
            PreviewRightEyeGlow?.BeginAnimation(DropShadowEffect.ColorProperty, anim);
        }
        catch { }
    }

    private void ScrollToActiveLyric(int index)
    {
        if (index < 0 || LyricsScrollViewer == null) return;
        Dispatcher.Invoke(() =>
        {
            double targetOffset = Math.Max(0, (index * 26.0) - (LyricsScrollViewer.ActualHeight / 2.0) + 13);
            LyricsScrollViewer.ScrollToVerticalOffset(targetOffset);
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;

        // Apply Win32 Extended Styles: ToolWindow (removes from Alt+Tab) & TopMost
        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        // Ensure topmost z-order
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // Register global toggle hotkey: Win + Ctrl + N
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(HwndHook);
        NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID, NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL, NativeMethods.VK_N);

        // Initialize smart fullscreen detection
        _fullscreenDetector = new FullscreenDetector(_hwnd);
        _fullscreenDetector.FullscreenStateChanged += OnFullscreenStateChanged;
        _fullscreenDetector.Start();

        // Initialize system tray icon
        _trayManager = new TrayIconManager(this, _hwnd);
        _trayManager.Initialize();
        _trayManager.ToggleRequested += ToggleVisibilityOrExpand;

        // Initialize Hardware HUD Interceptor (Volume, Mic-Mute, Brightness)
        if (DataContext is MainViewModel vm)
        {
            vm.HardwareHud.Initialize(_hwnd);
        }
    }

    private void OnCursorMonitorTimerTick(object? sender, EventArgs e)
    {
        if (!_isExpanded && !NotchContainer.IsMouseOver)
        {
            UpdatePosition(force: false);
        }
    }

    private void UpdatePosition(bool force = false)
    {
        Point topCenter = ScreenHelper.GetTopCenterPositionForCursor(this.Width, out IntPtr monHandle, 0);
        if (force || (monHandle != IntPtr.Zero && monHandle != _currentMonitorHandle))
        {
            _currentMonitorHandle = monHandle;
            this.Left = topCenter.X;
            this.Top = topCenter.Y;
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleVisibilityOrExpand();
            handled = true;
        }
        else if (msg == TrayIconManager.WM_TRAYICON)
        {
            _trayManager?.ProcessTrayMessage(lParam);
            handled = true;
        }
        else if (DataContext is MainViewModel vm)
        {
            vm.HardwareHud.ProcessWindowMessage(msg, wParam, lParam);
        }
        return IntPtr.Zero;
    }

    private void OnFullscreenStateChanged(bool isFullscreen)
    {
        Dispatcher.Invoke(() =>
        {
            if (isFullscreen)
            {
                // Suppress and hide notch during exclusive fullscreen gaming or media playback
                this.Visibility = Visibility.Collapsed;
            }
            else if (!_isForceHidden)
            {
                this.Visibility = Visibility.Visible;
            }
        });
    }

    private void ToggleVisibilityOrExpand()
    {
        if (_isForceHidden)
        {
            _isForceHidden = false;
            this.Visibility = Visibility.Visible;
        }
        else if (_isExpanded)
        {
            AnimateCollapse();
        }
        else
        {
            AnimateExpand();
        }
    }

    private void NotchContainer_MouseEnter(object sender, MouseEventArgs e)
    {
        // Browser Tab-Drag Immunity: If the user is dragging a browser tab or window near the top of the screen
        // (left mouse button already pressed when entering the notch), do NOT expand the notch.
        if (ScreenHelper.IsLeftMouseButtonDown() || e.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        _collapseTimer.Stop();
        _inactivityTimer.Stop();

        if (!_isExpanded)
        {
            AnimateExpand();
        }
    }

    private void NotchContainer_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void OnCollapseTimerTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_isExpanded && !NotchContainer.IsMouseOver)
        {
            AnimateCollapse();
        }
    }

    private void OnInactivityTimerTick(object? sender, EventArgs e)
    {
        _inactivityTimer.Stop();
        if (!_isExpanded && !_isMinimal && !NotchContainer.IsMouseOver)
        {
            if (DataContext is MainViewModel vm && !vm.Media.IsPlaying && !vm.IsPeeking && !vm.IsNotificationActive && !vm.IsHardwareHudActive)
            {
                AnimateToMinimal();
            }
        }
    }

    private void ResetInactivityTimer()
    {
        _inactivityTimer.Stop();
        if (!_isExpanded && !_isMinimal)
        {
            _inactivityTimer.Start();
        }
    }

    public void AnimateToMinimal()
    {
        if (_isExpanded || _isMinimal) return;

        if (DataContext is MainViewModel vm)
        {
            if (vm.Media.IsPlaying || vm.IsPeeking || vm.IsNotificationActive || vm.IsHardwareHudActive) return;
            vm.IsMinimal = true;
        }

        _isMinimal = true;
        _collapseTimer.Stop();
        _inactivityTimer.Stop();

        var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(300);

        var widthAnim = new DoubleAnimation
        {
            To = MINIMAL_WIDTH,
            Duration = duration,
            EasingFunction = cubicEase
        };

        var heightAnim = new DoubleAnimation
        {
            To = MINIMAL_HEIGHT,
            Duration = duration,
            EasingFunction = cubicEase
        };

        NotchContainer.BeginAnimation(WidthProperty, widthAnim);
        NotchContainer.BeginAnimation(HeightProperty, heightAnim);
    }

    public void AnimateToCollapsed()
    {
        _isMinimal = false;
        if (DataContext is MainViewModel vm)
        {
            vm.IsMinimal = false;
        }

        var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(240);

        var widthAnim = new DoubleAnimation
        {
            To = COLLAPSED_WIDTH,
            Duration = duration,
            EasingFunction = cubicEase
        };

        var heightAnim = new DoubleAnimation
        {
            To = COLLAPSED_HEIGHT,
            Duration = duration,
            EasingFunction = cubicEase
        };

        NotchContainer.BeginAnimation(WidthProperty, widthAnim);
        NotchContainer.BeginAnimation(HeightProperty, heightAnim);

        ResetInactivityTimer();
    }

    public void AnimateToHardwareHudPill()
    {
        _isMinimal = false;
        if (DataContext is MainViewModel vm)
        {
            vm.IsMinimal = false;
        }

        var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);

        var widthAnim = new DoubleAnimation
        {
            To = HUD_WIDTH,
            Duration = duration,
            EasingFunction = cubicEase
        };

        var heightAnim = new DoubleAnimation
        {
            To = HUD_HEIGHT,
            Duration = duration,
            EasingFunction = cubicEase
        };

        NotchContainer.BeginAnimation(WidthProperty, widthAnim);
        NotchContainer.BeginAnimation(HeightProperty, heightAnim);
    }

    public void AnimateToNotificationBanner()
    {
        _isMinimal = false;
        if (DataContext is MainViewModel vm)
        {
            vm.IsMinimal = false;
        }

        var bounceEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 };
        var duration = TimeSpan.FromMilliseconds(320);

        var widthAnim = new DoubleAnimation
        {
            To = NOTIFICATION_WIDTH,
            Duration = duration,
            EasingFunction = bounceEase
        };

        var heightAnim = new DoubleAnimation
        {
            To = NOTIFICATION_HEIGHT,
            Duration = duration,
            EasingFunction = bounceEase
        };

        NotchContainer.BeginAnimation(WidthProperty, widthAnim);
        NotchContainer.BeginAnimation(HeightProperty, heightAnim);
    }

    private void PlayEyesAlertAnimation()
    {
        try
        {
            if (LeftEyeScale == null || RightEyeScale == null) return;

            // Startle & widen eye animation: rapid widen, bouncy settle
            var surpriseAnimY = new DoubleAnimationUsingKeyFrames();
            surpriseAnimY.KeyFrames.Add(new SplineDoubleKeyFrame(1.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            surpriseAnimY.KeyFrames.Add(new SplineDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
            surpriseAnimY.KeyFrames.Add(new SplineDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
            surpriseAnimY.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));

            var surpriseAnimX = new DoubleAnimationUsingKeyFrames();
            surpriseAnimX.KeyFrames.Add(new SplineDoubleKeyFrame(1.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            surpriseAnimX.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));

            LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, surpriseAnimY);
            RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, surpriseAnimY);

            LeftEyeScale.BeginAnimation(ScaleTransform.ScaleXProperty, surpriseAnimX);
            RightEyeScale.BeginAnimation(ScaleTransform.ScaleXProperty, surpriseAnimX);

            // Glow shifts to radiant alert purple with a pulse
            if (LeftEyeGlow != null && RightEyeGlow != null)
            {
                var glowColor = (Color)ColorConverter.ConvertFromString("#C084FC");
                var baseColor = (Color)ColorConverter.ConvertFromString("#78BEFF");

                var glowColorAnim = new ColorAnimation
                {
                    From = glowColor,
                    To = baseColor,
                    Duration = TimeSpan.FromMilliseconds(800)
                };

                var glowBlurAnim = new DoubleAnimation
                {
                    From = 14,
                    To = 6,
                    Duration = TimeSpan.FromMilliseconds(800)
                };

                LeftEyeGlow.BeginAnimation(DropShadowEffect.ColorProperty, glowColorAnim);
                RightEyeGlow.BeginAnimation(DropShadowEffect.ColorProperty, glowColorAnim);
                LeftEyeGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, glowBlurAnim);
                RightEyeGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, glowBlurAnim);
            }
        }
        catch { }
    }

    private void NotificationBanner_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.OpenNotificationsTab();
            AnimateExpand();
        }
        e.Handled = true;
    }

    private void MascotEyes_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isExpanded)
        {
            if (_isMinimal)
            {
                AnimateToCollapsed();
            }
            else
            {
                AnimateToMinimal();
            }
            e.Handled = true;
        }
    }

    public void AnimateExpand()
    {
        _isExpanded = true;
        _isMinimal = false;
        _inactivityTimer.Stop();

        if (DataContext is MainViewModel vm)
        {
            vm.IsMinimal = false;
        }

        ExpandedContent.Visibility = Visibility.Visible;

        var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);

        // Container Width Animation
        var widthAnim = new DoubleAnimation
        {
            To = EXPANDED_WIDTH,
            Duration = duration,
            EasingFunction = cubicEase
        };

        // Container Height Animation
        var heightAnim = new DoubleAnimation
        {
            To = EXPANDED_HEIGHT,
            Duration = duration,
            EasingFunction = cubicEase
        };

        // Collapsed Content Fade Out
        var fadeOutAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = cubicEase
        };

        // Expanded Content Fade In & Translate
        var fadeInAnim = new DoubleAnimation
        {
            To = 1,
            Duration = duration,
            EasingFunction = cubicEase
        };

        var translateAnim = new DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = cubicEase
        };

        NotchContainer.BeginAnimation(WidthProperty, widthAnim);
        NotchContainer.BeginAnimation(HeightProperty, heightAnim);
        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        ExpandedTransform.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    public void AnimateCollapse()
    {
        _isExpanded = false;
        _isMinimal = false;

        double targetWidth = COLLAPSED_WIDTH;
        double targetHeight = COLLAPSED_HEIGHT;

        if (DataContext is MainViewModel vm)
        {
            vm.IsMinimal = false;
            if (vm.IsNotificationActive)
            {
                targetWidth = NOTIFICATION_WIDTH;
                targetHeight = NOTIFICATION_HEIGHT;
            }
        }

        var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(240);

        // Container Width Animation
        var widthAnim = new DoubleAnimation
        {
            To = targetWidth,
            Duration = duration,
            EasingFunction = cubicEase
        };

        // Container Height Animation
        var heightAnim = new DoubleAnimation
        {
            To = targetHeight,
            Duration = duration,
            EasingFunction = cubicEase
        };

        // Collapsed Content Fade In
        var fadeInAnim = new DoubleAnimation
        {
            To = 1,
            Duration = duration,
            EasingFunction = cubicEase
        };

        // Expanded Content Fade Out & Translate Up
        var fadeOutAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = cubicEase
        };

        var translateAnim = new DoubleAnimation
        {
            To = -10,
            Duration = duration,
            EasingFunction = cubicEase
        };

        fadeOutAnim.Completed += (s, e) =>
        {
            if (!_isExpanded)
            {
                ExpandedContent.Visibility = Visibility.Collapsed;
            }
        };

        NotchContainer.BeginAnimation(WidthProperty, widthAnim);
        NotchContainer.BeginAnimation(HeightProperty, heightAnim);
        CollapsedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        ExpandedTransform.BeginAnimation(TranslateTransform.YProperty, translateAnim);

        ResetInactivityTimer();
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tabName)
        {
            // Switch tabs dynamically
            if (DataContext is MainViewModel vm)
            {
                vm.SelectTab(tabName);
            }
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        AnimateCollapse();
    }

    private void NotchContainer_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;

            if (!_isExpanded)
            {
                AnimateExpand();
            }
        }
    }

    private void NotchContainer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.Shelf.AddFiles(files);
                    vm.SelectTab("Shelf");
                    if (ShelfTabButton != null) ShelfTabButton.IsChecked = true;
                }
            }
            e.Handled = true;
        }
    }

    private void ShelfItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement elem && elem.DataContext is Models.ShelfItem item)
        {
            // Do not start a drag-drop operation if the user is clicking one of the quick-action buttons on the card
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? cur = dep;
                while (cur != null && cur != elem)
                {
                    if (cur is Button) return;
                    cur = VisualTreeHelper.GetParent(cur);
                }
            }

            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
                DragDrop.DoDragDrop(elem, data, DragDropEffects.Copy);
            }
            catch { }
        }
    }

    private async void SnipScreenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Temporarily hide the notch so it does not obstruct the screen region being snipped
            this.Visibility = Visibility.Hidden;
            await System.Threading.Tasks.Task.Delay(120);

            var snipWindow = new ScreenSnipWindow();
            bool? result = snipWindow.ShowDialog();

            this.Visibility = Visibility.Visible;
            if (!_isExpanded)
            {
                AnimateExpand();
            }

            if (result == true && snipWindow.CapturedPngBytes != null && snipWindow.CapturedPngBytes.Length > 0)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.SelectTab("AI");
                    if (AiTabButton != null) AiTabButton.IsChecked = true;

                    string label = !string.IsNullOrEmpty(snipWindow.SavedTempFilePath)
                        ? System.IO.Path.GetFileName(snipWindow.SavedTempFilePath)
                        : $"ScreenSnip_{DateTime.Now:HHmmss}.png";

                    vm.AI.AttachImage(
                        snipWindow.CapturedPngBytes,
                        "image/png",
                        label,
                        snipWindow.CapturedBitmap);

                    await vm.AI.SendPromptAsync();
                }
            }
        }
        catch { }
    }

    private void AiProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string provider && DataContext is MainViewModel vm)
        {
            vm.AI.SelectedProvider = provider;
        }
    }

    private void AiInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            if (vm.AI.SendPromptCommand.CanExecute(null))
            {
                vm.AI.SendPromptCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void TodoInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            if (vm.Todo.AddTaskCommand.CanExecute(null))
            {
                vm.Todo.AddTaskCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void WeatherCityInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            if (vm.Weather.SearchCityCommand.CanExecute(null))
            {
                vm.Weather.SearchCityCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => UpdatePosition(force: true)));
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _collapseTimer.Stop();
        _inactivityTimer.Stop();
        _cursorMonitorTimer.Stop();
        _fullscreenDetector?.Stop();
        _trayManager?.Dispose();

        if (DataContext is MainViewModel vm)
        {
            vm.SystemStatus.Dispose();
            vm.HardwareHud.Dispose();
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID);
        }
    }
}