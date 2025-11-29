using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Controls;
using Microsoft.Win32;
using System.IO;
using System.Linq;

namespace WindowScatter
{
    public partial class MainWindow : Window
    {
        #region Win32 + DWM P/Invoke

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr hThumb, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumb, out PSIZE size);

        // Keyboard hook for global hotkey
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);


        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint KEYEVENTF_KEYUP = 0x0002;

        private void SendSyntheticKeyUp(int vkCode)
        {
            keybd_event((byte)vkCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09;
        private const int VK_W = 0x57;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        // Constants
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const int SW_SHOWNOACTIVATE = 4;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fSourceClientAreaOnly;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PSIZE
        {
            public int x, y;
        }

        #endregion

        // DWM flags
        private const int DWM_TNP_RECTDESTINATION = 0x00000001;
        private const int DWM_TNP_OPACITY = 0x00000004;
        private const int DWM_TNP_VISIBLE = 0x00000008;

        // Animation parameters
        private const double ANIMATION_DURATION = 0.3;
        private const int MAX_WINDOWS = 20;
        private const double DWM_UPDATE_MIN_MS = 1.0;

        private List<WindowThumb> windowThumbs = new List<WindowThumb>();
        private DateTime animStart;
        private DateTime lastDwmUpdate = DateTime.MinValue;
        private Random rng = new Random();
        private DWM_THUMBNAIL_PROPERTIES sharedProps;

        // New fields for blur and start menu suppression
        private double currentBlurRadius = 0;
        private const double TARGET_BLUR_RADIUS = 40;
        private bool suppressStartMenu = false;
        private DateTime startMenuSuppressionStart;

        // Global hotkey stuff
        private IntPtr hookID = IntPtr.Zero;
        private LowLevelKeyboardProc hookCallback;
        private bool isWinKeyPressed = false;

        public MainWindow()
        {
            InitializeComponent();
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

            SetWallpaperBackground();

            this.Loaded += (s, e) =>
            {
                // Force window to fully initialize
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();

                // Now hide it
                this.Visibility = Visibility.Hidden;
            };

            // Setup global hotkey hook
            hookCallback = HookCallback;
            hookID = SetHook(hookCallback);
        }

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
                    // Track Win key state
                    if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                    {
                        isWinKeyPressed = true;
                    }
                    // Win+Tab pressed
                    else if (vkCode == VK_W && isWinKeyPressed)
                    {
                        // Don't clear isWinKeyPressed yet - we need to keep suppressing
                        // the Windows key to prevent Start Menu
                        suppressStartMenu = true;
                        startMenuSuppressionStart = DateTime.Now;

                        // Trigger on UI thread
                        this.Dispatcher.BeginInvoke(new Action(async () =>
                        {
                            if (this.Visibility == Visibility.Hidden)
                            {
                                this.Visibility = Visibility.Visible;
                                this.Activate();
                                this.Focus();

                                await System.Threading.Tasks.Task.Delay(50);
                                await StartScatterAsync();

                                // NOW release the Windows key after overlay is fully active
                                await System.Threading.Tasks.Task.Delay(100); // Additional safety delay
                                ReleaseWindowsKey();
                            }
                        }));

                        return (IntPtr)1; // Consume the key press
                    }
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    // Suppress Win key release for a period after triggering scatter
                    if ((vkCode == VK_LWIN || vkCode == VK_RWIN) && suppressStartMenu)
                    {
                        if ((DateTime.Now - startMenuSuppressionStart).TotalMilliseconds < 500) // Increased to 500ms
                        {
                            return (IntPtr)1; // Consume Win key release
                        }
                        else
                        {
                            // Time's up - stop suppressing and clear state
                            suppressStartMenu = false;
                            isWinKeyPressed = false;
                        }
                    }
                    else if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                    {
                        isWinKeyPressed = false;
                    }
                }
            }

            return CallNextHookEx(hookID, nCode, wParam, lParam);
        }

        private void ReleaseWindowsKey()
        {
            // Clear the state
            isWinKeyPressed = false;
            suppressStartMenu = false;

            // Send synthetic key ups for both Windows keys to be safe
            keybd_event((byte)VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event((byte)VK_RWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Unhook keyboard hook
            if (hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookID);
                hookID = IntPtr.Zero;
            }
        }

        private void SetWallpaperBackground()
        {
            try
            {
                // Get wallpaper path from registry
                string wallpaperPath = GetWallpaperPath();

                if (!string.IsNullOrEmpty(wallpaperPath) && File.Exists(wallpaperPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(wallpaperPath);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    // Get screen dimensions
                    double screenWidth = SystemParameters.PrimaryScreenWidth;
                    double screenHeight = SystemParameters.PrimaryScreenHeight;

                    // Calculate how Windows actually displays the wallpaper (Fill mode)
                    double imageWidth = bitmap.PixelWidth;
                    double imageHeight = bitmap.PixelHeight;

                    double scaleX = screenWidth / imageWidth;
                    double scaleY = screenHeight / imageHeight;
                    double scale = Math.Max(scaleX, scaleY); // Fill mode - cover entire screen

                    double scaledWidth = imageWidth * scale;
                    double scaledHeight = imageHeight * scale;

                    // Center the image (like Windows does)
                    double offsetX = (screenWidth - scaledWidth) / 2.0;
                    double offsetY = (screenHeight - scaledHeight) / 2.0;

                    // Set exact dimensions
                    BackgroundImage.Width = scaledWidth;
                    BackgroundImage.Height = scaledHeight;
                    BackgroundImage.Stretch = Stretch.Fill;
                    BackgroundImage.Source = bitmap;

                    // Position it precisely
                    Canvas.SetLeft(BackgroundImage, offsetX);
                    Canvas.SetTop(BackgroundImage, offsetY);

                    // Apply blur effect - START AT ZERO
                    BackgroundImage.Effect = new BlurEffect
                    {
                        Radius = 0,
                        KernelType = KernelType.Gaussian
                    };
                }
                else
                {
                    // Fallback to dark gray if wallpaper not found
                    this.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                }
            }
            catch
            {
                // Fallback
                this.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            }
        }

        private string GetWallpaperPath()
        {
            try
            {
                // Try current user wallpaper first
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    if (key != null)
                    {
                        string path = key.GetValue("WallPaper") as string;
                        if (!string.IsNullOrEmpty(path))
                            return path;
                    }
                }

                // Try TranscodedImageCache (Windows 10/11 wallpaper)
                string transcodedPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper"
                );

                if (File.Exists(transcodedPath))
                    return transcodedPath;

                // Try CachedFiles
                string cachedPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\CachedFiles"
                );

                if (Directory.Exists(cachedPath))
                {
                    var files = Directory.GetFiles(cachedPath);
                    if (files.Length > 0)
                        return files[0];
                }
            }
            catch { }

            return null;
        }

        #region Event handlers

        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                await StartScatterAsync();
            }
            else if (e.Key == Key.Escape)
            {
                CleanupAndClose();
            }
        }

        #endregion

        #region Core logic

        private async System.Threading.Tasks.Task StartScatterAsync()
        {
            // Ensure our window handle is valid before doing DWM shit
            var helper = new WindowInteropHelper(this);
            IntPtr ourHwnd = helper.EnsureHandle();

            if (ourHwnd == IntPtr.Zero)
            {
                MessageBox.Show("Window handle fucked!", "Error");
                return;
            }

            Cleanup();

            // Enumerate windows
            var windows = new List<WindowInfo>();
            EnumWindows((h, l) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    if (IsIconic(h)) return true;

                    int len = GetWindowTextLength(h);
                    if (len == 0) return true;

                    var sb = new StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    var lower = title.ToLower();
                    if (lower.Contains("window scatter")) return true;
                    if (lower.Contains("program manager")) return true;
                    if (lower.Contains("microsoft text input")) return true;
                    if (lower.Contains("windows input")) return true;
                    if (lower.Contains("nvidia geforce")) return true;
                    if (title == "Default IME" || title == "MSCTFIME UI") return true;
                    if (title == "GDI+ Window") return true;

                    RECT rect;
                    if (GetWindowRect(h, out rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        if (width < 100 || height < 100) return true;

                        windows.Add(new WindowInfo
                        {
                            Handle = h,
                            Title = title,
                            OriginalRect = rect
                        });
                    }
                }
                catch { }

                return true;
            }, IntPtr.Zero);

            if (windows.Count == 0)
            {
                MessageBox.Show("No windows found!", "Info");
                return;
            }

            if (windows.Count > MAX_WINDOWS)
                windows = windows.GetRange(0, MAX_WINDOWS);

            double screenW = SystemParameters.PrimaryScreenWidth;
            double screenH = SystemParameters.PrimaryScreenHeight;

            // MACOS-STYLE ORGANIC FLOW LAYOUT
            double padding = 50;
            double minGap = 20;
            double availableW = screenW - (2 * padding);
            double availableH = screenH - (2 * padding);

            // Calculate total area and scale factor
            double totalOriginalArea = 0;
            foreach (var win in windows)
            {
                var r = win.OriginalRect;
                totalOriginalArea += (r.Right - r.Left) * (r.Bottom - r.Top);
            }

            // Scale windows to fit available space (with breathing room)
            double targetTotalArea = availableW * availableH * 0.65; // Use 65% of available space
            double scaleFactor = Math.Sqrt(targetTotalArea / totalOriginalArea);
            scaleFactor = Math.Max(0.15, Math.Min(0.5, scaleFactor)); // Clamp between 15% and 50%

            // Create layouts with scaled sizes
            var layouts = new List<WindowLayout>();
            foreach (var win in windows)
            {
                var r = win.OriginalRect;
                double origW = r.Right - r.Left;
                double origH = r.Bottom - r.Top;

                // Apply base scale
                double w = origW * scaleFactor;
                double h = origH * scaleFactor;

                // Add size variance (±20%) for organic feel
                double sizeVariance = 0.85 + (rng.NextDouble() * 0.3);
                w *= sizeVariance;
                h *= sizeVariance;

                // Clamp to reasonable sizes
                w = Math.Max(180, Math.Min(600, w));
                h = Math.Max(120, Math.Min(450, h));

                layouts.Add(new WindowLayout
                {
                    Window = win,
                    Width = w,
                    Height = h,
                    OrigWidth = origW,
                    OrigHeight = origH,
                    X = 0,
                    Y = 0
                });
            }

            // ORGANIC PACKING ALGORITHM - like macOS but with NO OVERLAP
            // PROPER AREA-FILLING LAYOUT (macOS style - fill corners and edges)
            const double windowGap = 10;

            // Sort by area (biggest first) for better packing
            layouts = layouts.OrderByDescending(l => l.Width * l.Height).ToList();

            // Define regions with priority (corners → edges → center)
            var placedWindows = new List<Rect>();

            // SPECIAL CASE: 2 windows - place them side by side efficiently
            if (layouts.Count == 2)
            {
                double totalWidth = layouts[0].Width + layouts[1].Width + 40; // 40px gap
                double maxHeight = Math.Max(layouts[0].Height, layouts[1].Height);

                double startX = (screenW - totalWidth) / 2;
                double startY = (screenH - maxHeight) / 2;

                // First window on left
                layouts[0].X = startX;
                layouts[0].Y = startY + (maxHeight - layouts[0].Height) / 2; // Vertically center
                placedWindows.Add(new Rect(layouts[0].X, layouts[0].Y, layouts[0].Width, layouts[0].Height));

                // Second window on right
                layouts[1].X = startX + layouts[0].Width + 40;
                layouts[1].Y = startY + (maxHeight - layouts[1].Height) / 2; // Vertically center
                placedWindows.Add(new Rect(layouts[1].X, layouts[1].Y, layouts[1].Width, layouts[1].Height));
            }
            else
            {
                // Normal placement for 3+ windows

                foreach (var layout in layouts)
                {
                    Rect bestRect = new Rect(-9999, -9999, layout.Width, layout.Height); // INVALID position
                    double bestScore = double.MaxValue;
                    bool foundValidPosition = false;

                    int maxResizeAttempts = 5; // Try shrinking up to 5 times
                    double originalWidth = layout.Width;
                    double originalHeight = layout.Height;

                    // SPECIAL CASE: First window always goes near center (slightly RIGHT-biased for balance)
                    if (placedWindows.Count == 0)
                    {
                        double centerX = (screenW - layout.Width) / 2;
                        double centerY = (screenH - layout.Height) / 2;

                        // Add slight randomness AND a rightward push (to pre-compensate for left bias)
                        double randomOffsetX = (rng.NextDouble() - 0.3) * 150; // Shifted from -0.5 to -0.3 (pushes right)
                        double randomOffsetY = (rng.NextDouble() - 0.5) * 100; // Reduced vertical variance

                        centerX += randomOffsetX - 80; // Extra 80px push to the left
                        centerY += randomOffsetY;

                        // Make sure it's still on screen
                        centerX = Math.Max(padding, Math.Min(screenW - padding - layout.Width, centerX));
                        centerY = Math.Max(padding, Math.Min(screenH - padding - layout.Height, centerY));

                        bestRect = new Rect(centerX, centerY, layout.Width, layout.Height);
                        foundValidPosition = true;

                        layout.X = bestRect.X;
                        layout.Y = bestRect.Y;
                        placedWindows.Add(bestRect);
                        continue; // Skip the normal placement logic
                    }

                    for (int resizeAttempt = 0; resizeAttempt < maxResizeAttempts && !foundValidPosition; resizeAttempt++)
                    {
                        // Shrink by 10% each attempt (but keep aspect ratio)
                        if (resizeAttempt > 0)
                        {
                            double shrinkFactor = 1.0 - (resizeAttempt * 0.1);
                            layout.Width = originalWidth * shrinkFactor;
                            layout.Height = originalHeight * shrinkFactor;

                            // Don't go below minimum sizes
                            layout.Width = Math.Max(120, layout.Width);
                            layout.Height = Math.Max(80, layout.Height);
                        }

                        // Try MANY positions across the entire screen
                        int gridSteps = 50; // More = better placement but slower
                        double stepX = Math.Max(1, (availableW - layout.Width) / gridSteps);
                        double stepY = Math.Max(1, (availableH - layout.Height) / gridSteps);

                        for (int ix = 0; ix <= gridSteps; ix++)
                        {
                            for (int iy = 0; iy <= gridSteps; iy++)
                            {
                                double x = padding + (ix * stepX);
                                double y = padding + (iy * stepY);

                                // Make sure we're not out of bounds
                                if (x + layout.Width > screenW - padding) continue;
                                if (y + layout.Height > screenH - padding) continue;

                                var candidateRect = new Rect(x, y, layout.Width, layout.Height);

                                // Check for overlaps
                                bool hasOverlap = false;

                                foreach (var placed in placedWindows)
                                {
                                    // Use a BIGGER buffer for collision detection
                                    var expandedPlaced = new Rect(
                                        placed.X - 15,
                                        placed.Y - 15,
                                        placed.Width + 30,
                                        placed.Height + 30
                                    );

                                    // CRITICAL: Check intersection with BOTH rects
                                    var intersection = Rect.Intersect(candidateRect, expandedPlaced);

                                    // Even a 1-pixel overlap is FORBIDDEN
                                    if (!intersection.IsEmpty && intersection.Width > 0.1 && intersection.Height > 0.1)
                                    {
                                        hasOverlap = true;
                                        break; // Immediately reject this position
                                    }
                                }

                                if (hasOverlap) continue; // Skip overlapping positions entirely

                                // Score this position (lower = better)
                                double score = 0;

                                // PRIORITY 1: Balance left/right distribution BY WINDOW COUNT (not area)
                                if (placedWindows.Count > 0)
                                {
                                    double screenCenterX = screenW / 2;
                                    double screenCenterY = screenH / 2;
                                    double currentX = x + layout.Width / 2;
                                    double currentY = y + layout.Height / 2;

                                    // Count windows on each side (not area-based)
                                    int leftCount = placedWindows.Count(p => (p.X + p.Width / 2) < screenCenterX);
                                    int rightCount = placedWindows.Count(p => (p.X + p.Width / 2) >= screenCenterX);
                                    int topCount = placedWindows.Count(p => (p.Y + p.Height / 2) < screenCenterY);
                                    int bottomCount = placedWindows.Count(p => (p.Y + p.Height / 2) >= screenCenterY);

                                    // Horizontal balance - MASSIVE bonus for fixing imbalance
                                    if (leftCount > rightCount && currentX >= screenCenterX)
                                    {
                                        // Left is crowded, current position is on right
                                        int imbalance = leftCount - rightCount;
                                        score -= 600 * imbalance; // More imbalanced = stronger pull
                                    }
                                    else if (rightCount > leftCount && currentX < screenCenterX)
                                    {
                                        // Right is crowded, current position is on left
                                        int imbalance = rightCount - leftCount;
                                        score -= 600 * imbalance;
                                    }

                                    // Vertical balance
                                    if (topCount > bottomCount && currentY >= screenCenterY)
                                    {
                                        int imbalance = topCount - bottomCount;
                                        score -= 400 * imbalance;
                                    }
                                    else if (bottomCount > topCount && currentY < screenCenterY)
                                    {
                                        int imbalance = bottomCount - topCount;
                                        score -= 400 * imbalance;
                                    }

                                    // Quadrant balancing - try to spread across all 4 quadrants
                                    int topLeftQuad = placedWindows.Count(p => p.X + p.Width / 2 < screenCenterX && p.Y + p.Height / 2 < screenCenterY);
                                    int topRightQuad = placedWindows.Count(p => p.X + p.Width / 2 >= screenCenterX && p.Y + p.Height / 2 < screenCenterY);
                                    int bottomLeftQuad = placedWindows.Count(p => p.X + p.Width / 2 < screenCenterX && p.Y + p.Height / 2 >= screenCenterY);
                                    int bottomRightQuad = placedWindows.Count(p => p.X + p.Width / 2 >= screenCenterX && p.Y + p.Height / 2 >= screenCenterY);

                                    // Find the least populated quadrant and give bonus if we're going there
                                    int minQuadCount = Math.Min(Math.Min(topLeftQuad, topRightQuad), Math.Min(bottomLeftQuad, bottomRightQuad));

                                    // Which quadrant is this position in?
                                    bool isTopLeft = currentX < screenCenterX && currentY < screenCenterY;
                                    bool isTopRight = currentX >= screenCenterX && currentY < screenCenterY;
                                    bool isBottomLeft = currentX < screenCenterX && currentY >= screenCenterY;
                                    bool isBottomRight = currentX >= screenCenterX && currentY >= screenCenterY;

                                    if ((isTopLeft && topLeftQuad == minQuadCount) ||
                                        (isTopRight && topRightQuad == minQuadCount) ||
                                        (isBottomLeft && bottomLeftQuad == minQuadCount) ||
                                        (isBottomRight && bottomRightQuad == minQuadCount))
                                    {
                                        score -= 1000; // Bonus for filling empty quadrant
                                    }

                                    // PRIORITY 2: Corner proximity (second priority)
                                    double distToTopLeft = Math.Sqrt(Math.Pow(x - padding, 2) + Math.Pow(y - padding, 2));
                                    double distToTopRight = Math.Sqrt(Math.Pow(x + layout.Width - (screenW - padding), 2) + Math.Pow(y - padding, 2));
                                    double distToBottomLeft = Math.Sqrt(Math.Pow(x - padding, 2) + Math.Pow(y + layout.Height - (screenH - padding), 2));
                                    double distToBottomRight = Math.Sqrt(Math.Pow(x + layout.Width - (screenW - padding), 2) + Math.Pow(y + layout.Height - (screenH - padding), 2));

                                    double nearestCorner = Math.Min(Math.Min(distToTopLeft, distToTopRight), Math.Min(distToBottomLeft, distToBottomRight));
                                    score += nearestCorner * 0.5;

                                    // PRIORITY 3: Edge proximity (third priority)
                                    double distToLeftEdge = Math.Abs(x - padding);
                                    double distToRightEdge = Math.Abs(x + layout.Width - (screenW - padding));
                                    double distToTopEdge = Math.Abs(y - padding);
                                    double distToBottomEdge = Math.Abs(y + layout.Height - (screenH - padding));

                                    double nearestEdge = Math.Min(Math.Min(distToLeftEdge, distToRightEdge), Math.Min(distToTopEdge, distToBottomEdge));
                                    score += nearestEdge * 0.05;

                                    // PRIORITY 4: Center bias (PENALIZE center heavily)
                                    double distFromCenter = Math.Sqrt(
                                        Math.Pow(currentX - screenCenterX, 2) +
                                        Math.Pow(currentY - screenCenterY, 2)
                                    );
                                    score += distFromCenter * 0.20; // Increased from 0.08 - push away from center harder

                                    // PRIORITY 5: Don't cluster too tight
                                    double minDistToPlaced = double.MaxValue;
                                    foreach (var placed in placedWindows)
                                    {
                                        double dx = Math.Max(0, Math.Max(placed.X - (x + layout.Width), x - (placed.X + placed.Width)));
                                        double dy = Math.Max(0, Math.Max(placed.Y - (y + layout.Height), y - (placed.Y + placed.Height)));
                                        double dist = Math.Sqrt(dx * dx + dy * dy);
                                        minDistToPlaced = Math.Min(minDistToPlaced, dist);
                                    }
                                    score += minDistToPlaced * 0.05;

                                    // Slight randomness for organic feel
                                    score += rng.NextDouble() * 3;
                                }

                                // Pick best valid position
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    bestRect = candidateRect;
                                    foundValidPosition = true;
                                }
                            }
                        }
                    }

                    // If we STILL couldn't place it after shrinking, overlap it slightly in the center as last resort
                    if (!foundValidPosition)
                    {
                        double centerX = (screenW - layout.Width) / 2;
                        double centerY = (screenH - layout.Height) / 2;
                        bestRect = new Rect(centerX, centerY, layout.Width, layout.Height);
                    }

                    layout.X = bestRect.X;
                    layout.Y = bestRect.Y;
                    placedWindows.Add(bestRect);
                }
            } // Close the else block for 3+ windows

            // Make sure our overlay is topmost
            SetWindowPos(ourHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            // Register DWM thumbnails
            foreach (var layout in layouts)
            {
                var win = layout.Window;
                bool wasMinimized = IsIconic(win.Handle);

                IntPtr thumbHandle;
                int res = DwmRegisterThumbnail(ourHwnd, win.Handle, out thumbHandle);

                if (res != 0 || thumbHandle == IntPtr.Zero)
                {
                    continue;
                }

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
                    SwitchToWindow(thumb.WindowHandle);
                    e.Handled = true;
                };

                ScatterCanvas.Children.Add(clickBorder);
                windowThumbs.Add(thumb);


            }

            if (windowThumbs.Count == 0)
            {
                MessageBox.Show("No windows captured!", "Info");
                return;
            }

            animStart = DateTime.Now;
            lastDwmUpdate = DateTime.MinValue;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            double elapsed = (DateTime.Now - animStart).TotalSeconds;
            double progress = Math.Min(elapsed / ANIMATION_DURATION, 1.0);
            double eased = 1 - Math.Pow(1 - progress, 3);

            // Animate blur radius
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

        private void RestoreWindows()
        {
            // Nothing to restore - windows never moved!
        }

        private void Cleanup()
        {
            // Reset blur
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
            // Just focus the window - it's already in the right position!
            ShowWindow(windowHandle, SW_RESTORE);
            SetForegroundWindow(windowHandle);

            Cleanup();

            // Hide our overlay - windows are already where they belong
            this.Visibility = Visibility.Hidden;
        }

        private void CleanupAndClose()
        {
            RestoreWindows();
            Cleanup();

            // Hide instead of closing so hotkey keeps working
            this.Visibility = Visibility.Hidden;
        }

        #endregion

        #region Helpers

        private static double Lerp(double start, double end, double t)
        {
            return start + (end - start) * t;
        }

        #endregion

        #region Data classes

        private class WindowInfo
        {
            public IntPtr Handle;
            public string Title;
            public RECT OriginalRect;
        }

        private class WindowThumb
        {
            public IntPtr WindowHandle;
            public IntPtr ThumbnailHandle;
            public bool WasMinimized;
            public Border ClickBorder;

            public double StartX, StartY, StartWidth, StartHeight;
            public double TargetX, TargetY, TargetWidth, TargetHeight;
            public double CurrentX, CurrentY, CurrentWidth, CurrentHeight;
        }

        private class WindowLayout
        {
            public WindowInfo Window;
            public double Width;
            public double Height;
            public double X;
            public double Y;
            public double OrigWidth;
            public double OrigHeight;
        }

        #endregion
    }
}