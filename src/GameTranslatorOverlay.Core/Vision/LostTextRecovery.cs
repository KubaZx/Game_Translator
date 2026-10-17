using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Vision;

public sealed record LostTextFound(TextBlock Block, GlyphCover Cover, int LineHeight);

public sealed record LostTextResumed(
    GlyphCover Cover, RectPx Box, GlyphMatch Match, OcrBitmap Region, RectPx Area, int VelocityX, int VelocityY, bool Rebuilt);

public static class LostTextRecovery
{
    public const double MaxSizeRatio = 2.0;
    private const int EdgeMargin = 2;

    public static bool ShouldRead(bool moving, bool sceneCut, bool hasTrack, bool screenFallback, bool travelling, bool lettersGone) =>
        moving && !sceneCut && hasTrack && !screenFallback && (travelling || !lettersGone);

    public static LostTextResumed? Resume(
        GlyphCover lostCover, RectPx lostBox, LostTextFound found, Func<RectPx, OcrBitmap?> regionOf, RectPx frameRect,
        double maxShift, bool patch)
    {
        var foundX = found.Block.Box.X + found.Block.Box.Width / 2.0;
        var foundY = found.Block.Box.Y + found.Block.Box.Height / 2.0;
        var shiftX = foundX - (lostBox.X + lostBox.Width / 2.0);
        var shiftY = foundY - (lostBox.Y + lostBox.Height / 2.0);
        return Follow(lostCover, lostBox, 2 * shiftX, 2 * shiftY, rebuilt: false)
            ?? Follow(found.Cover, found.Block.Box, shiftX, shiftY, rebuilt: true);

        LostTextResumed? Follow(GlyphCover cover, RectPx box, double moveX, double moveY, bool rebuilt)
        {
            if (cover.Track is not { } track || track.Step <= 0) return null;
            var plan = GlyphFollow.Plan(track, maxShift, isMoving: true, fresh: false,
                (int)Math.Round(moveX / track.Step), (int)Math.Round(moveY / track.Step), moving: true);
            var area = GlyphFollow.SearchArea(track, plan).Intersect(frameRect);
            if (area.IsEmpty || regionOf(area) is not { } region) return null;
            var match = GlyphFollow.Locate(track, new GlyphField(region, area.X, area.Y), plan, refreshOnly: false);
            if (match is not { IsConfident: true } confident || GlyphFollow.Follow(cover, match, region, area, patch) is not { Track: { } movedTrack } moved)
                return null;
            var next = GlyphFollow.FollowBox(box, cover, moved);
            var nextX = next.X + next.Width / 2.0;
            var nextY = next.Y + next.Height / 2.0;
            if (DistanceToSegment(nextX, nextY, foundX, foundY, foundX + shiftX, foundY + shiftY) > maxShift) return null;
            var velocityX = nextX - foundX;
            var velocityY = nextY - foundY;
            return new LostTextResumed(moved, next, confident, region, area,
                (int)Math.Round(velocityX / movedTrack.Step), (int)Math.Round(velocityY / movedTrack.Step), rebuilt);
        }
    }

    private static double DistanceToSegment(double x, double y, double ax, double ay, double bx, double by)
    {
        var lengthSquared = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        var t = lengthSquared <= 0 ? 0 : Math.Clamp(((x - ax) * (bx - ax) + (y - ay) * (by - ay)) / lengthSquared, 0, 1);
        var px = ax + t * (bx - ax);
        var py = ay + t * (by - ay);
        return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
    }

    public static RectPx ReadArea(RectPx box, double shiftX, double shiftY, double radius, RectPx frame) =>
        box.Offset((int)Math.Round(shiftX), (int)Math.Round(shiftY)).Inflate((int)Math.Ceiling(Math.Max(0, radius))).Intersect(frame);

    public static IReadOnlyList<OcrLine> ToWindow(IReadOnlyList<OcrLine> lines, RectPx area, double scaleBack)
    {
        if (Math.Abs(scaleBack - 1.0) <= 0.001 && area.X == 0 && area.Y == 0) return lines;
        var scale = Math.Abs(scaleBack - 1.0) > 0.001;
        return lines.Select(line => new OcrLine(
                line.Text,
                Place(line.Box),
                line.Words.Select(w => new OcrWord(w.Text, Place(w.Box))).ToList()))
            .ToList();

        RectPx Place(RectPx box) => (scale ? box.Scale(scaleBack) : box).Offset(area.X, area.Y);
    }

    public static IReadOnlyList<TextBlock> Blocks(
        OcrBitmap image, IReadOnlyList<OcrLine> lines, RectPx area, double scaleBack, RectPx frameRect,
        IReadOnlyList<RelativeRegion> ignore, bool detectIcons)
    {
        var placed = ToWindow(lines, area, scaleBack);
        if (ignore.Count > 0 && frameRect.Width > 0 && frameRect.Height > 0)
        {
            placed = placed.Where(line =>
            {
                var relativeX = (line.Box.X + line.Box.Width / 2.0) / frameRect.Width;
                var relativeY = (line.Box.Y + line.Box.Height / 2.0) / frameRect.Height;
                return !ignore.Any(region => region.Contains(relativeX, relativeY));
            }).ToList();
        }
        if (placed.Count == 0) return [];
        if (detectIcons) placed = InlineIcons.Annotate(image, placed, area, scaleBack);
        return TextBlockSplitter.SplitLabels(TextBlockGrouper.Group(placed),
            box => BlockColorSampler.SampleColors(image.PixelsBgra32, image.Width, image.Height, image.Stride,
                GlyphCoverBuilder.ToSampleBox(box, area, scaleBack)).TextRgb);
    }

    public static TextBlock? FindSame(
        string sourceText, RectPx previous, IReadOnlyList<TextBlock> blocks, RectPx predicted, RectPx area, RectPx frameRect)
    {
        TextBlock? best = null;
        var bestDistance = double.MaxValue;
        foreach (var block in blocks)
        {
            if (!Inside(block.Box, area, frameRect) || !SimilarSize(block.Box, previous)
                || !LiveReadingStabilizer.IsSameText(sourceText, block.Text)) continue;
            var dx = block.Box.X + block.Box.Width / 2.0 - (predicted.X + predicted.Width / 2.0);
            var dy = block.Box.Y + block.Box.Height / 2.0 - (predicted.Y + predicted.Height / 2.0);
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = block;
        }
        return best;
    }

    public static LostTextFound? Rebuild(OcrBitmap image, TextBlock block, RectPx area, double scaleBack, bool patch)
    {
        var imageRect = new RectPx(0, 0, image.Width, image.Height);
        var sampleBox = GlyphCoverBuilder.ToSampleBox(GlyphCoverBuilder.CoverWindowBox(block), area, scaleBack);
        var sampleLines = block.Lines.Select(l => GlyphCoverBuilder.ToSampleBox(l.Box, area, scaleBack)).ToList();
        var signature = GlyphCoverBuilder.Signature(image, GlyphCoverBuilder.SignatureRegion(sampleBox, sampleLines, imageRect));
        if (GlyphCoverBuilder.BuildForBlock(image, block, area, scaleBack, soft: false, patch) is not { Track: not null } built) return null;
        return new LostTextFound(block, built with { Signature = signature, Anchor = block.Box }, TextBlockMetrics.MedianLineHeight(block));
    }

    private static bool Inside(RectPx box, RectPx area, RectPx frameRect)
    {
        if (box.IsEmpty || box.Intersect(area) != box) return false;
        return (box.X - area.X >= EdgeMargin || area.X <= frameRect.X)
            && (area.Right - box.Right >= EdgeMargin || area.Right >= frameRect.Right)
            && (box.Y - area.Y >= EdgeMargin || area.Y <= frameRect.Y)
            && (area.Bottom - box.Bottom >= EdgeMargin || area.Bottom >= frameRect.Bottom);
    }

    private static bool SimilarSize(RectPx box, RectPx previous)
    {
        if (previous.IsEmpty) return true;
        static bool Within(double a, double b) => a > 0 && b > 0 && a / b <= MaxSizeRatio && b / a <= MaxSizeRatio;
        return Within(box.Width, previous.Width) && Within(box.Height, previous.Height);
    }
}

public sealed class LostTextReads<T>(int maxPerSecond, int maxPerBlock, TimeSpan maxWait)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, (T Read, TimeSpan Started)> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TimeSpan>> _blockStarts = new(StringComparer.Ordinal);
    private readonly Queue<TimeSpan> _started = new();

    public int Count => _pending.Count;

    public IReadOnlyCollection<string> Keys => _pending.Keys;

    public bool IsPending(string key) => _pending.ContainsKey(key);

    public bool CanBegin(string key, TimeSpan now)
    {
        if (_pending.ContainsKey(key)) return false;
        Expire(_started, now);
        if (_started.Count >= maxPerSecond) return false;
        if (!_blockStarts.TryGetValue(key, out var starts)) return true;
        Expire(starts, now);
        return starts.Count < maxPerBlock;
    }

    public bool TryBegin(string key, TimeSpan now, Func<T> start)
    {
        if (!CanBegin(key, now)) return false;
        _pending[key] = (start(), now);
        if (!_blockStarts.TryGetValue(key, out var starts)) _blockStarts[key] = starts = new Queue<TimeSpan>();
        starts.Enqueue(now);
        _started.Enqueue(now);
        if (_blockStarts.Count > 64)
        {
            foreach (var (block, queue) in _blockStarts.ToList())
            {
                Expire(queue, now);
                if (queue.Count == 0) _blockStarts.Remove(block);
            }
        }
        return true;
    }

    private static void Expire(Queue<TimeSpan> starts, TimeSpan now)
    {
        while (starts.Count > 0 && now - starts.Peek() >= Window) starts.Dequeue();
    }

    public bool TryGet(string key, out T read, out TimeSpan started)
    {
        if (_pending.TryGetValue(key, out var entry))
        {
            read = entry.Read;
            started = entry.Started;
            return true;
        }
        read = default!;
        started = default;
        return false;
    }

    public bool IsExpired(string key, TimeSpan now) => _pending.TryGetValue(key, out var entry) && now - entry.Started > maxWait;

    public bool Remove(string key, out T read)
    {
        if (_pending.Remove(key, out var entry))
        {
            read = entry.Read;
            return true;
        }
        read = default!;
        return false;
    }

    public IReadOnlyList<T> Clear()
    {
        var reads = _pending.Values.Select(static p => p.Read).ToList();
        _pending.Clear();
        return reads;
    }
}
