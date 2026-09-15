namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Schedules scene checks around OCR. A short operation that finishes between
/// the normal interval and the first periodic check needs one fresh check after
/// completion, avoiding a redundant capture while it is about to finish.
/// The caller supplies the positive interval for the supported 0.5–30 FPS range.
/// </summary>
public static class OcrSceneCheckTiming
{
    // Only the first periodic check waits longer. Once a check has run, the caller
    // resumes its normal interval so a slow OCR cannot defer scene checks forever.
    public static TimeSpan FirstCheckDelay(TimeSpan normalInterval) => normalInterval * 1.5;

    // An in-progress capture may contain pixels from before OCR completed. Its age
    // cannot replace the final fresh capture, even if that periodic check just ended.
    public static bool RequiresCompletionCheck(
        TimeSpan elapsedSinceOcrStart, TimeSpan normalInterval, bool performedPeriodicCheck) =>
        performedPeriodicCheck || elapsedSinceOcrStart >= normalInterval;
}