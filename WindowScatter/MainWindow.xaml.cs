using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    public partial class MainWindow : Window
    {
        // Constants
        private const double ANIMATION_DURATION = 0.25;
        private const int MAX_WINDOWS = 20;
        private const double DWM_UPDATE_MIN_MS = 1.0;
        private const double TARGET_BLUR_RADIUS = 40;

        // Core state
        private List<WindowThumb> windowThumbs = new List<WindowThumb>();
        private List<WindowLayout> cachedLayouts = null;
        private List<IntPtr> lastWindowHandles = null;
        private Random rng = new Random();
        private DWM_THUMBNAIL_PROPERTIES sharedProps;

        // Animation state
        private DateTime animStart;
        private DateTime lastDwmUpdate = DateTime.MinValue;
        private double currentBlurRadius = 0;
        private bool isReturningToOriginal = false;

        // Hotkey state
        private IntPtr hookID = IntPtr.Zero;
        private LowLevelKeyboardProc hookCallback;
        private HotkeyParser configuredHotkey;
        private bool isWinKeyPressed = false;
        private bool isCtrlPressed = false;
        private bool isAltPressed = false;
        private bool isShiftPressed = false;
        private bool suppressStartMenu = false;
        private DateTime startMenuSuppressionStart;

        // Wallpaper state
        private HwndSource hwndSource;
        private System.Windows.Threading.DispatcherTimer wallpaperCheckTimer;
        private string lastWallpaperPath = null;
        private WallpaperManager wallpaperManager;

        private bool isScatterActive = false;
        private bool isAnimatingBack = false;

        // NEW: Prevent click spam during transitions
        private bool isTransitioning = false;
        private object transitionLock = new object();

        // Settings
        private AppSettings settings;

        public MainWindow()
        {
            InitializeComponent();

            settings = AppSettings.Load();
            configuredHotkey = HotkeyParser.Parse(settings.Hotkey);

            this.Topmost = true;
            this.ShowActivated = true;
            this.Focusable = true;
            this.WindowStyle = WindowStyle.None;
            this.ResizeMode = ResizeMode.NoResize;
            this.WindowState = WindowState.Maximized;
            this.AllowsTransparency = false;

            sharedProps = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            wallpaperManager = new WallpaperManager(BackgroundImage, this);
            wallpaperManager.SetWallpaperBackground();
            SetupWallpaperMonitoring();

            this.Loaded += (s, e) =>
            {
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                this.Visibility = Visibility.Hidden;
            };

            hookCallback = HookCallback;
            hookID = SetHook(hookCallback);
        }

        #region Wallpaper Monitoring

        private void SetupWallpaperMonitoring()
        {
            wallpaperCheckTimer = new System.Windows.Threading.DispatcherTimer();
            wallpaperCheckTimer.Interval = TimeSpan.FromSeconds(2);
            wallpaperCheckTimer.Tick += (s, e) =>
            {
                string currentPath = wallpaperManager.GetWallpaperPath();
                if (currentPath != lastWallpaperPath && !string.IsNullOrEmpty(currentPath))
                {
                    lastWallpaperPath = currentPath;
                    wallpaperManager.SetWallpaperBackground();
                }
            };
            wallpaperCheckTimer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            hwndSource?.AddHook(WndProc);

            lastWallpaperPath = wallpaperManager.GetWallpaperPath();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SETTINGCHANGE)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    string newPath = wallpaperManager.GetWallpaperPath();
                    if (newPath != lastWallpaperPath)
                    {
                        lastWallpaperPath = newPath;
                        wallpaperManager.SetWallpaperBackground();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }

            return IntPtr.Zero;
        }

        #endregion

        #region Keyboard Hook

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    // Track modifier keys
                    if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                        isWinKeyPressed = true;
                    if (vkCode == VK_CONTROL)
                        isCtrlPressed = true;
                    if (vkCode == VK_MENU)
                        isAltPressed = true;
                    if (vkCode == VK_SHIFT)
                        isShiftPressed = true;

                    // Check if hotkey matches
                    if (vkCode == configuredHotkey.VirtualKeyCode && CheckModifiersMatch())
                    {
                        suppressStartMenu = true;
                        startMenuSuppressionStart = DateTime.Now;

                        this.Dispatcher.BeginInvoke(new Action(async () =>
                        {
                            // FIXED: Check isTransitioning too
                            if (this.Visibility == Visibility.Hidden && !isScatterActive && !isAnimatingBack && !isTransitioning)
                            {
                                await StartScatterAsync();
                                this.Visibility = Visibility.Visible;
                                this.Activate();
                                this.Focus();

                                await System.Threading.Tasks.Task.Delay(250);
                                ReleaseModifierKeys();
                            }
                        }));

                        return (IntPtr)1;
                    }
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    // Handle modifier key releases
                    if ((vkCode == VK_LWIN || vkCode == VK_RWIN) && suppressStartMenu)
                    {
                        if ((DateTime.Now - startMenuSuppressionStart).TotalMilliseconds < 500)
                            return (IntPtr)1;

                        suppressStartMenu = false;
                        isWinKeyPressed = false;
                    }
                    else if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                        isWinKeyPressed = false;
                    if (vkCode == VK_CONTROL)
                        isCtrlPressed = false;
                    if (vkCode == VK_MENU)
                        isAltPressed = false;
                    if (vkCode == VK_SHIFT)
                        isShiftPressed = false;
                }
            }

            return CallNextHookEx(hookID, nCode, wParam, lParam);
        }

        private bool CheckModifiersMatch()
        {
            // Check if current modifier state matches configured hotkey
            bool winMatch = ((configuredHotkey.ModifierKeys & 0x08) != 0) == isWinKeyPressed;
            bool ctrlMatch = ((configuredHotkey.ModifierKeys & 0x01) != 0) == isCtrlPressed;
            bool altMatch = ((configuredHotkey.ModifierKeys & 0x02) != 0) == isAltPressed;
            bool shiftMatch = ((configuredHotkey.ModifierKeys & 0x04) != 0) == isShiftPressed;

            return winMatch && ctrlMatch && altMatch && shiftMatch;
        }

        private void ReleaseModifierKeys()
        {
            isWinKeyPressed = false;
            isCtrlPressed = false;
            isAltPressed = false;
            isShiftPressed = false;
            suppressStartMenu = false;

            keybd_event((byte)VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event((byte)VK_RWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event((byte)VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        #endregion

        #region Event Handlers

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // FIXED: Reject ESC spam during transitions
            if (e.Key == Key.Escape && !isAnimatingBack && !isTransitioning)
            {
                CleanupAndClose();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            hwndSource?.RemoveHook(WndProc);
            wallpaperCheckTimer?.Stop();

            if (hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookID);
                hookID = IntPtr.Zero;
            }

            base.OnClosed(e);
        }

        #endregion

        #region Core Scatter Logic

        private async System.Threading.Tasks.Task StartScatterAsync()
        {
            if (isScatterActive || isAnimatingBack || isTransitioning) return;
            isScatterActive = true;
            try
            {
                var helper = new WindowInteropHelper(this);
                IntPtr ourHwnd = helper.EnsureHandle();

                if (ourHwnd == IntPtr.Zero)
                {
                    MessageBox.Show("Window handle fucked!", "Error");
                    return;
                }

                Cleanup();

                var windows = EnumerateWindows();
                if (windows.Count == 0)
                {
                    MessageBox.Show("No windows found!", "Info");
                    return;
                }

                if (windows.Count > MAX_WINDOWS)
                    windows = windows.GetRange(0, MAX_WINDOWS);

                var currentHandles = windows.Select(w => w.Handle).ToList();
                bool windowStateChanged = HasWindowStateChanged(currentHandles);

                double screenW = SystemParameters.PrimaryScreenWidth;
                double screenH = SystemParameters.PrimaryScreenHeight;

                List<WindowLayout> layouts;

                if (!windowStateChanged && cachedLayouts != null)
                {
                    layouts = ReuseCachedLayouts(windows);
                }
                else
                {
                    layouts = CalculateNewLayouts(windows, screenW, screenH);
                    cachedLayouts = new List<WindowLayout>(layouts);
                    lastWindowHandles = currentHandles;
                }

                SetWindowPos(ourHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                RegisterThumbnails(layouts, ourHwnd);

                if (windowThumbs.Count == 0)
                {
                    MessageBox.Show("No windows captured!", "Info");
                    return;
                }

                animStart = DateTime.Now;
                lastDwmUpdate = DateTime.MinValue;
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
            finally
            {
                isScatterActive = false;
            }
        }

        private List<WindowInfo> EnumerateWindows()
        {
            var windows = new List<WindowInfo>();

            EnumWindows((h, l) =>
            {
                try
                {
                    if (!IsWindowVisible(h) || IsIconic(h))
                        return true;

                    int len = GetWindowTextLength(h);
                    if (len == 0) return true;

                    var sb = new StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (string.IsNullOrWhiteSpace(title) || IsExcludedWindow(title))
                        return true;

                    RECT rect;
                    if (GetWindowRect(h, out rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;

                        if (width >= 100 && height >= 100)
                        {
                            windows.Add(new WindowInfo
                            {
                                Handle = h,
                                Title = title,
                                OriginalRect = rect
                            });
                        }
                    }
                }
                catch { }

                return true;
            }, IntPtr.Zero);

            return windows;
        }

        private bool IsExcludedWindow(string title)
        {
            var lower = title.ToLower();
            return lower.Contains("window scatter") ||
                   lower.Contains("program manager") ||
                   lower.Contains("microsoft text input") ||
                   lower.Contains("windows input") ||
                   lower.Contains("nvidia geforce") ||
                   title == "Default IME" ||
                   title == "MSCTFIME UI" ||
                   title == "GDI+ Window";
        }

        private bool HasWindowStateChanged(List<IntPtr> currentHandles)
        {
            return lastWindowHandles == null ||
                   lastWindowHandles.Count != currentHandles.Count ||
                   !lastWindowHandles.SequenceEqual(currentHandles);
        }

        private List<WindowLayout> ReuseCachedLayouts(List<WindowInfo> windows)
        {
            var layouts = new List<WindowLayout>(cachedLayouts);
            var windowsByHandle = windows.ToDictionary(w => w.Handle);

            foreach (var layout in layouts)
            {
                if (windowsByHandle.ContainsKey(layout.Window.Handle))
                {
                    layout.Window = windowsByHandle[layout.Window.Handle];
                }
            }

            return layouts;
        }

        #endregion

        private List<WindowLayout> CalculateNewLayouts(List<WindowInfo> windows, double screenW, double screenH)
        {
            var calculator = new WindowLayoutCalculator(rng);
            return calculator.CalculateLayouts(windows, screenW, screenH);
        }

        private void RegisterThumbnails(List<WindowLayout> layouts, IntPtr ourHwnd)
        {
            foreach (var layout in layouts)
            {
                var win = layout.Window;
                bool wasMinimized = IsIconic(win.Handle);

                IntPtr thumbHandle;
                int res = DwmRegisterThumbnail(ourHwnd, win.Handle, out thumbHandle);

                if (res != 0 || thumbHandle == IntPtr.Zero)
                    continue;

                RECT currentRect = win.OriginalRect;
                double startX = currentRect.Left;
                double startY = currentRect.Top;
                double startW = currentRect.Right - currentRect.Left;
                double startH = currentRect.Bottom - currentRect.Top;

                var clickBorder = new Border
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    Width = layout.Width,
                    Height = layout.Height,
                    Cursor = Cursors.Hand
                };

                Canvas.SetLeft(clickBorder, layout.X);
                Canvas.SetTop(clickBorder, layout.Y);

                var thumb = new WindowThumb
                {
                    WindowHandle = win.Handle,
                    ThumbnailHandle = thumbHandle,
                    WasMinimized = wasMinimized,
                    ClickBorder = clickBorder,

                    StartX = startX,
                    StartY = startY,
                    StartWidth = startW,
                    StartHeight = startH,

                    TargetX = layout.X,
                    TargetY = layout.Y,
                    TargetWidth = layout.Width,
                    TargetHeight = layout.Height,

                    CurrentX = startX,
                    CurrentY = startY,
                    CurrentWidth = startW,
                    CurrentHeight = startH
                };

                UpdateThumbnailPosition(thumb);

                clickBorder.MouseDown += (s, e) =>
                {
                    // FIXED: Ignore ALL clicks during any state transition
                    if (isScatterActive || isAnimatingBack || isTransitioning)
                    {
                        e.Handled = true;
                        return;
                    }

                    SwitchToWindow(thumb.WindowHandle);
                    e.Handled = true;
                };

                ScatterCanvas.Children.Add(clickBorder);
                windowThumbs.Add(thumb);
            }
        }


        #region Animation

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            double elapsed = (DateTime.Now - animStart).TotalSeconds;
            double progress = Math.Min(elapsed / ANIMATION_DURATION, 1.0);
            double eased = 1 - Math.Pow(1 - progress, 3);

            // Animate blur
            currentBlurRadius = Lerp(0, TARGET_BLUR_RADIUS, eased);
            if (BackgroundImage.Effect is BlurEffect blurEffect)
            {
                blurEffect.Radius = currentBlurRadius;
            }

            var now = DateTime.Now;
            bool canUpdateDwm = (lastDwmUpdate == DateTime.MinValue) ||
                                (now - lastDwmUpdate).TotalMilliseconds >= DWM_UPDATE_MIN_MS;

            foreach (var thumb in windowThumbs)
            {
                thumb.CurrentX = Lerp(thumb.StartX, thumb.TargetX, eased);
                thumb.CurrentY = Lerp(thumb.StartY, thumb.TargetY, eased);
                thumb.CurrentWidth = Lerp(thumb.StartWidth, thumb.TargetWidth, eased);
                thumb.CurrentHeight = Lerp(thumb.StartHeight, thumb.TargetHeight, eased);

                if (canUpdateDwm)
                {
                    UpdateThumbnailPosition(thumb);
                }
            }

            if (canUpdateDwm)
                lastDwmUpdate = now;

            if (progress >= 1.0)
            {
                CompositionTarget.Rendering -= CompositionTarget_Rendering;

                foreach (var thumb in windowThumbs)
                {
                    UpdateThumbnailPosition(thumb);
                }
            }
        }

        private void UpdateThumbnailPosition(WindowThumb thumb)
        {
            sharedProps.opacity = 255;
            sharedProps.rcDestination.Left = (int)Math.Round(thumb.CurrentX);
            sharedProps.rcDestination.Top = (int)Math.Round(thumb.CurrentY);
            sharedProps.rcDestination.Right = (int)Math.Round(thumb.CurrentX + thumb.CurrentWidth);
            sharedProps.rcDestination.Bottom = (int)Math.Round(thumb.CurrentY + thumb.CurrentHeight);

            DwmUpdateThumbnailProperties(thumb.ThumbnailHandle, ref sharedProps);
        }

        private static double Lerp(double start, double end, double t)
        {
            return start + (end - start) * t;
        }

        #endregion

        #region Cleanup & Window Switching

        private void Cleanup()
        {
            currentBlurRadius = 0;
            if (BackgroundImage.Effect is BlurEffect blurEffect)
            {
                blurEffect.Radius = 0;
            }

            CompositionTarget.Rendering -= CompositionTarget_Rendering;

            foreach (var thumb in windowThumbs)
            {
                try
                {
                    if (thumb.ThumbnailHandle != IntPtr.Zero)
                        DwmUnregisterThumbnail(thumb.ThumbnailHandle);
                }
                catch { }
            }

            ScatterCanvas.Children.Clear();
            windowThumbs.Clear();
        }

        private void SwitchToWindow(IntPtr windowHandle)
        {
            // FIXED: Use lock to prevent concurrent transitions
            lock (transitionLock)
            {
                if (isAnimatingBack || isScatterActive || isTransitioning)
                    return;

                isTransitioning = true;
            }

            try
            {
                // Wake the window
                ShowWindow(windowHandle, SW_RESTORE);
                SetForegroundWindow(windowHandle);

                var clickedThumb = windowThumbs.FirstOrDefault(t => t.WindowHandle == windowHandle);
                if (clickedThumb == null)
                {
                    lock (transitionLock) { isTransitioning = false; }
                    return;
                }

                // Bring its Border to the top of the canvas z-order
                ScatterCanvas.Children.Remove(clickedThumb.ClickBorder);
                ScatterCanvas.Children.Add(clickedThumb.ClickBorder);

                // Maintain order in the list
                windowThumbs.Remove(clickedThumb);
                windowThumbs.Add(clickedThumb);

                // Enforce Z-order
                SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                // Give Windows time to settle
                Task.Delay(10).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            // Recapture positions
                            foreach (var thumb in windowThumbs)
                            {
                                RECT rect;
                                if (GetWindowRect(thumb.WindowHandle, out rect))
                                {
                                    thumb.StartX = rect.Left;
                                    thumb.StartY = rect.Top;
                                    thumb.StartWidth = rect.Right - rect.Left;
                                    thumb.StartHeight = rect.Bottom - rect.Top;
                                }

                                // Re-register thumbnail
                                if (thumb.ThumbnailHandle != IntPtr.Zero)
                                    DwmUnregisterThumbnail(thumb.ThumbnailHandle);

                                IntPtr newThumb;
                                if (DwmRegisterThumbnail(new WindowInteropHelper(this).Handle,
                                    thumb.WindowHandle, out newThumb) == 0)
                                {
                                    thumb.ThumbnailHandle = newThumb;
                                    UpdateThumbnailPosition(thumb);
                                }
                            }

                            // Animate back
                            AnimateBackToOriginal(() =>
                            {
                                this.Visibility = Visibility.Hidden;

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    Cleanup();
                                    SetForegroundWindow(windowHandle);

                                    // FIXED: Clear transition flag AFTER cleanup
                                    lock (transitionLock) { isTransitioning = false; }
                                }), DispatcherPriority.Background);
                            });
                        }
                        catch
                        {
                            lock (transitionLock) { isTransitioning = false; }
                        }
                    });
                });
            }
            catch
            {
                lock (transitionLock) { isTransitioning = false; }
            }
        }


        private void CleanupAndClose()
        {
            // FIXED: Use lock for transition guard
            lock (transitionLock)
            {
                if (isAnimatingBack || isTransitioning)
                    return;

                isAnimatingBack = true;
                isTransitioning = true;
            }

            AnimateBackToOriginal(() =>
            {
                this.Visibility = Visibility.Hidden;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Cleanup();

                    // FIXED: Clear both flags
                    lock (transitionLock)
                    {
                        isAnimatingBack = false;
                        isTransitioning = false;
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        private void AnimateBackToOriginal(Action onComplete)
        {
            isReturningToOriginal = true;

            // Swap Start/Target so animation goes backwards
            foreach (var thumb in windowThumbs)
            {
                // Remember current position
                double tempX = thumb.StartX;
                double tempY = thumb.StartY;
                double tempW = thumb.StartWidth;
                double tempH = thumb.StartHeight;

                // Start from current scattered position
                thumb.StartX = thumb.TargetX;
                thumb.StartY = thumb.TargetY;
                thumb.StartWidth = thumb.TargetWidth;
                thumb.StartHeight = thumb.TargetHeight;

                // Target is original position
                thumb.TargetX = tempX;
                thumb.TargetY = tempY;
                thumb.TargetWidth = tempW;
                thumb.TargetHeight = tempH;

                // Reset current to start
                thumb.CurrentX = thumb.StartX;
                thumb.CurrentY = thumb.StartY;
                thumb.CurrentWidth = thumb.StartWidth;
                thumb.CurrentHeight = thumb.StartHeight;
            }

            animStart = DateTime.Now;
            lastDwmUpdate = DateTime.MinValue;

            // Store callback
            Action storedCallback = onComplete;

            // Rendering loop
            EventHandler renderHandler = null;
            renderHandler = (s, e) =>
            {
                double elapsed = (DateTime.Now - animStart).TotalSeconds;
                double progress = Math.Min(elapsed / ANIMATION_DURATION, 1.0);
                double eased = 1 - Math.Pow(1 - progress, 3);

                // Animate blur BACKWARDS (from blurred to clear)
                currentBlurRadius = Lerp(TARGET_BLUR_RADIUS, 0, eased);
                if (BackgroundImage.Effect is BlurEffect blurEffect)
                {
                    blurEffect.Radius = currentBlurRadius;
                }

                var now = DateTime.Now;
                bool canUpdateDwm = (lastDwmUpdate == DateTime.MinValue) ||
                                    (now - lastDwmUpdate).TotalMilliseconds >= DWM_UPDATE_MIN_MS;

                foreach (var thumb in windowThumbs)
                {
                    thumb.CurrentX = Lerp(thumb.StartX, thumb.TargetX, eased);
                    thumb.CurrentY = Lerp(thumb.StartY, thumb.TargetY, eased);
                    thumb.CurrentWidth = Lerp(thumb.StartWidth, thumb.TargetWidth, eased);
                    thumb.CurrentHeight = Lerp(thumb.StartHeight, thumb.TargetHeight, eased);

                    if (canUpdateDwm)
                    {
                        UpdateThumbnailPosition(thumb);
                    }
                }

                if (canUpdateDwm)
                    lastDwmUpdate = now;

                if (progress >= 1.0)
                {
                    CompositionTarget.Rendering -= renderHandler;
                    isReturningToOriginal = false;
                    storedCallback?.Invoke();
                }
            };

            CompositionTarget.Rendering += renderHandler;
        }
        #endregion
    }
}