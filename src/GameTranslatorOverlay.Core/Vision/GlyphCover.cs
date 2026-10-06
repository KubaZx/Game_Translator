using System.Diagnostics;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

public enum TextAlignHint
{
    Unknown,
    Left,
    Center,
    Right,
}

public sealed record GlyphCoverLine(double InkLeft, double InkRight, double InkTop, double Baseline, string Text, double Density = 0);

public sealed record GlyphCover
{
    public required byte[] PatchPbgra { get; init; }
    public required int PatchPixelWidth { get; init; }
    public required int PatchPixelHeight { get; init; }
    public required double PatchX { get; init; }
    public required double PatchY { get; init; }
    public required double PatchWidth { get; init; }
    public required double PatchHeight { get; init; }
    public bool Soft { get; init; }
    public int TextRgb { get; init; } = -1;
    public int OutlineRgb { get; init; } = -1;
    public double OutlinePx { get; init; }
    public double ShadowDx { get; init; }
    public double ShadowDy { get; init; }
    public IReadOnlyList<GlyphCoverLine> Lines { get; init; } = [];
    public double StrokePx { get; init; }
    public TextAlignHint Align { get; init; }
    public double MaskFraction { get; init; }
    public float[] Signature { get; init; } = [];
    public double BuildMs { get; init; }
    public double IconSkipPx { get; init; }
    public string IconToken { get; init; } = string.Empty;
    public double TailSkipPx { get; init; }
    public string TailToken { get; init; } = string.Empty;
    public RectPx Anchor { get; init; }
    public GlyphTrack? Track { get; init; }

    public GlyphCover AnchorTo(RectPx box) =>
        Relocate(Anchor.X - box.X, Anchor.Y - box.Y) with { Anchor = box };

    public double Ascent => Lines.Count == 0 ? 0 : Lines.Max(static l => l.Baseline - l.InkTop);

    public double LinePitch
    {
        get
        {
            if (Lines.Count < 2) return 0;
            var gaps = new List<double>(Lines.Count - 1);
            for (var i = 1; i < Lines.Count; i++) gaps.Add(Lines[i].Baseline - Lines[i - 1].Baseline);
            gaps.Sort();
            return gaps[gaps.Count / 2];
        }
    }

    public GlyphCover Relocate(double dx, double dy)
    {
        if (dx == 0 && dy == 0) return this;
        return this with
        {
            PatchX = PatchX + dx,
            PatchY = PatchY + dy,
            Lines = Lines.Select(l => l with
            {
                InkLeft = l.InkLeft + dx,
                InkRight = l.InkRight + dx,
                InkTop = l.InkTop + dy,
                Baseline = l.Baseline + dy,
            }).ToList(),
        };
    }

    public static double SignatureDifference(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length) return double.PositiveInfinity;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }
}

public sealed record InkProfile(int Left, int Right, int Top, int Baseline, int Bottom, double Density, int Pixels)
{
    public int Ascent => Baseline - Top;

    public static InkProfile? Measure(ReadOnlySpan<bool> mask, int width, RectPx area)
    {
        if (area.IsEmpty || width <= 0) return null;
        var rows = new int[area.Height];
        var columns = new bool[area.Width];
        var pixels = 0;
        for (var y = 0; y < area.Height; y++)
        {
            var offset = (area.Y + y) * width + area.X;
            for (var x = 0; x < area.Width; x++)
            {
                if (!mask[offset + x]) continue;
                rows[y]++;
                columns[x] = true;
                pixels++;
            }
        }
        if (pixels == 0) return null;
        var rowMax = rows.Max();
        var bottom = Array.FindLastIndex(rows, static r => r > 0);
        var baseline = Array.FindLastIndex(rows, r => r >= rowMax * 0.3) + 1;
        var floor = Math.Max(1, rowMax * 0.02);
        var top = Math.Max(0, baseline - 1);
        while (top > 0 && rows[top - 1] >= floor) top--;
        var left = Array.IndexOf(columns, true);
        var right = Array.LastIndexOf(columns, true) + 1;
        var ascent = Math.Max(1, baseline - top);
        var density = (double)pixels / (Math.Max(1, right - left) * ascent);
        return new InkProfile(area.X + left, area.X + right, area.Y + top, area.Y + baseline, area.Y + bottom + 1, density, pixels);
    }
}

public static class GlyphCoverBuilder
{
    public const long MaxRegionPixels = 2_500_000;
    public const double StaticSignatureTolerance = 3.0;

    private const int SignatureColumns = 12;
    private const int SignatureRows = 4;

    public static GlyphCover? Build(
        OcrBitmap frame, RectPx box, IReadOnlyList<RectPx>? lineBoxes = null, IReadOnlyList<string>? lineTexts = null,
        double nativeScale = 1.0, bool soft = false, IReadOnlyList<OcrWord>? firstLineWords = null, GlyphTrackMap? map = null) =>
        BuildCore(frame, box, lineBoxes, lineTexts, nativeScale, soft, firstLineWords, Stopwatch.StartNew(), allowScale: true,
            map ?? new GlyphTrackMap(0, 0, nativeScale));

    private static GlyphCover? BuildScaled(
        OcrBitmap frame, RectPx box, IReadOnlyList<RectPx> lines, IReadOnlyList<string> lineTexts, double nativeScale, bool soft,
        IReadOnlyList<OcrWord>? firstLineWords, double lineHeight, int workScale, Stopwatch watch, GlyphTrackMap map)
    {
        var frameRect = new RectPx(0, 0, frame.Width, frame.Height);
        var reach = Math.Clamp(Math.Round(lineHeight * 0.16), 3, 24) + Math.Clamp(Math.Round(lineHeight * 0.08), 2, 10);
        var pad = (int)Math.Ceiling(reach / workScale + 2) * workScale;
        var crop = box.Inflate(pad).Intersect(frameRect);
        var dx = box.X - crop.X;
        var dy = box.Y - crop.Y;
        dx -= dx % workScale;
        dy -= dy % workScale;
        crop = new RectPx(box.X - dx, box.Y - dy, crop.Right - (box.X - dx), crop.Bottom - (box.Y - dy));
        var width = crop.Width / workScale;
        var height = crop.Height / workScale;
        if (width < 4 || height < 4) return null;
        var pixels = new byte[width * height * 4];
        var area = workScale * workScale;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0;
                for (var sy = 0; sy < workScale; sy++)
                {
                    var offset = (crop.Y + y * workScale + sy) * frame.Stride + (crop.X + x * workScale) * 4;
                    for (var sx = 0; sx < workScale; sx++)
                    {
                        b += frame.PixelsBgra32[offset + sx * 4];
                        g += frame.PixelsBgra32[offset + sx * 4 + 1];
                        r += frame.PixelsBgra32[offset + sx * 4 + 2];
                    }
                }
                var o = (y * width + x) * 4;
                pixels[o] = (byte)(b / area);
                pixels[o + 1] = (byte)(g / area);
                pixels[o + 2] = (byte)(r / area);
                pixels[o + 3] = 255;
            }
        }
        var small = new OcrBitmap(pixels, width, height, width * 4);
        RectPx Map(RectPx rect) => new(
            (rect.X - crop.X) / workScale, (rect.Y - crop.Y) / workScale,
            Math.Max(1, rect.Width / workScale), Math.Max(1, rect.Height / workScale));
        var words = firstLineWords?.Select(w => w with { Box = Map(w.Box) }).ToList();
        var smallMap = new GlyphTrackMap(map.OriginX + crop.X * map.Scale, map.OriginY + crop.Y * map.Scale, map.Scale * workScale);
        return BuildCore(small, Map(box), lines.Select(Map).ToList(), lineTexts, nativeScale * workScale, soft, words, watch, allowScale: false, smallMap);
    }

    private static GlyphCover? BuildCore(
        OcrBitmap frame, RectPx box, IReadOnlyList<RectPx>? lineBoxes, IReadOnlyList<string>? lineTexts,
        double nativeScale, bool soft, IReadOnlyList<OcrWord>? firstLineWords, Stopwatch watch, bool allowScale, GlyphTrackMap map)
    {
        if (frame.PixelsBgra32 is null || frame.Width <= 0 || frame.Height <= 0) return null;
        var frameRect = new RectPx(0, 0, frame.Width, frame.Height);
        box = box.Intersect(frameRect);
        if (box.Width < 3 || box.Height < 3) return null;

        var paired = (lineBoxes is { Count: > 0 } ? lineBoxes : [box])
            .Select((l, i) => (Box: l.Intersect(frameRect), Text: lineTexts is not null && i < lineTexts.Count ? lineTexts[i] : string.Empty))
            .Where(static p => !p.Box.IsEmpty)
            .OrderBy(static p => p.Box.Y)
            .ToList();
        if (paired.Count == 0) paired.Add((box, lineTexts is { Count: > 0 } ? lineTexts[0] : string.Empty));
        var lines = paired.Select(static p => p.Box).ToList();
        lineTexts = paired.Select(static p => p.Text).ToList();
        var heights = lines.Select(static l => l.Height).OrderBy(static h => h).ToList();
        double lineHeight = heights[heights.Count / 2];
        var workScale = lineHeight >= 150 ? 3 : lineHeight >= 90 ? 2 : 1;
        if (allowScale && workScale > 1)
            return BuildScaled(frame, box, lines, lineTexts, nativeScale, soft, firstLineWords, lineHeight, workScale, watch, map);

        var margin = (int)Math.Clamp(Math.Round(lineHeight * 0.16), 3, 24);
        var ringWidth = (int)Math.Clamp(Math.Round(lineHeight * 0.08), 2, 10);
        var region = box.Inflate(margin).Intersect(frameRect);
        var outer = region.Inflate(ringWidth).Intersect(frameRect);
        if ((long)outer.Width * outer.Height > MaxRegionPixels) return null;

        var iconSkip = 0;
        var iconToken = string.Empty;
        if (firstLineWords is { Count: >= 2 } && IsIconToken(firstLineWords[0].Text))
        {
            var first = firstLineWords[0].Box;
            var second = firstLineWords[1].Box;
            if (second.X - first.Right >= lineHeight * 0.45 && first.Right > box.X && second.X > box.X)
            {
                iconSkip = Math.Clamp(second.X - box.X - Math.Max(1, (int)Math.Round(lineHeight * 0.12)), 0, box.Width);
                iconToken = firstLineWords[0].Text.Trim();
            }
        }
        var tailSkip = 0;
        var tailToken = string.Empty;
        if (lines.Count == 1 && firstLineWords is { Count: >= 2 } && firstLineWords[^1].Text.Trim().Length is >= 1 and <= 2)
        {
            var last = firstLineWords[^1].Box;
            var before = firstLineWords[^2].Box;
            var others = firstLineWords.Take(firstLineWords.Count - 1).Select(static w => w.Box.Height).OrderBy(static h => h).ToList();
            double wordHeight = others[others.Count / 2];
            var gap = last.X - before.Right;
            var iconLike = gap >= wordHeight * 0.55 || (last.Height >= wordHeight * 1.1 && gap >= wordHeight * 0.3);
            if (iconLike && last.X < box.Right && last.X > box.X + iconSkip)
            {
                tailSkip = Math.Clamp(box.Right - last.X + Math.Max(1, (int)Math.Round(lineHeight * 0.12)), 0, box.Width - iconSkip - 1);
                tailToken = firstLineWords[^1].Text.Trim();
            }
        }

        var work = new Workspace(frame, outer);
        var regionLocal = new RectPx(region.X - outer.X, region.Y - outer.Y, region.Width, region.Height);
        var textLocal = new RectPx(box.X - outer.X + iconSkip, box.Y - outer.Y, box.Width - iconSkip - tailSkip, box.Height)
            .Inflate(Math.Max(1, margin / 2)).Intersect(regionLocal);

        var ds = Math.Max(1, (int)Math.Round(lineHeight / 24.0));
        var fill = DetectFill(work, regionLocal, textLocal, lineHeight, ds, out var bright);
        if (fill is null) return null;

        var halo = (int)Math.Clamp(Math.Round(lineHeight * 0.2), 2, 30);
        var nearText = Morphology.Dilate(fill, work.Width, work.Height, halo + ds);
        var smoothing = Math.Max(2, ds * 2);
        var background = work.SmoothExcluding(nearText, smoothing);
        if (background is null) return null;

        var contrast = new float[work.Count];
        var sign = bright ? 1f : -1f;
        for (var i = 0; i < work.Count; i++) contrast[i] = sign * (work.L[i] - Luma(background, i));

        var widened = Morphology.Dilate(fill, work.Width, work.Height, ds);
        var textContrast = Percentile(contrast, widened, 0.9);
        if (textContrast < 15) return null;

        var inText = RectMask(textLocal, work.Width, work.Height);
        var inRegion = RectMask(regionLocal, work.Width, work.Height);
        var refined = new bool[work.Count];
        var fillCount = 0;
        for (var i = 0; i < work.Count; i++)
        {
            if (!widened[i] || !inText[i]) continue;
            if (contrast[i] > textContrast * 0.5f)
            {
                refined[i] = true;
                fillCount++;
            }
        }
        if (fillCount < 12) return null;

        var textRgb = MeanRgb(work, i => refined[i] && contrast[i] > textContrast * 0.75f)
            ?? MeanRgb(work, i => refined[i]) ?? -1;

        var backgroundNoise = NoiseLevel(work, i => !nearText[i]);
        var haloThreshold = (float)Math.Clamp(Math.Max(8, backgroundNoise * 4), 8, Math.Max(8, textContrast * 0.15));
        var haloZone = Morphology.Dilate(refined, work.Width, work.Height, halo);
        var haloMask = new bool[work.Count];
        var outline = new bool[work.Count];
        var outlineCount = 0;
        for (var i = 0; i < work.Count; i++)
        {
            if (!haloZone[i] || refined[i] || !inRegion[i]) continue;
            if (ColorDistance(work, background, i) <= haloThreshold) continue;
            haloMask[i] = true;
            if (contrast[i] < -haloThreshold * 0.5f)
            {
                outline[i] = true;
                outlineCount++;
            }
        }

        var perimeter = Morphology.Perimeter(refined, work.Width, work.Height);
        var outlineRgb = -1;
        double outlinePx = 0, shadowDx = 0, shadowDy = 0;
        if (perimeter > 0 && outlineCount >= perimeter * 0.3)
        {
            outlineRgb = MeanRgb(work, i => outline[i]) ?? -1;
            var extents = OutlineExtents(refined, outline, work.Width, work.Height, halo);
            var thickness = Math.Min(Math.Min(extents.Up, extents.Down), Math.Min(extents.Left, extents.Right));
            var surrounding = MeanRgb(work, i => haloZone[i] && !haloMask[i] && !refined[i]);
            var separation = outlineRgb >= 0 && surrounding is { } around
                ? Math.Abs(LumaOf(outlineRgb) - LumaOf(around)) : 0;
            var hasOutline = thickness >= 2 || (thickness >= 1 && separation >= 30);
            outlinePx = hasOutline ? thickness : 0;
            shadowDx = extents.Right - extents.Left;
            shadowDy = extents.Down - extents.Up;
            var minimumShadow = Math.Max(1.5, outlinePx * 0.35);
            if (Math.Abs(shadowDx) < minimumShadow) shadowDx = 0;
            if (Math.Abs(shadowDy) < minimumShadow) shadowDy = 0;
            if (!hasOutline && (shadowDx == 0 && shadowDy == 0 || separation < 12)) outlineRgb = -1;
            if (outlineRgb < 0)
            {
                outlinePx = 0;
                shadowDx = 0;
                shadowDy = 0;
            }
        }

        var mask = new bool[work.Count];
        for (var i = 0; i < work.Count; i++) mask[i] = refined[i] || haloMask[i];
        mask = Morphology.Dilate(mask, work.Width, work.Height, lineHeight >= 36 ? 2 : 1);
        var maskCount = 0;
        var regionCount = regionLocal.Width * regionLocal.Height;
        for (var i = 0; i < work.Count; i++)
        {
            if (!inRegion[i]) mask[i] = false;
            else if (iconSkip > 0 && i % work.Width < textLocal.X) mask[i] = false;
            else if (tailSkip > 0 && i % work.Width >= textLocal.Right) mask[i] = false;
            else if (mask[i]) maskCount++;
        }
        var maskFraction = regionCount == 0 ? 1 : (double)maskCount / regionCount;
        if (maskFraction > 0.85) soft = true;

        var filled = work.InpaintExcluding(mask);
        if (filled is null) return null;

        var feather = Math.Max(2, margin / 2);
        var skipLeft = iconSkip > 0 ? textLocal.X - regionLocal.X : 0;
        var skipRight = tailSkip > 0 ? regionLocal.Right - textLocal.Right : 0;
        var patch = BuildPatch(filled, mask, work.Width, regionLocal, soft, feather, skipLeft, skipRight);
        var track = BuildTrack(work, refined, outline, contrast, inRegion, mask, regionLocal, soft, feather, skipLeft, skipRight, outer, map);

        var coverLines = MeasureLines(refined, work.Width, work.Height, lines, outer, margin, lineTexts);
        var stroke = MeasureStroke(refined, work.Width, coverLines.Count > 0 ? coverLines[0] : null);
        var align = AlignmentOf(coverLines);

        var origin = (X: box.X - outer.X, Y: box.Y - outer.Y);
        var s = nativeScale;
        return new GlyphCover
        {
            PatchPbgra = patch,
            PatchPixelWidth = regionLocal.Width,
            PatchPixelHeight = regionLocal.Height,
            PatchX = (regionLocal.X - origin.X) * s,
            PatchY = (regionLocal.Y - origin.Y) * s,
            PatchWidth = regionLocal.Width * s,
            PatchHeight = regionLocal.Height * s,
            Soft = soft,
            TextRgb = textRgb,
            OutlineRgb = outlineRgb,
            OutlinePx = outlinePx * s,
            ShadowDx = shadowDx * s,
            ShadowDy = shadowDy * s,
            Lines = coverLines.Select(l => new GlyphCoverLine(
                (l.InkLeft - origin.X) * s, (l.InkRight - origin.X) * s,
                (l.InkTop - origin.Y) * s, (l.Baseline - origin.Y) * s, l.Text, l.Density)).ToList(),
            StrokePx = stroke * s,
            Align = align,
            MaskFraction = maskFraction,
            Signature = Signature(frame, region),
            BuildMs = watch.Elapsed.TotalMilliseconds,
            IconSkipPx = iconSkip * s,
            IconToken = iconToken,
            TailSkipPx = tailSkip * s,
            TailToken = tailToken,
            Track = track,
        };
    }

    private static GlyphTrack? BuildTrack(
        Workspace work, bool[] glyph, bool[] outline, float[] contrast, bool[] inRegion, bool[] mask, RectPx regionLocal,
        bool soft, int feather, int skipLeft, int skipRight, RectPx outer, GlyphTrackMap map)
    {
        var width = work.Width;
        var height = work.Height;
        var points = new List<int>();
        var strong = new List<int>();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                if (!inRegion[i]) continue;
                var isGlyph = glyph[i];
                var isOutline = outline[i];
                if (!isGlyph && !isOutline) continue;
                var interior = true;
                foreach (var j in (ReadOnlySpan<int>)[i - 1, i + 1, i - width, i + width])
                {
                    if (isGlyph ? !glyph[j] : !(outline[j] || glyph[j]))
                    {
                        interior = false;
                        break;
                    }
                }
                if (interior) strong.Add(i);
                points.Add(i);
            }
        }
        var chosen = strong.Count >= 24 ? strong : points;
        if (chosen.Count < 8) return null;
        var values = new float[chosen.Count];
        double contrastSum = 0;
        var glyphCount = 0;
        for (var k = 0; k < chosen.Count; k++)
        {
            values[k] = work.L[chosen[k]];
            if (glyph[chosen[k]])
            {
                contrastSum += Math.Abs(contrast[chosen[k]]);
                glyphCount++;
            }
        }
        var level = glyphCount > 0 ? (float)(contrastSum / glyphCount) : 30f;
        var glyphPoints = chosen.Where(i => glyph[i]).ToArray();
        var outlinePoints = chosen.Where(i => outline[i] && !glyph[i]).ToArray();
        int[] partners;
        if (outlinePoints.Length >= Math.Max(8, glyphPoints.Length * 0.3))
        {
            partners = outlinePoints;
        }
        else
        {
            var around = Morphology.Dilate(mask, width, height, 2);
            var ring = new List<int>();
            for (var i = 0; i < around.Length; i++)
                if (around[i] && !mask[i] && inRegion[i]) ring.Add(i);
            partners = ring.ToArray();
        }
        float templateContrast = 0;
        if (glyphPoints.Length > 0 && partners.Length > 0)
            templateContrast = (float)(glyphPoints.Average(i => work.L[i]) - partners.Average(i => work.L[i]));
        return new GlyphTrack(
            map.OriginX + outer.X * map.Scale,
            map.OriginY + outer.Y * map.Scale,
            map.Scale,
            width,
            height,
            mask,
            regionLocal,
            soft,
            feather,
            skipLeft,
            skipRight,
            chosen.ToArray(),
            values,
            level,
            glyphPoints,
            partners,
            templateContrast);
    }

    public static GlyphCover? Refill(GlyphCover cover, OcrBitmap region, int regionX, int regionY, int workDx = 0, int workDy = 0, bool? soft = null)
    {
        if (cover.Track is not { } track) return null;
        var width = track.Width;
        var height = track.Height;
        var count = width * height;
        var channels = new[] { new float[count], new float[count], new float[count] };
        var known = new bool[count];
        var any = false;
        var span = Math.Max(1, (int)Math.Round(track.Step));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (track.Mask[i]) continue;
                var wx = (int)Math.Floor(track.OriginX + (x + workDx) * track.Step) - regionX;
                var wy = (int)Math.Floor(track.OriginY + (y + workDy) * track.Step) - regionY;
                if (wx < 0 || wy < 0 || wx + span > region.Width || wy + span > region.Height) continue;
                int r = 0, g = 0, b = 0;
                for (var sy = 0; sy < span; sy++)
                {
                    var row = (wy + sy) * region.Stride + wx * 4;
                    for (var sx = 0; sx < span; sx++)
                    {
                        var p = row + sx * 4;
                        b += region.PixelsBgra32[p];
                        g += region.PixelsBgra32[p + 1];
                        r += region.PixelsBgra32[p + 2];
                    }
                }
                var area = span * span;
                channels[0][i] = (float)r / area;
                channels[1][i] = (float)g / area;
                channels[2][i] = (float)b / area;
                known[i] = true;
                any = true;
            }
        }
        if (!any) return null;
        PushPull.Fill(channels, known, width, height);
        var useSoft = soft ?? track.Soft;
        var patch = BuildPatch(channels, track.Mask, width, track.Region, useSoft, track.Feather, track.SkipLeft, track.SkipRight);
        var dx = (int)Math.Round(workDx * track.Step);
        var dy = (int)Math.Round(workDy * track.Step);
        return cover with
        {
            PatchPbgra = patch,
            Soft = useSoft,
            Track = track.Shifted(workDx, workDy),
            Anchor = dx == 0 && dy == 0 ? cover.Anchor : cover.Anchor.Offset(dx, dy),
        };
    }

    public static void WarmUp()
    {
        const int width = 320;
        const int height = 200;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 30;
            pixels[i + 2] = 30;
            pixels[i + 3] = 255;
        }
        for (var y = 40; y < 140; y++)
        {
            for (var x = 40; x < 280; x++)
            {
                if ((x / 12) % 3 != 0 && y > 50 && y < 130) continue;
                var o = (y * width + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = 230;
            }
        }
        var frame = new OcrBitmap(pixels, width, height, width * 4);
        Build(frame, new RectPx(36, 36, 248, 108), [new RectPx(36, 36, 248, 108)], ["warm up"]);
        Build(frame, new RectPx(36, 36, 248, 40), [new RectPx(36, 36, 248, 40)], ["warm"]);
    }

    public static RectPx ToSampleBox(RectPx windowBox, RectPx ocrRegion, double scaleBack) => new(
        (int)((windowBox.X - ocrRegion.X) / scaleBack),
        (int)((windowBox.Y - ocrRegion.Y) / scaleBack),
        Math.Max(1, (int)(windowBox.Width / scaleBack)),
        Math.Max(1, (int)(windowBox.Height / scaleBack)));

    public static GlyphCover? BuildForBlock(
        OcrBitmap frame, GameTranslatorOverlay.Core.Text.TextBlock block, RectPx ocrRegion, double scaleBack, bool soft = false)
    {
        var rows = Rows(block.Lines);
        var lines = rows.Select(r => ToSampleBox(r.Box, ocrRegion, scaleBack)).ToList();
        var texts = rows.Count > 0 ? rows.Select(static r => r.Text).ToList() : [block.Text];
        var words = rows.Count > 0
            ? rows[0].Words.Select(w => w with { Box = ToSampleBox(w.Box, ocrRegion, scaleBack) }).ToList()
            : null;
        return Build(frame, ToSampleBox(block.Box, ocrRegion, scaleBack), lines, texts, scaleBack, soft, words,
            new GlyphTrackMap(ocrRegion.X, ocrRegion.Y, scaleBack));
    }

    internal static List<(RectPx Box, string Text, List<OcrWord> Words)> Rows(IReadOnlyList<OcrLine> lines)
    {
        var rows = new List<(RectPx Box, List<OcrLine> Lines)>();
        foreach (var line in lines.OrderBy(static l => l.Box.Y).ThenBy(static l => l.Box.X))
        {
            var index = rows.FindIndex(r =>
            {
                var overlap = Math.Min(r.Box.Bottom, line.Box.Bottom) - Math.Max(r.Box.Y, line.Box.Y);
                return overlap >= Math.Max(1, Math.Min(r.Box.Height, line.Box.Height)) * 0.5;
            });
            if (index < 0) rows.Add((line.Box, [line]));
            else
            {
                rows[index].Lines.Add(line);
                rows[index] = (rows[index].Box.Union(line.Box), rows[index].Lines);
            }
        }
        return rows
            .OrderBy(static r => r.Box.Y)
            .Select(static r =>
            {
                var ordered = r.Lines.OrderBy(static l => l.Box.X).ToList();
                return (r.Box, string.Join(' ', ordered.Select(static l => l.Text)), ordered.SelectMany(static l => l.Words).ToList());
            })
            .ToList();
    }

    public static float[] Signature(OcrBitmap frame, RectPx region)
    {
        region = region.Intersect(new RectPx(0, 0, frame.Width, frame.Height));
        var result = new float[SignatureColumns * SignatureRows];
        if (region.IsEmpty) return result;
        var counts = new int[result.Length];
        var step = Math.Max(1, Math.Min(region.Width, region.Height) / 24);
        for (var y = region.Y; y < region.Bottom; y += step)
        {
            var row = Math.Min(SignatureRows - 1, (y - region.Y) * SignatureRows / region.Height);
            var offset = y * frame.Stride;
            for (var x = region.X; x < region.Right; x += step)
            {
                var col = Math.Min(SignatureColumns - 1, (x - region.X) * SignatureColumns / region.Width);
                var p = offset + x * 4;
                var cell = row * SignatureColumns + col;
                result[cell] += 0.114f * frame.PixelsBgra32[p] + 0.587f * frame.PixelsBgra32[p + 1] + 0.299f * frame.PixelsBgra32[p + 2];
                counts[cell]++;
            }
        }
        for (var i = 0; i < result.Length; i++) result[i] = counts[i] > 0 ? result[i] / counts[i] : 0;
        return result;
    }

    public static RectPx SignatureRegion(RectPx box, IReadOnlyList<RectPx>? lineBoxes, RectPx frameRect)
    {
        var heights = (lineBoxes is { Count: > 0 } ? lineBoxes.Select(static l => l.Height) : [box.Height]).OrderBy(static h => h).ToList();
        var margin = (int)Math.Clamp(Math.Round(heights[heights.Count / 2] * 0.16), 3, 24);
        return box.Intersect(frameRect).Inflate(margin).Intersect(frameRect);
    }

    private static readonly HashSet<string> KeyTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "tab", "esc", "lb", "rb", "lt", "rt", "l1", "r1", "l2", "r2", "f1", "f2", "f3", "f4",
    };

    internal static bool IsIconToken(string token)
    {
        var t = token.Trim();
        if (t.Length == 1) return char.IsLetterOrDigit(t[0]);
        return KeyTokens.Contains(t);
    }

    private static bool[]? DetectFill(Workspace work, RectPx regionLocal, RectPx textLocal, double lineHeight, int ds, out bool bright)
    {
        bright = true;
        var ringOnly = RectMask(regionLocal, work.Width, work.Height);
        var anyRing = Array.IndexOf(ringOnly, false) >= 0;
        if (!anyRing) return null;
        var ringBackground = work.SmoothExcluding(ringOnly, Math.Max(2, ds * 2));
        if (ringBackground is null) return null;
        var noise = NoiseLevel(work, i => !ringOnly[i]);
        var threshold = Math.Max(16f, noise * 4f);

        var deviation = new float[work.Count];
        var brightSet = new bool[work.Count];
        var darkSet = new bool[work.Count];
        double brightEnergy = 0, darkEnergy = 0;
        for (var y = textLocal.Y; y < textLocal.Bottom; y++)
        {
            for (var x = textLocal.X; x < textLocal.Right; x++)
            {
                var i = y * work.Width + x;
                var d = work.L[i] - Luma(ringBackground, i);
                deviation[i] = d;
                if (d > threshold)
                {
                    brightSet[i] = true;
                    brightEnergy += d - threshold;
                }
                else if (d < -threshold)
                {
                    darkSet[i] = true;
                    darkEnergy += -d - threshold;
                }
            }
        }
        if (brightEnergy <= 0 && darkEnergy <= 0) return null;
        var balance = Math.Min(brightEnergy, darkEnergy) / Math.Max(brightEnergy, darkEnergy);
        if (balance < 0.35)
        {
            bright = brightEnergy >= darkEnergy;
        }
        else
        {
            var brightEnclosed = Enclosure(brightSet, darkSet, work.Width, work.Height);
            var darkEnclosed = Enclosure(darkSet, brightSet, work.Width, work.Height);
            bright = Math.Abs(brightEnclosed - darkEnclosed) > 0.1 ? brightEnclosed > darkEnclosed : brightEnergy >= darkEnergy;
        }

        var wd = (work.Width + ds - 1) / ds;
        var hd = (work.Height + ds - 1) / ds;
        var small = new float[wd * hd];
        var counts = new int[wd * hd];
        for (var y = 0; y < work.Height; y++)
        {
            var row = y / ds * wd;
            for (var x = 0; x < work.Width; x++)
            {
                small[row + x / ds] += work.L[y * work.Width + x];
                counts[row + x / ds]++;
            }
        }
        for (var i = 0; i < small.Length; i++) small[i] /= Math.Max(1, counts[i]);

        var k = Math.Max(3, (int)Math.Round(lineHeight * 0.45 / ds)) | 1;
        float[] hat;
        if (bright)
        {
            var opened = Morphology.MaxFilter(Morphology.MinFilter(small, wd, hd, k), wd, hd, k);
            hat = new float[small.Length];
            for (var i = 0; i < small.Length; i++) hat[i] = small[i] - opened[i];
        }
        else
        {
            var closed = Morphology.MinFilter(Morphology.MaxFilter(small, wd, hd, k), wd, hd, k);
            hat = new float[small.Length];
            for (var i = 0; i < small.Length; i++) hat[i] = closed[i] - small[i];
        }

        var sign = bright ? 1f : -1f;
        var histogram = new int[512];
        var total = 0;
        for (var y = textLocal.Y; y < textLocal.Bottom; y++)
        {
            for (var x = textLocal.X; x < textLocal.Right; x++)
            {
                var i = y * work.Width + x;
                if (sign * deviation[i] <= threshold) continue;
                histogram[Math.Clamp((int)(hat[Math.Min(hd - 1, y / ds) * wd + Math.Min(wd - 1, x / ds)] * 2), 0, 511)]++;
                total++;
            }
        }
        if (total < 12) return null;
        var rank = (int)Math.Floor(total * 0.97);
        var seen = 0;
        var top = 255.5f;
        for (var v = 0; v < histogram.Length; v++)
        {
            seen += histogram[v];
            if (seen > rank)
            {
                top = v / 2f;
                break;
            }
        }
        if (top < 18) return null;
        var hatThreshold = Math.Max(12f, top * 0.35f);

        var mask = new bool[work.Count];
        var any = 0;
        for (var y = textLocal.Y; y < textLocal.Bottom; y++)
        {
            for (var x = textLocal.X; x < textLocal.Right; x++)
            {
                var i = y * work.Width + x;
                if (sign * deviation[i] <= threshold) continue;
                if (hat[Math.Min(hd - 1, y / ds) * wd + Math.Min(wd - 1, x / ds)] <= hatThreshold) continue;
                mask[i] = true;
                any++;
            }
        }
        return any >= 12 ? mask : null;
    }

    private static double Enclosure(bool[] set, bool[] other, int width, int height)
    {
        var boundary = 0;
        var enclosed = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                if (!set[i]) continue;
                var touchesOther = false;
                var isBoundary = false;
                foreach (var j in (ReadOnlySpan<int>)[i - 1, i + 1, i - width, i + width])
                {
                    if (set[j]) continue;
                    isBoundary = true;
                    touchesOther |= other[j];
                }
                if (!isBoundary) continue;
                boundary++;
                if (touchesOther) enclosed++;
            }
        }
        return boundary == 0 ? 0 : (double)enclosed / boundary;
    }

    private static float NoiseLevel(Workspace work, Func<int, bool> take)
    {
        var histogram = new int[256];
        var total = 0;
        var step = Math.Max(1, (int)Math.Sqrt(work.Count / 40000.0));
        for (var y = 0; y < work.Height; y += step)
        {
            for (var x = 0; x + 1 < work.Width; x += step)
            {
                var i = y * work.Width + x;
                if (!take(i) || !take(i + 1)) continue;
                histogram[Math.Min(255, (int)Math.Abs(work.L[i] - work.L[i + 1]))]++;
                total++;
            }
        }
        if (total == 0) return 0;
        var seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen * 2 >= total) return v + 0.5f;
        }
        return 255;
    }

    private static bool[] RectMask(RectPx rect, int width, int height)
    {
        var mask = new bool[width * height];
        var x0 = Math.Clamp(rect.X, 0, width);
        var x1 = Math.Clamp(rect.Right, 0, width);
        for (var y = Math.Clamp(rect.Y, 0, height); y < Math.Clamp(rect.Bottom, 0, height); y++)
        {
            Array.Fill(mask, true, y * width + x0, x1 - x0);
        }
        return mask;
    }

    private static byte[] BuildPatch(float[][] filled, bool[] mask, int width, RectPx region, bool soft, int feather, int skipLeft, int skipRight)
    {
        var patch = new byte[region.Width * region.Height * 4];
        var near = soft ? null : Morphology.Dilate(mask, width, mask.Length / width, 1);
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var i = (region.Y + y) * width + region.X + x;
                double alpha;
                if (soft)
                {
                    if (x < skipLeft || x >= region.Width - skipRight)
                    {
                        alpha = 0;
                    }
                    else
                    {
                        var edge = Math.Min(Math.Min(x - skipLeft, region.Width - 1 - skipRight - x), Math.Min(y, region.Height - 1 - y));
                        alpha = Math.Clamp((edge + 0.5) / feather, 0, 1);
                    }
                }
                else
                {
                    alpha = mask[i] ? 1 : near![i] ? 0.5 : 0;
                }
                if (alpha <= 0) continue;
                var o = (y * region.Width + x) * 4;
                patch[o] = (byte)Math.Clamp(Math.Round(filled[2][i] * alpha), 0, 255);
                patch[o + 1] = (byte)Math.Clamp(Math.Round(filled[1][i] * alpha), 0, 255);
                patch[o + 2] = (byte)Math.Clamp(Math.Round(filled[0][i] * alpha), 0, 255);
                patch[o + 3] = (byte)Math.Clamp(Math.Round(255 * alpha), 0, 255);
            }
        }
        return patch;
    }

    private sealed record LineMeasure(double InkLeft, double InkRight, double InkTop, double Baseline, string Text, double Density);

    private static List<LineMeasure> MeasureLines(
        bool[] fill, int width, int height, List<RectPx> lines, RectPx outer, int margin, IReadOnlyList<string>? texts)
    {
        var result = new List<LineMeasure>();
        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            var top = line.Y - outer.Y - margin;
            var bottom = line.Bottom - outer.Y + margin;
            if (li > 0) top = Math.Max(top, (lines[li - 1].Bottom + line.Y) / 2 - outer.Y);
            if (li + 1 < lines.Count) bottom = Math.Min(bottom, (line.Bottom + lines[li + 1].Y) / 2 - outer.Y);
            top = Math.Clamp(top, 0, height);
            bottom = Math.Clamp(bottom, 0, height);
            var left = Math.Clamp(line.X - outer.X - margin, 0, width);
            var right = Math.Clamp(line.Right - outer.X + margin, 0, width);
            if (bottom - top < 2 || right - left < 2) continue;
            var profile = InkProfile.Measure(fill, width, new RectPx(left, top, right - left, bottom - top));
            if (profile is null) continue;
            var text = texts is not null && li < texts.Count ? texts[li] : string.Empty;
            result.Add(new LineMeasure(profile.Left, profile.Right, profile.Top, profile.Baseline, text, profile.Density));
        }
        return result;
    }

    private static double MeasureStroke(bool[] fill, int width, LineMeasure? line)
    {
        if (line is null) return 0;
        var ascent = line.Baseline - line.InkTop;
        var y0 = (int)Math.Round(line.InkTop + ascent * 0.45);
        var y1 = (int)Math.Round(line.Baseline - ascent * 0.2);
        var x0 = (int)Math.Floor(line.InkLeft);
        var x1 = (int)Math.Ceiling(line.InkRight);
        var runs = new List<int>();
        var height = fill.Length / width;
        return MeasureRuns(fill, width, height, y0, y1, x0, x1, runs);
    }

    private static double MeasureRuns(bool[] fill, int width, int height, int y0, int y1, int x0, int x1, List<int> runs)
    {
        y0 = Math.Clamp(y0, 0, height);
        y1 = Math.Clamp(y1, 0, height);
        x0 = Math.Clamp(x0, 0, width);
        x1 = Math.Clamp(x1, 0, width);
        for (var y = y0; y < y1; y++)
        {
            var run = 0;
            for (var x = x0; x < x1; x++)
            {
                if (fill[y * width + x]) run++;
                else if (run > 0)
                {
                    runs.Add(run);
                    run = 0;
                }
            }
            if (run > 0) runs.Add(run);
        }
        if (runs.Count == 0) return 0;
        runs.Sort();
        return runs[runs.Count / 2];
    }

    private static TextAlignHint AlignmentOf(List<LineMeasure> lines)
    {
        if (lines.Count < 2) return TextAlignHint.Unknown;
        var ascent = lines.Max(static l => l.Baseline - l.InkTop);
        var tolerance = Math.Max(4, ascent * 0.6);
        static double Spread(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Max() - list.Min();
        }
        var left = Spread(lines.Select(static l => l.InkLeft));
        var center = Spread(lines.Select(static l => (l.InkLeft + l.InkRight) / 2));
        var right = Spread(lines.Select(static l => l.InkRight));
        if (left <= tolerance && left <= center) return TextAlignHint.Left;
        if (center <= tolerance && center <= right) return TextAlignHint.Center;
        if (right <= tolerance) return TextAlignHint.Right;
        return TextAlignHint.Left;
    }

    private static (double Up, double Down, double Left, double Right) OutlineExtents(
        bool[] fill, bool[] outline, int width, int height, int reach)
    {
        var up = new List<int>();
        var down = new List<int>();
        var left = new List<int>();
        var right = new List<int>();
        for (var x = 0; x < width; x++)
        {
            var first = -1;
            var last = -1;
            for (var y = 0; y < height; y++)
            {
                if (!fill[y * width + x]) continue;
                if (first < 0) first = y;
                last = y;
            }
            if (first < 0) continue;
            up.Add(Extent(outline, (first - 1) * width + x, -width, first, reach));
            down.Add(Extent(outline, (last + 1) * width + x, width, height - last - 1, reach));
        }
        for (var y = 0; y < height; y++)
        {
            var first = -1;
            var last = -1;
            for (var x = 0; x < width; x++)
            {
                if (!fill[y * width + x]) continue;
                if (first < 0) first = x;
                last = x;
            }
            if (first < 0) continue;
            left.Add(Extent(outline, y * width + first - 1, -1, first, reach));
            right.Add(Extent(outline, y * width + last + 1, 1, width - last - 1, reach));
        }
        return (Median(up), Median(down), Median(left), Median(right));
    }

    private static int Extent(bool[] outline, int start, int step, int available, int reach)
    {
        var skipped = 0;
        while (skipped < 3 && skipped < available && !outline[start + skipped * step]) skipped++;
        var run = 0;
        while (skipped + run < available && run < reach && outline[start + (skipped + run) * step]) run++;
        return run == 0 ? 0 : skipped + run;
    }

    private static double Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static float Percentile(float[] values, bool[] mask, double q)
    {
        var histogram = new int[1024];
        var total = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (!mask[i]) continue;
            histogram[Math.Clamp((int)((values[i] + 256) * 2), 0, 1023)]++;
            total++;
        }
        if (total == 0) return 0;
        var rank = (long)Math.Floor(total * q);
        long seen = 0;
        for (var b = 0; b < histogram.Length; b++)
        {
            seen += histogram[b];
            if (seen > rank) return b / 2f - 256;
        }
        return 256;
    }


    private static double LumaOf(int rgb) => 0.299 * ((rgb >> 16) & 0xFF) + 0.587 * ((rgb >> 8) & 0xFF) + 0.114 * (rgb & 0xFF);

    private static float Luma(float[][] rgb, int i) => 0.299f * rgb[0][i] + 0.587f * rgb[1][i] + 0.114f * rgb[2][i];

    private static float ColorDistance(Workspace work, float[][] background, int i) => Math.Max(
        Math.Abs(work.R[i] - background[0][i]),
        Math.Max(Math.Abs(work.G[i] - background[1][i]), Math.Abs(work.B[i] - background[2][i])));

    private static int? MeanRgb(Workspace work, Func<int, bool> take)
    {
        long r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < work.Count; i++)
        {
            if (!take(i)) continue;
            r += work.R[i];
            g += work.G[i];
            b += work.B[i];
            count++;
        }
        if (count == 0) return null;
        return (int)(r / count) << 16 | (int)(g / count) << 8 | (int)(b / count);
    }

    private sealed class Workspace
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Count;
        public readonly byte[] R;
        public readonly byte[] G;
        public readonly byte[] B;
        public readonly float[] L;

        public Workspace(OcrBitmap frame, RectPx area)
        {
            Width = area.Width;
            Height = area.Height;
            Count = Width * Height;
            R = new byte[Count];
            G = new byte[Count];
            B = new byte[Count];
            L = new float[Count];
            for (var y = 0; y < Height; y++)
            {
                var source = (area.Y + y) * frame.Stride + area.X * 4;
                var target = y * Width;
                for (var x = 0; x < Width; x++)
                {
                    var p = source + x * 4;
                    var i = target + x;
                    B[i] = frame.PixelsBgra32[p];
                    G[i] = frame.PixelsBgra32[p + 1];
                    R[i] = frame.PixelsBgra32[p + 2];
                    L[i] = 0.299f * R[i] + 0.587f * G[i] + 0.114f * B[i];
                }
            }
        }

        public float[][]? SmoothExcluding(bool[] excluded, int factor)
        {
            if (factor <= 1) return InpaintExcluding(excluded);
            var w = (Width + factor - 1) / factor;
            var h = (Height + factor - 1) / factor;
            var channels = new[] { new float[w * h], new float[w * h], new float[w * h] };
            var counts = new int[w * h];
            for (var y = 0; y < Height; y++)
            {
                var row = y / factor * w;
                for (var x = 0; x < Width; x++)
                {
                    var i = y * Width + x;
                    if (excluded[i]) continue;
                    var cell = row + x / factor;
                    channels[0][cell] += R[i];
                    channels[1][cell] += G[i];
                    channels[2][cell] += B[i];
                    counts[cell]++;
                }
            }
            var known = new bool[w * h];
            var any = false;
            for (var cell = 0; cell < counts.Length; cell++)
            {
                if (counts[cell] == 0) continue;
                known[cell] = true;
                any = true;
                channels[0][cell] /= counts[cell];
                channels[1][cell] /= counts[cell];
                channels[2][cell] /= counts[cell];
            }
            if (!any) return null;
            PushPull.Fill(channels, known, w, h);

            var result = new[] { new float[Count], new float[Count], new float[Count] };
            for (var y = 0; y < Height; y++)
            {
                var fy = Math.Clamp((y + 0.5f) / factor - 0.5f, 0, h - 1);
                var y0 = (int)fy;
                var y1 = Math.Min(h - 1, y0 + 1);
                var ty = fy - y0;
                for (var x = 0; x < Width; x++)
                {
                    var fx = Math.Clamp((x + 0.5f) / factor - 0.5f, 0, w - 1);
                    var x0 = (int)fx;
                    var x1 = Math.Min(w - 1, x0 + 1);
                    var tx = fx - x0;
                    var i = y * Width + x;
                    for (var c = 0; c < 3; c++)
                    {
                        var p = channels[c];
                        var top = p[y0 * w + x0] + (p[y0 * w + x1] - p[y0 * w + x0]) * tx;
                        var bottom = p[y1 * w + x0] + (p[y1 * w + x1] - p[y1 * w + x0]) * tx;
                        result[c][i] = top + (bottom - top) * ty;
                    }
                }
            }
            return result;
        }

        public float[][]? InpaintExcluding(bool[] excluded)
        {
            var channels = new[] { new float[Count], new float[Count], new float[Count] };
            var known = new bool[Count];
            var any = false;
            for (var i = 0; i < Count; i++)
            {
                if (excluded[i]) continue;
                known[i] = true;
                channels[0][i] = R[i];
                channels[1][i] = G[i];
                channels[2][i] = B[i];
                any = true;
            }
            if (!any) return null;
            PushPull.Fill(channels, known, Width, Height);
            return channels;
        }
    }
}

internal static class PushPull
{
    public static void Fill(float[][] channels, bool[] known, int width, int height)
    {
        var levels = new List<(float[][] Channels, bool[] Known, int Width, int Height)> { (channels, known, width, height) };
        while (true)
        {
            var (c, k, w, h) = levels[^1];
            if (Array.TrueForAll(k, static v => v) || (w == 1 && h == 1)) break;
            var nw = (w + 1) / 2;
            var nh = (h + 1) / 2;
            var nc = new float[c.Length][];
            for (var ch = 0; ch < c.Length; ch++) nc[ch] = new float[nw * nh];
            var nk = new bool[nw * nh];
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    var count = 0;
                    float s0 = 0, s1 = 0, s2 = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var sy = 2 * y + dy;
                        if (sy >= h) continue;
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var sx = 2 * x + dx;
                            if (sx >= w) continue;
                            var i = sy * w + sx;
                            if (!k[i]) continue;
                            s0 += c[0][i];
                            s1 += c[1][i];
                            s2 += c[2][i];
                            count++;
                        }
                    }
                    if (count == 0) continue;
                    var j = y * nw + x;
                    nc[0][j] = s0 / count;
                    nc[1][j] = s1 / count;
                    nc[2][j] = s2 / count;
                    nk[j] = true;
                }
            }
            levels.Add((nc, nk, nw, nh));
        }

        for (var level = levels.Count - 2; level >= 0; level--)
        {
            var (c, k, w, h) = levels[level];
            var (pc, _, pw, ph) = levels[level + 1];
            for (var y = 0; y < h; y++)
            {
                var fy = Math.Clamp((y + 0.5) / 2 - 0.5, 0, ph - 1);
                var y0 = (int)fy;
                var y1 = Math.Min(ph - 1, y0 + 1);
                var ty = (float)(fy - y0);
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (k[i]) continue;
                    var fx = Math.Clamp((x + 0.5) / 2 - 0.5, 0, pw - 1);
                    var x0 = (int)fx;
                    var x1 = Math.Min(pw - 1, x0 + 1);
                    var tx = (float)(fx - x0);
                    for (var ch = 0; ch < c.Length; ch++)
                    {
                        var p = pc[ch];
                        var top = p[y0 * pw + x0] + (p[y0 * pw + x1] - p[y0 * pw + x0]) * tx;
                        var bottom = p[y1 * pw + x0] + (p[y1 * pw + x1] - p[y1 * pw + x0]) * tx;
                        c[ch][i] = top + (bottom - top) * ty;
                    }
                }
            }
            if (level > 0) Array.Fill(k, true);
        }
    }
}

internal static class Morphology
{
    public static float[] MinFilter(float[] source, int width, int height, int size) => Filter(source, width, height, size, max: false);

    public static float[] MaxFilter(float[] source, int width, int height, int size) => Filter(source, width, height, size, max: true);

    private static float[] Filter(float[] source, int width, int height, int size, bool max)
    {
        var radius = size / 2;
        var horizontal = new float[source.Length];
        var deque = new int[Math.Max(width, height)];
        for (var y = 0; y < height; y++)
        {
            Slide(source, horizontal, y * width, 1, width, radius, max, deque);
        }
        var result = new float[source.Length];
        for (var x = 0; x < width; x++)
        {
            Slide(horizontal, result, x, width, height, radius, max, deque);
        }
        return result;
    }

    private static void Slide(float[] source, float[] target, int offset, int step, int length, int radius, bool max, int[] deque)
    {
        var head = 0;
        var tail = 0;
        for (var i = 0; i < length + radius; i++)
        {
            if (i < length)
            {
                var value = source[offset + i * step];
                while (tail > head)
                {
                    var last = source[offset + deque[tail - 1] * step];
                    if (max ? last <= value : last >= value) tail--;
                    else break;
                }
                deque[tail++] = i;
            }
            var center = i - radius;
            if (center < 0) continue;
            while (deque[head] < center - radius) head++;
            target[offset + center * step] = source[offset + deque[head] * step];
        }
    }

    public static bool[] Dilate(bool[] mask, int width, int height, int radius)
    {
        if (radius <= 0) return (bool[])mask.Clone();
        var distance = new int[mask.Length];
        var horizontal = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var last = int.MinValue / 2;
            for (var x = 0; x < width; x++)
            {
                if (mask[row + x]) last = x;
                distance[row + x] = x - last;
            }
            last = int.MaxValue / 2;
            for (var x = width - 1; x >= 0; x--)
            {
                if (mask[row + x]) last = x;
                horizontal[row + x] = Math.Min(distance[row + x], last - x) <= radius;
            }
        }
        var result = new bool[mask.Length];
        for (var x = 0; x < width; x++)
        {
            var last = int.MinValue / 2;
            for (var y = 0; y < height; y++)
            {
                if (horizontal[y * width + x]) last = y;
                distance[y * width + x] = y - last;
            }
            last = int.MaxValue / 2;
            for (var y = height - 1; y >= 0; y--)
            {
                if (horizontal[y * width + x]) last = y;
                result[y * width + x] = Math.Min(distance[y * width + x], last - y) <= radius;
            }
        }
        return result;
    }

    public static int Perimeter(bool[] mask, int width, int height)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (!mask[i]) continue;
                if (x == 0 || !mask[i - 1] || x == width - 1 || !mask[i + 1]
                    || y == 0 || !mask[i - width] || y == height - 1 || !mask[i + width])
                    count++;
            }
        }
        return count;
    }
}
