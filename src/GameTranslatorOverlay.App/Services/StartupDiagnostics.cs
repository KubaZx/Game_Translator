using System.Reflection;
using System.Runtime.InteropServices;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Infrastructure.Diagnostics;
using Windows.Media.Ocr;

namespace GameTranslatorOverlay.App.Services;

public static class StartupDiagnostics
{
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    public static string Describe()
    {
        var assembly = typeof(StartupDiagnostics).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "?";
        var metadata = informational.IndexOf('+');
        var version = metadata > 0 ? informational[..metadata] : informational;
        return StartupReport.Format(
            version, Environment.OSVersion.Version, RuntimeInformation.FrameworkDescription, ReadMonitors(), ReadOcrLanguages());
    }

    private static List<MonitorSummary> ReadMonitors()
    {
        var monitors = new List<MonitorSummary>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
            {
                var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(handle, ref info))
                {
                    var scale = NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
                        ? dpiX / 96.0
                        : 1.0;
                    monitors.Add(new MonitorSummary(info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, scale));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            monitors.Clear();
        }
        return monitors;
    }

    private static List<string> ReadOcrLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.Select(static l => l.LanguageTag).ToList();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}
