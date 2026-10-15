using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

internal static class Monitors
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static (int Left, int Top, int Right, int Bottom) WorkArea(Window window)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? (info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom)
            : (0, 0, 0, 0);
    }
}
