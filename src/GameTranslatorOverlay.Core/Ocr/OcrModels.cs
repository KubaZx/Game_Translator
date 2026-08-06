namespace GameTranslatorOverlay.Core.Ocr;

public readonly record struct RectPx(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectPx Union(RectPx other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new RectPx(x, y, right - x, bottom - y);
    }

    public bool IntersectsWith(RectPx other) =>
        !IsEmpty && !other.IsEmpty &&
        X < other.Right && other.X < Right &&
        Y < other.Bottom && other.Y < Bottom;

    public RectPx Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public RectPx Inflate(int amount) => new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);

    public RectPx Intersect(RectPx other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y ? default : new RectPx(x, y, right - x, bottom - y);
    }

    public RectPx Scale(double factor) => new(
        (int)Math.Round(X * factor),
        (int)Math.Round(Y * factor),
        (int)Math.Round(Width * factor),
        (int)Math.Round(Height * factor));
}

public sealed record OcrWord(string Text, RectPx Box);

public sealed record OcrLine(string Text, RectPx Box, IReadOnlyList<OcrWord> Words);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, string LanguageTag)
{
    public static OcrResult Empty(string languageTag) => new([], languageTag);
    public bool HasText => Lines.Count > 0;
}

public sealed record OcrBitmap(byte[] PixelsBgra32, int Width, int Height, int Stride);

public interface IOcrProvider
{
    string Name { get; }

    /// <summary>Największy obsługiwany wymiar obrazu (px). Obrazy większe trzeba zmniejszyć przed OCR.</summary>
    int MaxImageDimension { get; }

    IReadOnlyList<string> AvailableLanguages { get; }
    bool IsLanguageAvailable(string languageTag);
    Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default);
}

public sealed class OcrLanguageNotAvailableException(string languageTag) : Exception(
    $"Pakiet językowy OCR „{languageTag}” nie jest zainstalowany w systemie Windows. " +
    "Dodaj język w: Ustawienia → Czas i język → Język i region → Dodaj język.")
{
    public string LanguageTag { get; } = languageTag;
}
