using System.Diagnostics;
using System.Text;

namespace GameTranslatorOverlay.App.Interop;

public sealed record TargetWindow(IntPtr Handle, string Title, string ProcessName, int ProcessId)
{
    public string DisplayName => $"{Title}  ({ProcessName})";
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

            windows.Add(new TargetWindow(hwnd, title, processName, (int)processId));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(static w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
