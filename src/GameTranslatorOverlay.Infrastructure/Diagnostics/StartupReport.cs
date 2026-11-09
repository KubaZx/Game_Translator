using System.Globalization;

namespace GameTranslatorOverlay.Infrastructure.Diagnostics;

public sealed record MonitorSummary(int Width, int Height, double Scale);

public static class StartupReport
{
    public static string Format(
        string appVersion,
        Version windowsVersion,
        string runtimeDescription,
        IReadOnlyList<MonitorSummary> monitors,
        IReadOnlyList<string> ocrLanguages)
    {
        var monitorText = monitors.Count == 0
            ? "brak danych"
            : string.Join("; ", monitors.Select(static m =>
                string.Create(CultureInfo.InvariantCulture, $"{m.Width}x{m.Height} @ {m.Scale * 100:0}%")));
        var languageText = ocrLanguages.Count == 0 ? "brak" : string.Join(", ", ocrLanguages);
        return $"Start programu: wersja {appVersion}; Windows {windowsVersion.Major}.{windowsVersion.Minor} (kompilacja {windowsVersion.Build}); " +
               $"{runtimeDescription}; monitory: {monitors.Count} ({monitorText}); języki OCR: {languageText}";
    }
}

public sealed class ErrorDialogThrottle(TimeSpan? interval = null)
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(60);
    private DateTime? _lastShown;

    public bool TryAcquire(DateTime now, bool liveRunning)
    {
        if (liveRunning) return false;
        if (_lastShown is { } last && now - last < _interval) return false;
        _lastShown = now;
        return true;
    }
}
