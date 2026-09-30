using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Znacznik kontekstu wpisu cache (<c>CachedTranslation.Context</c>): wersja formatu
/// (<c>reflow-1</c>) i opcjonalnie wynik kontroli jakości, np. <c>reflow-1;qa=numbers</c>.
/// Całe parsowanie i składanie znacznika jest tutaj — pipeline nie operuje na napisach.
/// </summary>
/// <param name="Format">Wersja formatu (np. <c>reflow-1</c>); null we wpisach sprzed znacznika.</param>
/// <param name="QualityIssues">Problemy wykryte przy zapisie; None, gdy wynik przeszedł kontrolę.</param>
/// <param name="HasQualityMarker">Czy znacznik zawiera część <c>qa=</c> (także z nieznanymi nazwami).</param>
/// <param name="QualityFinal">
/// Wynik z problemem, który już raz przetłumaczono ponownie i nadal ma problem — nie jest
/// tłumaczony kolejny raz (inaczej każde wystąpienie tekstu byłoby płatnym zapytaniem).
/// </param>
public readonly record struct TranslationCacheContext(
    string? Format,
    TranslationQualityFlags QualityIssues,
    bool HasQualityMarker,
    bool QualityFinal)
{
    private const char Separator = ';';
    private const string QualityPrefix = "qa=";
    private const string QualityFinalTag = "qa-final";

    public static TranslationCacheContext Parse(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return default;

        string? format = null;
        var issues = TranslationQualityFlags.None;
        var hasQuality = false;
        var final = false;
        var parts = context.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.StartsWith(QualityPrefix, StringComparison.OrdinalIgnoreCase))
            {
                hasQuality = true;
                issues |= TranslationQualityGate.FromMarker(part[QualityPrefix.Length..]);
            }
            else if (part.Equals(QualityFinalTag, StringComparison.OrdinalIgnoreCase))
            {
                final = true;
            }
            else if (i == 0)
            {
                format = part;
            }
        }
        return new TranslationCacheContext(format, issues, hasQuality, final);
    }

    /// <summary>Znacznik zapisywany z nowym wynikiem dostawcy.</summary>
    public static string Build(TranslationQualityFlags issues, bool final = false)
    {
        if (issues == TranslationQualityFlags.None) return TextReflow.FormatVersion;
        var marker = $"{TextReflow.FormatVersion}{Separator}{QualityPrefix}{TranslationQualityGate.ToMarker(issues)}";
        return final ? $"{marker}{Separator}{QualityFinalTag}" : marker;
    }

    /// <summary>Wpis ma zgłoszony problem jakości, którego jeszcze nie próbowano naprawić.</summary>
    public bool NeedsQualityRetry => HasQualityMarker && !QualityFinal;

    /// <summary>
    /// Czy automatyczny wpis warto przetłumaczyć ponownie: wieloliniowy tekst sprzed sklejania
    /// wierszy albo wynik z niesprawdzonym jeszcze problemem jakości.
    /// </summary>
    public static bool IsStale(string? context, string normalizedText)
    {
        var parsed = Parse(context);
        if (parsed.NeedsQualityRetry) return true;
        return normalizedText.Contains('\n') && parsed.Format != TextReflow.FormatVersion;
    }
}
