using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Znacznik kontekstu wpisu cache (<c>CachedTranslation.Context</c>): wersja formatu
/// (<c>reflow-1</c>), opcjonalnie wynik kontroli jakości, np. <c>reflow-1;qa=numbers</c>, oraz płeć
/// gracza, z którą tłumaczył dostawca świadomy płci (<c>;pg=f</c> / <c>;pg=m</c>).
/// Całe parsowanie i składanie znacznika jest tutaj — pipeline nie operuje na napisach.
/// </summary>
/// <param name="Format">Wersja formatu (np. <c>reflow-1</c>); null we wpisach sprzed znacznika.</param>
/// <param name="QualityIssues">Problemy wykryte przy zapisie; None, gdy wynik przeszedł kontrolę.</param>
/// <param name="HasQualityMarker">Czy znacznik zawiera część <c>qa=</c> (także z nieznanymi nazwami).</param>
/// <param name="QualityFinal">
/// Wynik z problemem, który już raz przetłumaczono ponownie i nadal ma problem — nie jest
/// tłumaczony kolejny raz (inaczej każde wystąpienie tekstu byłoby płatnym zapytaniem).
/// </param>
/// <param name="PlayerGender">
/// Płeć gracza użyta przy tłumaczeniu (tylko dostawcy <see cref="IGenderAwareTranslationProvider"/>);
/// Unknown, gdy znacznik jej nie zawiera (stare wpisy, inni dostawcy, płeć nieznana).
/// </param>
public readonly record struct TranslationCacheContext(
    string? Format,
    TranslationQualityFlags QualityIssues,
    bool HasQualityMarker,
    bool QualityFinal,
    PlayerGender PlayerGender = PlayerGender.Unknown,
    string? Source = null)
{
    private const char Separator = ';';
    private const string QualityPrefix = "qa=";
    private const string QualityFinalTag = "qa-final";
    private const string PlayerGenderPrefix = "pg=";
    private const string SourcePrefix = "src=";

    public const string CorpusSource = "corpus";

    public bool IsFromCorpus => string.Equals(Source, CorpusSource, StringComparison.OrdinalIgnoreCase);

    public static TranslationCacheContext Parse(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return default;

        string? format = null;
        var issues = TranslationQualityFlags.None;
        var hasQuality = false;
        var final = false;
        var gender = PlayerGender.Unknown;
        string? source = null;
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
            else if (part.StartsWith(PlayerGenderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                gender = part[PlayerGenderPrefix.Length..].ToLowerInvariant() switch
                {
                    "f" => PlayerGender.Female,
                    "m" => PlayerGender.Male,
                    _ => PlayerGender.Unknown,
                };
            }
            else if (part.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                source = part[SourcePrefix.Length..] is { Length: > 0 } value ? value : null;
            }
            else if (i == 0)
            {
                format = part;
            }
        }
        return new TranslationCacheContext(format, issues, hasQuality, final, gender, source);
    }

    /// <summary>
    /// Znacznik zapisywany z nowym wynikiem dostawcy. <paramref name="playerGender"/> podaje
    /// się tylko dla dostawcy świadomego płci — Unknown nie dopisuje niczego, więc znaczniki
    /// pozostałych dostawców są takie jak dotąd.
    /// </summary>
    public static string Build(
        TranslationQualityFlags issues, bool final = false, PlayerGender playerGender = PlayerGender.Unknown,
        string? source = null)
    {
        if (source is not null && (source.Length == 0 || !source.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')))
        {
            throw new ArgumentException("Źródło wpisu może zawierać tylko litery, cyfry i myślniki.", nameof(source));
        }
        var marker = TextReflow.FormatVersion;
        if (issues != TranslationQualityFlags.None)
        {
            marker = $"{marker}{Separator}{QualityPrefix}{TranslationQualityGate.ToMarker(issues)}";
            if (final) marker = $"{marker}{Separator}{QualityFinalTag}";
        }
        marker = playerGender switch
        {
            PlayerGender.Female => $"{marker}{Separator}{PlayerGenderPrefix}f",
            PlayerGender.Male => $"{marker}{Separator}{PlayerGenderPrefix}m",
            _ => marker,
        };
        return source is null ? marker : $"{marker}{Separator}{SourcePrefix}{source}";
    }

    /// <summary>Wpis ma zgłoszony problem jakości, którego jeszcze nie próbowano naprawić.</summary>
    public bool NeedsQualityRetry => HasQualityMarker && !QualityFinal;

    /// <summary>
    /// Czy automatyczny wpis warto przetłumaczyć ponownie: wieloliniowy tekst sprzed sklejania
    /// wierszy albo wynik z niesprawdzonym jeszcze problemem jakości.
    /// </summary>
    public static bool IsStale(string? context, string normalizedText) =>
        IsStale(context, normalizedText, PlayerGender.Unknown);

    /// <summary>
    /// Jak <see cref="IsStale(string?, string)"/>, a dodatkowo: wynik przetłumaczony z inną płcią
    /// gracza (także bez żadnej — stare wpisy, inny dostawca) jest nieaktualny, gdy tekst zwraca
    /// się do gracza. <paramref name="currentGender"/> to Unknown dla dostawców nieświadomych
    /// płci — wtedy płeć nigdy nie unieważnia wpisu (ich wynik od niej nie zależy).
    /// </summary>
    public static bool IsStale(string? context, string normalizedText, PlayerGender currentGender)
    {
        var parsed = Parse(context);
        if (parsed.NeedsQualityRetry) return true;
        if (normalizedText.Contains('\n') && parsed.Format != TextReflow.FormatVersion) return true;
        return DiffersInPlayerGender(parsed, normalizedText, currentGender);
    }

    /// <summary>
    /// Czy sama płeć gracza czyni wpis nieaktualnym (niezależnie od innych powodów) — pipeline
    /// zapamiętuje takie teksty, żeby ponawiać je najwyżej raz (patrz <see cref="IsStale(string?, string, PlayerGender)"/>).
    /// </summary>
    public static bool IsStaleForPlayerGender(string? context, string normalizedText, PlayerGender currentGender) =>
        DiffersInPlayerGender(Parse(context), normalizedText, currentGender);

    private static bool DiffersInPlayerGender(TranslationCacheContext parsed, string normalizedText, PlayerGender currentGender) =>
        currentGender != PlayerGender.Unknown
        && parsed.PlayerGender != currentGender
        && PlayerGenders.AddressesPlayer(normalizedText);
}
