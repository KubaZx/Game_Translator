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
    float TemplateContrast,
    bool PartnerIsOutline = false,
    double Stroke = 0)
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
    double? LocalContrast = null, float TemplateContrast = 0, bool PartnerIsOutline = false, bool Ambiguous = false,
    double? StaticLocalContrast = null)
{
    public bool IsStatic => WorkDx == 0 && WorkDy == 0;

    public double AcceptLimit => Math.Max(8.0, Contrast * 0.14);

    public bool KeepsContrast => Keeps(LocalContrast);

    public bool IsConfident => Cost <= AcceptLimit && (Typical <= 0 || Cost <= Typical * 0.55) && KeepsContrast && !Ambiguous;

    public bool LettersGone => StaticLocalContrast is { } local && Math.Abs(TemplateContrast) >= 12
        ? Math.Abs(local) < Math.Max(6.0, Math.Abs(TemplateContrast) * 0.2)
        : StaticCost > Math.Max(Contrast * 0.84, 80);

    private bool Keeps(double? measured)
    {
        if (measured is not { } local || Math.Abs(TemplateContrast) < 12) return true;
        if (Math.Sign(local) != Math.Sign(TemplateContrast)) return false;
        var share = PartnerIsOutline || TemplateContrast < 0 ? 0.5 : 0.2;
        return Math.Abs(local) >= Math.Max(6.0, Math.Abs(TemplateContrast) * share);
    }
}

public static class GlyphTracker
{
    public const int CoarsePoints = 256;

    public static GlyphMatch? Locate(GlyphTrack track, OcrBitmap region, int regionX, int regionY, double maxShift, int centerDx = 0, int centerDy = 0)
    {
        if (track.Points.Length < 8 || region.Width <= 0 || region.Height <= 0) return null;
        var lum = Luminance(region);
        var radius = Math.Max(0, (int)Math.Ceiling(maxShift / track.Step));
        var centerCost = Cost(track, lum, region.Width, region.Height, regionX, regionY, centerDx, centerDy, 1);
        var centerContrast = LocalContrast(track, lum, region.Width, region.Height, regionX, regionY, centerDx, centerDy);
        if (centerCost <= Math.Max(6.0, track.Contrast * 0.12) || radius == 0)
            return Match(track, centerDx, centerDy, centerCost, centerCost, 0, centerContrast, false, centerContrast);

        var stride = Math.Max(1, track.Points.Length / CoarsePoints);
        var step = radius > 12 && track.Stroke >= 4 ? 2 : 1;
        var coarse = new List<(int X, int Y, double Cost)>((2 * radius / step + 1) * (2 * radius / step + 1));
        for (var sy = centerDy - radius; sy <= centerDy + radius; sy += step)
        {
            for (var sx = centerDx - radius; sx <= centerDx + radius; sx += step)
            {
                var c = Cost(track, lum, region.Width, region.Height, regionX, regionY, sx, sy, stride);
                if (c != double.MaxValue) coarse.Add((sx, sy, c));
            }
        }
        if (coarse.Count == 0) return null;
        var ordered = coarse.OrderBy(static c => c.Cost).ToList();
        var typical = ordered[ordered.Count / 2].Cost;
        var seeds = new List<(int X, int Y, double Cost)>(3);
        foreach (var candidate in ordered)
        {
            if (seeds.Any(s => Math.Abs(s.X - candidate.X) <= 2 && Math.Abs(s.Y - candidate.Y) <= 2)) continue;
            seeds.Add(candidate);
            if (seeds.Count == 3) break;
        }

        var refined = double.MaxValue;
        var refinedX = centerDx;
        var refinedY = centerDy;
        foreach (var seed in seeds)
        {
            for (var sy = seed.Y - step; sy <= seed.Y + step; sy++)
            {
                for (var sx = seed.X - step; sx <= seed.X + step; sx++)
                {
                    if (Math.Abs(sx - centerDx) > radius || Math.Abs(sy - centerDy) > radius) continue;
                    var c = Cost(track, lum, region.Width, region.Height, regionX, regionY, sx, sy, 1);
                    if (c < refined)
                    {
                        refined = c;
                        refinedX = sx;
                        refinedY = sy;
                    }
                }
            }
        }
        if (refined == double.MaxValue) return null;
        var bestCoarse = ordered[0].Cost;
        var ambiguous = ordered.Any(c => (Math.Abs(c.X - refinedX) > 3 || Math.Abs(c.Y - refinedY) > 3)
            && c.Cost <= Math.Max(bestCoarse * 1.2, bestCoarse + 2));
        return Match(track, refinedX, refinedY, refined, centerCost, typical,
            LocalContrast(track, lum, region.Width, region.Height, regionX, regionY, refinedX, refinedY), ambiguous, centerContrast);
    }

    private static GlyphMatch Match(GlyphTrack track, int dx, int dy, double cost, double centerCost, double typical, double? contrast, bool ambiguous, double? centerContrast) =>
        new(dx, dy, dx * track.Step, dy * track.Step, cost, centerCost, typical, track.Contrast,
            contrast, track.TemplateContrast, track.PartnerIsOutline, ambiguous, centerContrast);

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
