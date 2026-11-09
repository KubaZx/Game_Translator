namespace GameTranslatorOverlay.Core.Tests;

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
