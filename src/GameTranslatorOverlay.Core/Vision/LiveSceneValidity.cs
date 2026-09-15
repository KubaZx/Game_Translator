namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Tracks whether a pending live result still belongs to the observed scene.
/// Uses the existing scene-cut and two-sample motion rules; ordinary animation
/// and a single motion sample do not invalidate a translation.
/// </summary>
public sealed class LiveSceneValidity(double sceneCutThreshold, double motionThreshold)
{
    public long Generation { get; private set; }
    public int MotionSamples { get; private set; }

    public bool Observe(NoiseAwareAnalysis analysis, bool hasPreviousFrame)
    {
        // The synthetic initial analysis describes a full frame, not a scene cut.
        if (!hasPreviousFrame)
        {
            MotionSamples = 0;
            return false;
        }

        MotionSamples = analysis.StrongChangedFraction >= motionThreshold
            ? MotionSamples + 1 : 0;
        if (analysis.ChangedFraction < sceneCutThreshold && MotionSamples < 2)
            return false;

        // Once motion is confirmed, every later moving sample invalidates work
        // started during that same panorama, including a deadline-forced OCR.
        Generation++;
        return true;
    }

    public bool IsCurrent(long generation) => generation == Generation;

    // A lost/minimized window also invalidates results already in flight.
    public void Reset()
    {
        Generation++;
        MotionSamples = 0;
    }
}
