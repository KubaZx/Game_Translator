namespace GameTranslatorOverlay.Core.Vision;

public readonly record struct FailureStep(bool Stop, TimeSpan Delay);

public sealed class LiveFailureBackoff
{
    public static readonly TimeSpan StopAfter = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    private TimeSpan _firstFailureAt;

    public int Failures { get; private set; }

    public TimeSpan FailingFor(TimeSpan now) => Failures == 0 ? TimeSpan.Zero : now - _firstFailureAt;

    public FailureStep RecordFailure(TimeSpan now)
    {
        if (Failures == 0) _firstFailureAt = now;
        Failures++;
        var delay = Delays[Math.Min(Failures - 1, Delays.Length - 1)];
        return new FailureStep(Failures > 1 && now - _firstFailureAt >= StopAfter, delay);
    }

    public void RecordSuccess() => Failures = 0;
}
