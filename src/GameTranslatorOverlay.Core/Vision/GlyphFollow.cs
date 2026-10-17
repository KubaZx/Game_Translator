using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

public readonly record struct GlyphFollowPlan(
    double Radius, int VelocityX, int VelocityY, double WideRadius, int WideStep, bool Travelling, double Reach);

public static class GlyphFollow
{
    public const double WideReach = 3.0;
    public const double ScaleLow = 0.9;
    public const double ScaleHigh = 1.1;
    public const double MinScale = 0.5;
    public const double MaxScale = 2.0;

    public static GlyphFollowPlan Plan(GlyphTrack track, double maxShift, bool isMoving, bool fresh, int velocityX, int velocityY, bool moving)
    {
        var vx = isMoving && !fresh ? velocityX : 0;
        var vy = isMoving && !fresh ? velocityY : 0;
        var radius = isMoving ? maxShift : Math.Max(4, 2 * track.Step);
        var wideRadius = maxShift * WideReach;
        var travelling = isMoving && !fresh && moving;
        var wideStep = travelling ? GlyphTracker.WideStep(track, wideRadius) : 0;
        var reach = (wideStep > 0 ? wideRadius : radius) + Math.Max(Math.Abs(vx), Math.Abs(vy)) * track.Step;
        return new GlyphFollowPlan(radius, vx, vy, wideStep > 0 ? wideRadius : 0, wideStep, travelling, reach);
    }

    public static RectPx SearchArea(GlyphTrack track, GlyphFollowPlan plan) =>
        track.SearchArea(plan.Reach, plan.Travelling ? ScaleHigh : 1);

    public static GlyphMatch? Locate(GlyphTrack track, GlyphField field, GlyphFollowPlan plan, bool refreshOnly)
    {
        GlyphMatch? match = null;
        if (plan.VelocityX != 0 || plan.VelocityY != 0)
            match = GlyphTracker.Locate(track, field,
                Math.Max(Math.Max(8, 4 * track.Step), 0.5 * Math.Max(Math.Abs(plan.VelocityX), Math.Abs(plan.VelocityY)) * track.Step),
                plan.VelocityX, plan.VelocityY);
        if (match is not { IsConfident: true })
            match = GlyphTracker.Locate(track, field, plan.Radius);
        if (match is not { IsConfident: true } && plan.WideStep > 0)
            match = GlyphTracker.Locate(track, field, plan.WideRadius, plan.VelocityX, plan.VelocityY, plan.WideStep);
        var ladderStep = plan.Travelling && !refreshOnly ? GlyphTracker.WideStep(track, plan.Radius) : 0;
        if (match is not { IsConfident: true } && ladderStep > 0)
            match = GlyphTracker.LocateScaled(track, field, plan.Radius, GlyphTracker.ScaleLadder(track, ScaleLow, ScaleHigh),
                plan.VelocityX, plan.VelocityY, ladderStep, match) ?? match;
        else if (match is { IsConfident: true, IsStatic: false } found && plan.Travelling)
            match = GlyphTracker.RefineScale(track, field, found);
        return match;
    }

    public static GlyphCover? Follow(GlyphCover cover, GlyphMatch? match, OcrBitmap region, RectPx area, bool renderPatches)
    {
        if (match is not { IsConfident: true } m) return null;
        if (m.Scale != 1 && cover.Track is { } scaled && scaled.Scale * m.Scale is < MinScale or > MaxScale) return null;
        var source = cover.Rescaled(m.Scale);
        var moved = renderPatches
            ? GlyphCoverBuilder.Refill(source, region, area.X, area.Y, m.WorkDx, m.WorkDy)
            : GlyphCoverBuilder.Shift(source, m.WorkDx, m.WorkDy);
        if (moved is { Track: { } track } && !m.IsStatic) moved = moved with { Track = GlyphTracker.Resample(track, region, area.X, area.Y) };
        return moved;
    }

    public static RectPx FollowBox(RectPx box, GlyphCover from, GlyphCover to) =>
        from.Anchor == to.Anchor ? box : new RectPx(
            box.X + to.Anchor.X - from.Anchor.X,
            box.Y + to.Anchor.Y - from.Anchor.Y,
            Math.Max(1, box.Width + to.Anchor.Width - from.Anchor.Width),
            Math.Max(1, box.Height + to.Anchor.Height - from.Anchor.Height));

    public static RectPx SignatureArea(RectPx box, int lineHeight, RectPx frameRect)
    {
        var margin = (int)Math.Clamp(Math.Round(Math.Max(1, lineHeight > 0 ? lineHeight : box.Height) * 0.16), 3, 24);
        return box.Intersect(frameRect).Inflate(margin).Intersect(frameRect);
    }
}
