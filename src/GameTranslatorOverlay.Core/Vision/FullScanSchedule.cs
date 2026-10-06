namespace GameTranslatorOverlay.Core.Vision;

public sealed class FullScanSchedule(TimeSpan interval)
{
    private TimeSpan _lastFullScanAt;

    public bool IsDue(TimeSpan now) => interval > TimeSpan.Zero && now - _lastFullScanAt >= interval;

    public void OnScanStarted(TimeSpan now, bool fullFrame)
    {
        if (fullFrame) _lastFullScanAt = now;
    }
}
