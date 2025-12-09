using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace WindowScatter
{
    internal class HotCornerManager
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private readonly DispatcherTimer timer;
        private readonly Action onHotCornerTriggered;
        private readonly AppSettings settings;
        private DateTime? cornerEnteredTime = null;
        private bool isInCorner = false;
        private const int CORNER_THRESHOLD = 5; // pixels from corner
        private bool isOnCooldown = false;

        public HotCornerManager(AppSettings settings, Action onTriggered)
        {
            this.settings = settings;
            this.onHotCornerTriggered = onTriggered;

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50); // Check every 50ms
            timer.Tick += Timer_Tick;
        }

        public void Start()
        {
            if (settings.EnableHotCorners)
            {
                timer.Start();
            }
        }

        public void Stop()
        {
            timer.Stop();
            cornerEnteredTime = null;
            isInCorner = false;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!settings.EnableHotCorners)
                return;

            POINT cursorPos;
            if (!GetCursorPos(out cursorPos))
                return;

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            bool inCornerNow = IsInHotCorner(cursorPos, screenWidth, screenHeight);

            if (inCornerNow && !isInCorner)
            {
                // Just entered corner
                isInCorner = true;
                cornerEnteredTime = DateTime.Now;
            }
            else if (inCornerNow && isInCorner)
            {
                // Still in corner - check if delay elapsed
                if (cornerEnteredTime.HasValue)
                {
                    var elapsed = (DateTime.Now - cornerEnteredTime.Value).TotalMilliseconds;
                    if (elapsed >= settings.HotCornerDelay)
                    {
                        // Don't trigger if already on cooldown
                        if (isOnCooldown)
                            return;

                        // TRIGGER IT
                        isOnCooldown = true;
                        cornerEnteredTime = null;
                        isInCorner = false;
                        onHotCornerTriggered?.Invoke();

                        // Reset cooldown after 2 seconds
                        var cooldownTimer = new DispatcherTimer();
                        cooldownTimer.Interval = TimeSpan.FromSeconds(2);
                        cooldownTimer.Tick += (s, args) =>
                        {
                            isOnCooldown = false;
                            cooldownTimer.Stop();
                        };
                        cooldownTimer.Start();
                    }
                }
            }
            else if (!inCornerNow && isInCorner)
            {
                // Left corner before delay
                isInCorner = false;
                cornerEnteredTime = null;
            }
        }

        private bool IsInHotCorner(POINT cursor, double screenWidth, double screenHeight)
        {
            switch (settings.HotCornerPosition.ToLower())
            {
                case "topleft":
                    return cursor.X <= CORNER_THRESHOLD && cursor.Y <= CORNER_THRESHOLD;

                case "topright":
                    return cursor.X >= screenWidth - CORNER_THRESHOLD && cursor.Y <= CORNER_THRESHOLD;

                case "bottomleft":
                    return cursor.X <= CORNER_THRESHOLD && cursor.Y >= screenHeight - CORNER_THRESHOLD;

                case "bottomright":
                    return cursor.X >= screenWidth - CORNER_THRESHOLD && cursor.Y >= screenHeight - CORNER_THRESHOLD;

                default:
                    return false;
            }
        }
    }
}