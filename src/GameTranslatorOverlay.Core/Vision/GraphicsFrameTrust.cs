namespace GameTranslatorOverlay.Core.Vision;

public enum GraphicsFrameVerdict
{
    Use,
    Reject,
    Abandon,
}

public sealed class GraphicsFrameTrust
{
    public const int ConfirmationFrames = 3;

    private int _doubtSeries;

    public bool Proven { get; private set; }

    public GraphicsFrameVerdict Observe(bool uniform, bool printWindowWorks, bool gameVisible, Func<bool> screenIsUniform)
    {
        ArgumentNullException.ThrowIfNull(screenIsUniform);
        if (!uniform)
        {
            Proven = true;
            _doubtSeries = 0;
            return GraphicsFrameVerdict.Use;
        }

        if (Proven) return GraphicsFrameVerdict.Use;
        if (printWindowWorks) return GraphicsFrameVerdict.Reject;
        if (!gameVisible) return GraphicsFrameVerdict.Use;

        if (screenIsUniform())
        {
            _doubtSeries = 0;
            return GraphicsFrameVerdict.Use;
        }

        _doubtSeries++;
        if (_doubtSeries < ConfirmationFrames) return GraphicsFrameVerdict.Use;
        _doubtSeries = 0;
        return GraphicsFrameVerdict.Abandon;
    }
}
