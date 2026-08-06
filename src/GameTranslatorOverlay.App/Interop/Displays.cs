using System.Runtime.InteropServices;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.App.Interop;

/// <summary>Monitor w fizycznych pikselach + skala DPI (1.0 = 100%).</summary>
public sealed record MonitorArea(IntPtr Handle, RectPx Bounds, RectPx WorkArea, double Scale);

public static class Displays
{
    public static MonitorArea FromCursor()
    {
        NativeMethods.GetCursorPos(out var point);
        return FromPoint(point.X, point.Y);
    }

    public static MonitorArea FromPoint(int x, int y)
    {
        var handle = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = x, Y = y },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(handle);
    }

    public static MonitorArea FromRect(RectPx rect)
    {
        var native = new NativeMethods.RECT { Left = rect.X, Top = rect.Y, Right = rect.Right, Bottom = rect.Bottom };
        var handle = NativeMethods.MonitorFromRect(ref native, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(handle);
    }

    private static MonitorArea FromHandle(IntPtr handle)
    {
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(handle, ref info);

        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        return new MonitorArea(handle, ToRect(info.Monitor), ToRect(info.Work), scale);
    }

    private static RectPx ToRect(NativeMethods.RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
