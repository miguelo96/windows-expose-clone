using System;
using System.Windows.Controls;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public RECT OriginalRect;
    }

    internal class WindowThumb
    {
        public IntPtr WindowHandle;
        public IntPtr ThumbnailHandle;
        public bool WasMinimized;
        public Border ClickBorder;

        public double StartX, StartY, StartWidth, StartHeight;
        public double TargetX, TargetY, TargetWidth, TargetHeight;
        public double CurrentX, CurrentY, CurrentWidth, CurrentHeight;
    }

    internal class WindowLayout
    {
        public WindowInfo Window;
        public double Width;
        public double Height;
        public double X;
        public double Y;
        public double OrigWidth;
        public double OrigHeight;
    }
}