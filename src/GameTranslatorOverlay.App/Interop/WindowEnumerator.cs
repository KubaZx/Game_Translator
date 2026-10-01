using System.Diagnostics;
using System.Text;
using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.App.Interop;

public sealed record TargetWindow(IntPtr Handle, string Title, string ProcessName, int ProcessId)
{
    public string DisplayName => $"{Title}  ({ProcessName})";

    /// <summary>Powierzchnia okna w px² w chwili wyliczenia — gra ma zwykle największe okno procesu.</summary>
    public long Area { get; init; }

    public WindowCandidate ToCandidate() => new(Handle, Title, ProcessName, Area);
}

/// <summary>
/// Lista widocznych okien najwyższego poziomu — kandydatów do tłumaczenia.
/// Pomija okna systemowe (cloaked/UWP w tle), okna narzędziowe i własny proces.
/// </summary>
public static class WindowEnumerator
{
    public static IReadOnlyList<TargetWindow> GetOpenWindows()
    {
        var windows = new List<TargetWindow>();
        var ownProcessId = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            var titleLength = NativeMethods.GetWindowTextLength(hwnd);
            if (titleLength == 0) return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
            {
                return true;
            }

            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcessId || processId == 0) return true;

            var titleBuilder = new StringBuilder(titleLength + 1);
            NativeMethods.GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString();
            if (title.Length == 0) return true;

            string processName;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                return true;
            }

            windows.Add(new TargetWindow(hwnd, title, processName, (int)processId) { Area = GetArea(hwnd) });
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(static w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Okno pierwszoplanowe (najwyższego poziomu) — odczyt bez żadnej ingerencji w grę.</summary>
    public static IntPtr GetForegroundRootWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        return root != IntPtr.Zero ? root : foreground;
    }

    private static long GetArea(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return 0;
        var width = Math.Max(0L, (long)rect.Right - rect.Left);
        var height = Math.Max(0L, (long)rect.Bottom - rect.Top);
        return width * height;
    }
}
