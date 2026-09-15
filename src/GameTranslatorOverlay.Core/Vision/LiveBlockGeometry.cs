using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Suppresses small OCR box fluctuations without making the position deadband
/// grow with text size. All values are physical pixels, not WPF DIPs.
/// </summary>
public static class LiveBlockGeometry
{
    public static RectPx Stabilize(RectPx previous, RectPx observed)
    {
        if (previous.IsEmpty || observed.IsEmpty) return observed;

        var minWidth = Math.Min(previous.Width, observed.Width);
        var minHeight = Math.Min(previous.Height, observed.Height);
        return new RectPx(
            Math.Abs((long)previous.X - observed.X) <= 2 ? previous.X : observed.X,
            Math.Abs((long)previous.Y - observed.Y) <= 2 ? previous.Y : observed.Y,
            Math.Abs((long)previous.Width - observed.Width) <= Math.Max(12, minWidth * 0.15)
                ? previous.Width : observed.Width,
            Math.Abs((long)previous.Height - observed.Height) <= Math.Max(12, minHeight * 0.35)
                ? previous.Height : observed.Height);
    }
}