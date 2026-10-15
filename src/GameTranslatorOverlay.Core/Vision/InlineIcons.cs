using System.Text;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

public static class InlineIcons
{
    public const string Marker = "[X]";
    public const string SymbolReplacement = "▢";

    private const int EdgeThreshold = 40;

    public static bool IsMarker(string token) => string.Equals(token.Trim(), Marker, StringComparison.Ordinal);

    public static int CountMarkers(string text)
    {
        var count = 0;
        for (var at = text.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Marker, at + Marker.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    public static string Replace(string text, string replacement)
    {
        if (!text.Contains(Marker, StringComparison.Ordinal)) return text;
        var replaced = text.Replace(Marker, replacement, StringComparison.Ordinal);
        if (replacement.Length > 0) return replaced;
        var builder = new StringBuilder(replaced.Length);
        foreach (var line in replaced.Split('\n'))
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.AppendJoin(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return builder.ToString();
    }

    public const char Reserve = ' ';

    public sealed record Slot(int Start, int Length, int Icon, bool Spaced);

    public static (string Text, IReadOnlyList<Slot> Slots) Layout(string text, int icons, Func<int, bool, int> width)
    {
        if (!text.Contains(Marker, StringComparison.Ordinal)) return (text, []);
        var builder = new StringBuilder(text.Length + icons * 8);
        var slots = new List<Slot>();
        var position = 0;
        while (position < text.Length)
        {
            var at = text.IndexOf(Marker, position, StringComparison.Ordinal);
            if (at < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }
            var end = at + Marker.Length;
            var head = at;
            if (head > position && text[head - 1] == ' ') head--;
            builder.Append(text, position, head - position);
            if (slots.Count < icons)
            {
                var start = builder.Length;
                var spaced = head < at;
                var length = Math.Max(1, width(slots.Count, spaced));
                builder.Append(Reserve, length);
                slots.Add(new Slot(start, length, slots.Count, spaced));
            }
            else if (head == at && end < text.Length && text[end] == ' ')
            {
                end++;
            }
            position = end;
        }
        return (builder.ToString(), slots);
    }

    public static IReadOnlyList<OcrLine> Annotate(OcrBitmap frame, IReadOnlyList<OcrLine> lines, RectPx ocrRegion, double scaleBack)
    {
        if (frame.PixelsBgra32 is null || lines.Count == 0 || scaleBack <= 0) return lines;
        var occupied = lines.SelectMany(static l => l.Words).Select(static w => w.Box).ToList();
        List<OcrLine>? result = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var annotated = AnnotateLine(frame, line, occupied, ocrRegion, scaleBack);
            if (ReferenceEquals(annotated, line))
            {
                result?.Add(line);
                continue;
            }
            result ??= lines.Take(index).ToList();
            result.Add(annotated);
        }
        return result is null ? lines : result;
    }

    private static OcrLine AnnotateLine(OcrBitmap frame, OcrLine line, List<RectPx> occupied, RectPx ocrRegion, double scaleBack)
    {
        if (line.Words.Count == 0 || line.Words.Any(static w => IsMarker(w.Text))) return line;
        var heights = line.Words.Select(static w => w.Box.Height).OrderBy(static h => h).ToList();
        double lineHeight = heights[heights.Count / 2];
        if (lineHeight < 8) return line;

        var words = line.Words.OrderBy(static w => w.Box.X).ToList();
        if (GlyphCoverBuilder.IsIconToken(words[0].Text)) return line;
        var inserted = new List<(int After, RectPx Box)>();
        for (var i = 0; i + 1 < words.Count; i++)
        {
            var gap = words[i + 1].Box.X - words[i].Box.Right;
            if (gap < lineHeight * 1.1) continue;
            var area = new RectPx(
                words[i].Box.Right + (int)Math.Round(lineHeight * 0.1),
                line.Box.Y,
                gap - (int)Math.Round(lineHeight * 0.2),
                line.Box.Height);
            if (Find(frame, area, lineHeight, line.Box, occupied, ocrRegion, scaleBack, trailing: false) is { } icon)
                inserted.Add((i, icon));
        }
        var last = words[^1].Box;
        var tail = new RectPx(last.Right + (int)Math.Round(lineHeight * 0.1), line.Box.Y, (int)Math.Round(lineHeight * 4.5), line.Box.Height);
        if (Find(frame, tail, lineHeight, line.Box, occupied, ocrRegion, scaleBack, trailing: true) is { } trailingIcon)
            inserted.Add((words.Count - 1, trailingIcon));
        if (inserted.Count == 0) return line;

        var annotatedWords = new List<OcrWord>(words.Count + inserted.Count);
        for (var i = 0; i < words.Count; i++)
        {
            annotatedWords.Add(words[i]);
            foreach (var (after, box) in inserted)
                if (after == i) annotatedWords.Add(new OcrWord(Marker, box));
        }
        foreach (var (_, box) in inserted) occupied.Add(box);
        return new OcrLine(string.Join(' ', annotatedWords.Select(static w => w.Text)), line.Box, annotatedWords);
    }

    private static RectPx? Find(
        OcrBitmap frame, RectPx windowArea, double lineHeight, RectPx lineBox, List<RectPx> occupied, RectPx ocrRegion, double scaleBack,
        bool trailing)
    {
        if (windowArea.Width < lineHeight * 0.5) return null;
        var bandTop = lineBox.Y - (int)Math.Round(lineHeight * 0.35);
        var bandBottom = lineBox.Bottom + (int)Math.Round(lineHeight * 0.35);
        var search = new RectPx(windowArea.X, bandTop, windowArea.Width, bandBottom - bandTop);
        var sample = ToSample(search, ocrRegion, scaleBack).Intersect(new RectPx(0, 0, frame.Width, frame.Height));
        var h = lineHeight / scaleBack;
        if (sample.Width < h * 0.5 || sample.Height < h * 0.5) return null;

        var columns = new int[sample.Width];
        for (var y = sample.Y; y < sample.Bottom - 1; y++)
            for (var x = sample.X; x < sample.Right - 1; x++)
                if (IsEdge(frame, x, y)) columns[x - sample.X]++;

        var minimum = Math.Max(1, (int)Math.Round(h * 0.06));
        var quiet = Math.Max(2, (int)Math.Round(h * 0.3));
        var bridge = Math.Max(1, (int)Math.Round(h * 0.25));
        var start = Array.FindIndex(columns, c => c >= minimum);
        if (start < 0) return null;
        if (trailing && start > h * 1.0) return null;
        var end = start;
        var silent = 0;
        for (var x = start + 1; x < columns.Length; x++)
        {
            if (columns[x] >= minimum)
            {
                end = x;
                silent = 0;
            }
            else if (++silent > bridge) break;
        }
        if (columns.Length - 1 - end < quiet)
        {
            if (trailing) return null;
            if (columns.Length - 1 - end < Math.Max(1, quiet / 3)) return null;
        }
        var width = end - start + 1;
        if (width < h * 0.9 || width > h * 4.0) return null;

        var x0 = sample.X + start;
        var x1 = sample.X + end + 1;
        var reach = (int)Math.Round(h * 0.7);
        var top = Extend(frame, x0, x1, sample.Y, -1, Math.Max(0, sample.Y - reach), minimum);
        var bottom = Extend(frame, x0, x1, sample.Bottom - 1, 1, Math.Min(frame.Height - 1, sample.Bottom - 1 + reach), minimum) + 1;
        var rows = 0;
        var edges = 0;
        var firstRow = -1;
        var lastRow = -1;
        for (var y = top; y < bottom - 1; y++)
        {
            var count = 0;
            for (var x = x0; x < x1 - 1 && x < frame.Width - 1; x++)
                if (IsEdge(frame, x, y)) count++;
            edges += count;
            if (count < minimum) continue;
            if (firstRow < 0) firstRow = y;
            lastRow = y;
            rows++;
        }
        if (firstRow < 0) return null;
        var height = lastRow - firstRow + 1;
        if (height < h * 1.1 || height > h * 2.4 || width < height * 0.6) return null;
        var innerLeft = x0 + width / 4;
        var innerRight = x1 - width / 4;
        var innerTop = firstRow + height / 4;
        var innerBottom = lastRow - height / 4;
        var inner = 0;
        for (var y = innerTop; y < innerBottom && y < frame.Height - 1; y++)
            for (var x = innerLeft; x < innerRight && x < frame.Width - 1; x++)
                if (IsEdge(frame, x, y)) inner++;
        if (inner < edges * 0.05) return null;
        var center = (firstRow + lastRow) / 2.0;
        var lineCenter = (ToSample(lineBox, ocrRegion, scaleBack).Y + ToSample(lineBox, ocrRegion, scaleBack).Bottom) / 2.0;
        if (Math.Abs(center - lineCenter) > h * 0.6) return null;
        if (edges < width * height * 0.04) return null;

        var icon = new RectPx(
            ocrRegion.X + (int)Math.Floor(x0 * scaleBack),
            ocrRegion.Y + (int)Math.Floor(firstRow * scaleBack),
            Math.Max(1, (int)Math.Ceiling(width * scaleBack)),
            Math.Max(1, (int)Math.Ceiling(height * scaleBack)));
        foreach (var other in occupied)
        {
            var overlap = other.Intersect(icon);
            if (!overlap.IsEmpty && (long)overlap.Width * overlap.Height * 5 > (long)Math.Min(other.Width * other.Height, icon.Width * icon.Height))
                return null;
        }
        return icon;
    }

    private static int Extend(OcrBitmap frame, int x0, int x1, int from, int direction, int limit, int minimum)
    {
        var edge = from;
        var silent = 0;
        for (var y = from + direction; direction < 0 ? y >= limit : y <= limit; y += direction)
        {
            if (y < 0 || y >= frame.Height - 1) break;
            var count = 0;
            for (var x = x0; x < x1 - 1 && x < frame.Width - 1; x++)
                if (IsEdge(frame, x, y)) count++;
            if (count >= minimum)
            {
                edge = y;
                silent = 0;
            }
            else if (++silent >= 3) break;
        }
        return edge;
    }

    private static bool IsEdge(OcrBitmap frame, int x, int y)
    {
        var o = y * frame.Stride + x * 4;
        var l = Luma(frame.PixelsBgra32, o);
        return Math.Abs(Luma(frame.PixelsBgra32, o + 4) - l) + Math.Abs(Luma(frame.PixelsBgra32, o + frame.Stride) - l) > EdgeThreshold;
    }

    private static int Luma(byte[] pixels, int o) => (pixels[o] * 29 + pixels[o + 1] * 150 + pixels[o + 2] * 77) >> 8;

    private static RectPx ToSample(RectPx windowBox, RectPx ocrRegion, double scaleBack) => new(
        (int)Math.Floor((windowBox.X - ocrRegion.X) / scaleBack),
        (int)Math.Floor((windowBox.Y - ocrRegion.Y) / scaleBack),
        Math.Max(1, (int)Math.Ceiling(windowBox.Width / scaleBack)),
        Math.Max(1, (int)Math.Ceiling(windowBox.Height / scaleBack)));
}
