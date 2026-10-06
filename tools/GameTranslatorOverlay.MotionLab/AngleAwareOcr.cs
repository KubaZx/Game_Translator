using System.Runtime.InteropServices.WindowsRuntime;
using GameTranslatorOverlay.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrLine = GameTranslatorOverlay.Core.Ocr.OcrLine;
using OcrWord = GameTranslatorOverlay.Core.Ocr.OcrWord;

internal sealed class AngleAwareOcr
{
    private readonly OcrEngine _engine;

    public AngleAwareOcr(string languageTag)
    {
        var language = FindLanguage(languageTag) ?? throw new InvalidOperationException($"Brak pakietu Windows OCR dla „{languageTag}”.");
        _engine = OcrEngine.TryCreateFromLanguage(language) ?? throw new InvalidOperationException($"Nie udało się utworzyć silnika OCR dla „{languageTag}”.");
        LanguageTag = language.LanguageTag;
    }

    public string LanguageTag { get; }

    public static int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    public static Language? FindLanguage(string languageTag)
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        var exact = available.FirstOrDefault(l => l.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var primary = languageTag.Split('-')[0];
        return available.FirstOrDefault(l => l.LanguageTag.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(IReadOnlyList<OcrLine> Lines, double? Angle)> RecognizeAsync(OcrBitmap bitmap)
    {
        using var software = SoftwareBitmap.CreateCopyFromBuffer(
            bitmap.PixelsBgra32.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height);
        var result = await _engine.RecognizeAsync(software).AsTask().ConfigureAwait(false);
        var angle = result.TextAngle;
        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(w => new OcrWord(w.Text, Unrotate(w.BoundingRect, angle, bitmap.Width, bitmap.Height)))
                .ToList();
            if (words.Count == 0) continue;
            var box = words.Aggregate(default(RectPx), static (acc, w) => acc.Union(w.Box));
            lines.Add(new OcrLine(line.Text, box, words));
        }
        return (lines, angle);
    }

    public static RectPx Unrotate(Windows.Foundation.Rect rect, double? angle, int width, int height)
    {
        if (angle is not { } degrees || Math.Abs(degrees) < 1e-9)
        {
            return new RectPx(
                (int)Math.Floor(rect.X),
                (int)Math.Floor(rect.Y),
                (int)Math.Ceiling(rect.Width),
                (int)Math.Ceiling(rect.Height));
        }
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = width / 2.0;
        var cy = height / 2.0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[]
                 {
                     (rect.X, rect.Y), (rect.X + rect.Width, rect.Y),
                     (rect.X, rect.Y + rect.Height), (rect.X + rect.Width, rect.Y + rect.Height),
                 })
        {
            var rx = cx + (x - cx) * cos - (y - cy) * sin;
            var ry = cy + (x - cx) * sin + (y - cy) * cos;
            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
            maxX = Math.Max(maxX, rx);
            maxY = Math.Max(maxY, ry);
        }
        var left = (int)Math.Floor(minX);
        var top = (int)Math.Floor(minY);
        return new RectPx(left, top, (int)Math.Ceiling(maxX) - left, (int)Math.Ceiling(maxY) - top);
    }
}
