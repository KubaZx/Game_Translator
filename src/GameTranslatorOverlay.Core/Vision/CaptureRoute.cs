namespace GameTranslatorOverlay.Core.Vision;

public enum PrintOutcome
{
    Failed,
    Uniform,
    Content,
}

public enum CaptureFrameChoice
{
    Printed,
    Skip,
    Fallback,
}

public enum CaptureSourceKind
{
    None,
    PrintWindow,
    GraphicsCapture,
    ScreenCapture,
}

public readonly record struct CaptureVerdict(
    CaptureFrameChoice Choice, bool EnteredNoPrintWindow = false, bool LeftNoPrintWindow = false);

public sealed class CaptureRoute
{
    public const int ConfirmationFrames = 3;
    public const int RecoveryFrames = 2;

    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ScreenReadHold = TimeSpan.FromSeconds(5);

    private int _failureSeries;
    private int _recoveryStreak;
    private TimeSpan _nextProbeAt;
    private TimeSpan? _lastScreenFrameAt;

    public bool PrintWindowProven { get; private set; }

    public bool NoPrintWindow { get; private set; }

    public bool ShouldTryPrintWindow(TimeSpan now) =>
        !NoPrintWindow || _recoveryStreak > 0 || now >= _nextProbeAt;

    public CaptureVerdict Observe(PrintOutcome outcome, bool gameVisible, TimeSpan now, Func<bool> screenIsUniform)
    {
        ArgumentNullException.ThrowIfNull(screenIsUniform);
        return NoPrintWindow ? ObserveProbe(outcome, now) : ObserveNormal(outcome, gameVisible, now, screenIsUniform);
    }

    public void NoteScreenFrame(TimeSpan now) => _lastScreenFrameAt = now;

    public bool ReadsScreen(TimeSpan now) =>
        NoPrintWindow && _lastScreenFrameAt is { } at && now >= at && now - at < ScreenReadHold;

    private CaptureVerdict ObserveProbe(PrintOutcome outcome, TimeSpan now)
    {
        if (outcome != PrintOutcome.Content)
        {
            _recoveryStreak = 0;
            _nextProbeAt = now + ProbeInterval;
            return new CaptureVerdict(CaptureFrameChoice.Fallback);
        }

        _recoveryStreak++;
        if (_recoveryStreak < RecoveryFrames) return new CaptureVerdict(CaptureFrameChoice.Printed);

        NoPrintWindow = false;
        PrintWindowProven = true;
        _recoveryStreak = 0;
        _failureSeries = 0;
        _lastScreenFrameAt = null;
        return new CaptureVerdict(CaptureFrameChoice.Printed, LeftNoPrintWindow: true);
    }

    private CaptureVerdict ObserveNormal(PrintOutcome outcome, bool gameVisible, TimeSpan now, Func<bool> screenIsUniform)
    {
        if (outcome == PrintOutcome.Content)
        {
            PrintWindowProven = true;
            _failureSeries = 0;
            return new CaptureVerdict(CaptureFrameChoice.Printed);
        }

        var keepPrinted = outcome == PrintOutcome.Uniform ? CaptureFrameChoice.Printed : CaptureFrameChoice.Skip;
        if (outcome == PrintOutcome.Uniform && PrintWindowProven)
        {
            _failureSeries = 0;
            return new CaptureVerdict(keepPrinted);
        }

        if (!gameVisible) return new CaptureVerdict(keepPrinted);

        if (screenIsUniform())
        {
            _failureSeries = 0;
            return new CaptureVerdict(keepPrinted);
        }

        _failureSeries++;
        if (_failureSeries < ConfirmationFrames) return new CaptureVerdict(keepPrinted);

        NoPrintWindow = true;
        _failureSeries = 0;
        _recoveryStreak = 0;
        _nextProbeAt = now + ProbeInterval;
        return new CaptureVerdict(CaptureFrameChoice.Fallback, EnteredNoPrintWindow: true);
    }
}
