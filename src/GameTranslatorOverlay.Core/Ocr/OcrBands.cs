namespace GameTranslatorOverlay.Core.Ocr;

public readonly record struct OcrBand(RectPx Rect, int OwnTop, int OwnBottom);

public static class OcrBands
{
    public const long MinimumRetryPixels = 1_400_000;

    public static IReadOnlyList<int> RetryCounts { get; } = [2, 4];

    public static bool ShouldRetry(int width, int height, int lineCount) =>
        lineCount == 0 && (long)width * height >= MinimumRetryPixels;

    public static IReadOnlyList<OcrBand> Plan(int width, int height, int count)
    {
        if (width <= 0 || height <= 0) return [];
        count = Math.Clamp(count, 1, Math.Max(1, height / 64));
        if (count == 1) return [new OcrBand(new RectPx(0, 0, width, height), 0, height)];
        var overlap = Math.Max(64, (int)Math.Round(height * (count <= 2 ? 0.12 : 0.08)));
        var step = (height - overlap) / (double)count;
        var bands = new List<OcrBand>(count);
        for (var k = 0; k < count; k++)
        {
            var top = (int)Math.Round(k * step);
            var bottom = k == count - 1 ? height : Math.Min(height, (int)Math.Round((k + 1) * step + overlap));
            var ownTop = k == 0 ? 0 : (int)Math.Round(k * step + overlap / 2.0);
            var ownBottom = k == count - 1 ? height : (int)Math.Round((k + 1) * step + overlap / 2.0);
            bands.Add(new OcrBand(new RectPx(0, top, width, bottom - top), ownTop, ownBottom));
        }
        return bands;
    }

    public static OcrBitmap Crop(OcrBitmap bitmap, RectPx rect)
    {
        rect = rect.Intersect(new RectPx(0, 0, bitmap.Width, bitmap.Height));
        var stride = rect.Width * 4;
        var pixels = new byte[stride * rect.Height];
        for (var y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(bitmap.PixelsBgra32, (rect.Y + y) * bitmap.Stride + rect.X * 4, pixels, y * stride, stride);
        return new OcrBitmap(pixels, rect.Width, rect.Height, stride);
    }

    public static IReadOnlyList<OcrLine> Merge(IEnumerable<(OcrBand Band, IReadOnlyList<OcrLine> Lines)> parts)
    {
        var result = new List<OcrLine>();
        foreach (var (band, lines) in parts)
        {
            foreach (var line in lines)
            {
                var moved = Offset(line, band.Rect.X, band.Rect.Y);
                var center = moved.Box.Y + moved.Box.Height / 2.0;
                if (center >= band.OwnTop && center < band.OwnBottom) result.Add(moved);
            }
        }
        return result.OrderBy(static l => l.Box.Y).ThenBy(static l => l.Box.X).ToList();
    }

    private static OcrLine Offset(OcrLine line, int dx, int dy) =>
        dx == 0 && dy == 0
            ? line
            : new OcrLine(line.Text, line.Box.Offset(dx, dy), line.Words.Select(w => w with { Box = w.Box.Offset(dx, dy) }).ToList());
}
