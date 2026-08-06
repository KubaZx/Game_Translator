using System.Runtime.InteropServices.WindowsRuntime;
using GameTranslatorOverlay.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrResult = GameTranslatorOverlay.Core.Ocr.OcrResult;
using OcrLine = GameTranslatorOverlay.Core.Ocr.OcrLine;
using OcrWord = GameTranslatorOverlay.Core.Ocr.OcrWord;

namespace GameTranslatorOverlay.App.Ocr;

/// <summary>
/// Systemowe OCR Windows (Windows.Media.Ocr). Zero pobierania modeli —
/// korzysta z pakietów językowych zainstalowanych w systemie.
/// Uwaga: to API nie zwraca poziomu pewności rozpoznania.
/// </summary>
public sealed class WindowsOcrProvider : IOcrProvider
{
    public string Name => "Windows OCR";

    public int MaxImageDimension { get; } = (int)OcrEngine.MaxImageDimension;

    public IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(static l => l.LanguageTag).ToList();

    public bool IsLanguageAvailable(string languageTag) => FindLanguage(languageTag) is not null;

    private static Language? FindLanguage(string languageTag)
    {
        var available = OcrEngine.AvailableRecognizerLanguages;

        var exact = available.FirstOrDefault(l => l.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // „en” dopasowuje „en-US”, „en-GB” itd. — i odwrotnie.
        var requestedPrimary = languageTag.Split('-')[0];
        return available.FirstOrDefault(l =>
            l.LanguageTag.Split('-')[0].Equals(requestedPrimary, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
    {
        var language = FindLanguage(languageTag) ?? throw new OcrLanguageNotAvailableException(languageTag);
        var engine = OcrEngine.TryCreateFromLanguage(language) ?? throw new OcrLanguageNotAvailableException(languageTag);

        if (bitmap.Width > MaxImageDimension || bitmap.Height > MaxImageDimension)
        {
            throw new ArgumentException(
                $"Obraz {bitmap.Width}×{bitmap.Height} px przekracza limit silnika OCR ({MaxImageDimension} px). " +
                "Zmniejsz obraz przed rozpoznaniem.", nameof(bitmap));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bitmap.PixelsBgra32.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height);

        var recognized = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);

        var lines = new List<OcrLine>(recognized.Lines.Count);
        foreach (var line in recognized.Lines)
        {
            var words = line.Words
                .Select(static w => new OcrWord(w.Text, ToRect(w.BoundingRect)))
                .ToList();
            if (words.Count == 0) continue;

            var box = words.Aggregate(default(RectPx), static (acc, w) => acc.Union(w.Box));
            lines.Add(new OcrLine(line.Text, box, words));
        }

        return new OcrResult(lines, language.LanguageTag);
    }

    private static RectPx ToRect(Windows.Foundation.Rect rect) => new(
        (int)Math.Floor(rect.X),
        (int)Math.Floor(rect.Y),
        (int)Math.Ceiling(rect.Width),
        (int)Math.Ceiling(rect.Height));
}
