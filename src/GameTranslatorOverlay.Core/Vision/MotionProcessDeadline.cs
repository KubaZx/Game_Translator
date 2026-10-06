namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Keeps the maximum wait for OCR across brief calm samples during camera motion.
/// This bounds scheduling deferral, not capture, OCR or translation duration.
/// </summary>
public sealed class MotionProcessDeadline
{
    private readonly TimeSpan _maximumPause;
    private TimeSpan? _waitingSince;
    private bool _isMoving;

    public MotionProcessDeadline(TimeSpan maximumPause)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPause, TimeSpan.Zero);
        _maximumPause = maximumPause;
    }

    public bool Observe(bool isMoving, TimeSpan elapsed, TimeSpan? limit = null)
    {
        _isMoving = isMoving;
        // A calm sample is not a completed read. Resetting here lets alternating
        // motion/calm samples defer OCR indefinitely through ForceDirty.
        if (isMoving) _waitingSince ??= elapsed;
        return _waitingSince is { } since && elapsed - since >= (limit ?? _maximumPause);
    }

    // Consume the wait only after frame preparation actually reaches processing.
    // During continuous motion, processing time counts toward the next deadline.
    public void OnProcessingStarted(TimeSpan elapsed) =>
        _waitingSince = _isMoving ? elapsed : null;

    public void Reset()
    {
        _waitingSince = null;
        _isMoving = false;
    }
}
