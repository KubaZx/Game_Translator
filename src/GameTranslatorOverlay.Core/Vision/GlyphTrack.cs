using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

public readonly record struct GlyphTrackMap(double OriginX, double OriginY, double Scale)
{
    public static GlyphTrackMap Identity { get; } = new(0, 0, 1);
}

public sealed record GlyphTrack(
    double OriginX,
    double OriginY,
    double Step,
    int Width,
    int Height,
    bool[] Mask,
    RectPx Region,
    bool Soft,
    int Feather,
    int SkipLeft,
    int SkipRight,
    int[] Points,
    float[] Values,
    float Contrast,
    int[] GlyphPoints,
    int[] PartnerPoints,
    float TemplateContrast)
{
    public RectPx WindowBounds => new(
        (int)Math.Floor(OriginX),
        (int)Math.Floor(OriginY),
        (int)Math.Ceiling(Width * Step) + 1,
        (int)Math.Ceiling(Height * Step) + 1);

    public GlyphTrack Shifted(int workDx, int workDy) =>
        workDx == 0 && workDy == 0 ? this : this with { OriginX = OriginX + workDx * Step, OriginY = OriginY + workDy * Step };

    public RectPx SearchArea(double maxShift) => WindowBounds.Inflate((int)Math.Ceiling(maxShift + Step) + 1);
}

public readonly record struct GlyphMatch(
    int WorkDx, int WorkDy, double WindowDx, double WindowDy, double Cost, double StaticCost, double Typical, float Contrast,
    double? LocalContrast = null, float TemplateContrast = 0)
{
    public bool IsStatic => WorkDx == 0 && WorkDy == 0;

    public double AcceptLimit => Math.Max(8.0, Contrast * 0.14);

    public bool KeepsContrast => LocalContrast is not { } local || Math.Abs(TemplateContrast) < 12
        || (Math.Sign(local) == Math.Sign(TemplateContrast) && Math.Abs(local) >= Math.Abs(TemplateContrast) * 0.5);

    public bool IsConfident => Cost <= AcceptLimit && (Typical <= 0 || Cost <= Typical * 0.55) && KeepsContrast;
}

public static class GlyphTracker
{
    public const int CoarsePoints = 256;

    public static GlyphMatch? Locate(GlyphTrack track, OcrBitmap region, int regionX, int regionY, double maxShift)
    {
        if (track.Points.Length < 8 || region.Width <= 0 || region.Height <= 0) return null;
        var lum = Luminance(region);
        var radius = Math.Max(0, (int)Math.Ceiling(maxShift / track.Step));
        var full = Cost(track, lum, region.Width, region.Height, regionX, regionY, 0, 0, 1);
        if (full <= Math.Max(6.0, track.Contrast * 0.15) || radius == 0)
            return new GlyphMatch(0, 0, 0, 0, full, full, 0, track.Contrast,
                LocalContrast(track, lum, region.Width, region.Height, regionX, regionY, 0, 0), track.TemplateContrast);

        var stride = Math.Max(1, track.Points.Length / CoarsePoints);
        var step = radius >= 6 ? 2 : 1;
        var bestX = 0;
        var bestY = 0;
        var best = Cost(track, lum, region.Width, region.Height, regionX, regionY, 0, 0, stride);
        var samples = new List<double>(((2 * radius / step) + 1) * ((2 * radius / step) + 1));
        for (var sy = -radius; sy <= radius; sy += step)
        {
            for (var sx = -radius; sx <= radius; sx += step)
            {
                var c = Cost(track, lum, region.Width, region.Height, regionX, regionY, sx, sy, stride);
                if (c == double.MaxValue) continue;
                samples.Add(c);
                if (c < best)
                {
                    best = c;
                    bestX = sx;
                    bestY = sy;
                }
            }
        }
        if (samples.Count == 0) return null;
        samples.Sort();
        var typical = samples[samples.Count / 2];

        var refined = double.MaxValue;
        var refinedX = bestX;
        var refinedY = bestY;
        for (var sy = bestY - step; sy <= bestY + step; sy++)
        {
            for (var sx = bestX - step; sx <= bestX + step; sx++)
            {
                if (Math.Abs(sx) > radius || Math.Abs(sy) > radius) continue;
                var c = Cost(track, lum, region.Width, region.Height, regionX, regionY, sx, sy, 1);
                if (c < refined)
                {
                    refined = c;
                    refinedX = sx;
                    refinedY = sy;
                }
            }
        }
        if (refined == double.MaxValue) return null;
        return new GlyphMatch(refinedX, refinedY, refinedX * track.Step, refinedY * track.Step, refined, full, typical, track.Contrast,
            LocalContrast(track, lum, region.Width, region.Height, regionX, regionY, refinedX, refinedY), track.TemplateContrast);
    }

    private static double? LocalContrast(GlyphTrack track, float[] lum, int width, int height, int regionX, int regionY, int sx, int sy)
    {
        if (track.GlyphPoints.Length == 0 || track.PartnerPoints.Length == 0) return null;
        var ink = Mean(track, track.GlyphPoints, lum, width, height, regionX, regionY, sx, sy);
        var partner = Mean(track, track.PartnerPoints, lum, width, height, regionX, regionY, sx, sy);
        return ink is { } i && partner is { } p ? i - p : null;
    }

    private static double? Mean(GlyphTrack track, int[] points, float[] lum, int width, int height, int regionX, int regionY, int sx, int sy)
    {
        double sum = 0;
        var count = 0;
        var stride = Math.Max(1, points.Length / 512);
        for (var k = 0; k < points.Length; k += stride)
        {
            var p = points[k];
            var x = p % track.Width + sx;
            var y = p / track.Width + sy;
            var wx = (int)Math.Floor(track.OriginX + (x + 0.5) * track.Step) - regionX;
            var wy = (int)Math.Floor(track.OriginY + (y + 0.5) * track.Step) - regionY;
            if ((uint)wx >= (uint)width || (uint)wy >= (uint)height) continue;
            sum += lum[wy * width + wx];
            count++;
        }
        return count * 2 >= (points.Length + stride - 1) / stride ? sum / count : null;
    }

    private static float[] Luminance(OcrBitmap region)
    {
        var result = new float[region.Width * region.Height];
        for (var y = 0; y < region.Height; y++)
        {
            var row = y * region.Stride;
            var target = y * region.Width;
            for (var x = 0; x < region.Width; x++)
            {
                var p = row + x * 4;
                result[target + x] = 0.114f * region.PixelsBgra32[p] + 0.587f * region.PixelsBgra32[p + 1] + 0.299f * region.PixelsBgra32[p + 2];
            }
        }
        return result;
    }

    private static double Cost(GlyphTrack track, float[] lum, int width, int height, int regionX, int regionY, int sx, int sy, int stride)
    {
        double sum = 0;
        var count = 0;
        var points = track.Points;
        for (var k = 0; k < points.Length; k += stride)
        {
            var p = points[k];
            var x = p % track.Width + sx;
            var y = p / track.Width + sy;
            var wx = (int)Math.Floor(track.OriginX + (x + 0.5) * track.Step) - regionX;
            var wy = (int)Math.Floor(track.OriginY + (y + 0.5) * track.Step) - regionY;
            if ((uint)wx >= (uint)width || (uint)wy >= (uint)height) return double.MaxValue;
            sum += Math.Abs(lum[wy * width + wx] - track.Values[k]);
            count++;
        }
        return count == 0 ? double.MaxValue : sum / count;
    }
}
