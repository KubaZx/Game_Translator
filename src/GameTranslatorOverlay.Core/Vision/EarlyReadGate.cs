namespace GameTranslatorOverlay.Core.Vision;

public sealed class EarlyReadGate(TimeSpan delay)
{
    private TimeSpan? _changeAt;

    public bool Enabled => delay > TimeSpan.Zero;

    public bool IsArmed => _changeAt is not null;

    public void Observe(bool frameChanged, TimeSpan now)
    {
        if (Enabled && frameChanged) _changeAt = now;
    }

    public bool IsDue(TimeSpan now) => _changeAt is { } at && now - at >= delay;

    public void Clear() => _changeAt = null;

    public TimeSpan GetPollingDelay(TimeSpan now, TimeSpan plannedDelay)
    {
        if (_changeAt is not { } at || plannedDelay <= TimeSpan.Zero) return plannedDelay;
        var untilDue = at + delay - now;
        return untilDue > TimeSpan.Zero && untilDue < plannedDelay ? untilDue : plannedDelay;
    }
}
