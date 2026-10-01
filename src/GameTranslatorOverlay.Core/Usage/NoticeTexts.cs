using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Usage;

/// <summary>
/// Polskie teksty komunikatów nakładki: krótkie (najwyżej <see cref="MaxLength"/> znaków —
/// gracz ma je przeczytać kątem oka nad grą) i bez treści z ekranu.
/// </summary>
public static class NoticeTexts
{
    public const int MaxLength = 60;

    // Nazwy dostawców są krótkie („DeepL”, „Claude”), ale nazwa trafia do komunikatu,
    // więc obcinamy ją na wszelki wypadek, żeby nie rozepchnęła paska.
    private const int MaxProviderNameLength = 16;

    public const string CacheDegraded = "⚠ Cache niedostępny — tłumaczenia nie są zapisywane";
    public const string SessionLimit = "⚠ Limit znaków tej sesji wyczerpany";
    public const string LiveStarted = "▶ Tłumaczenie na żywo włączone";
    public const string LiveStopped = "■ Tłumaczenie na żywo zatrzymane";
    public const string NoTextFound = "ℹ Nie rozpoznano tekstu w zaznaczeniu";
    public const string TranslationFailed = "⚠ Tłumaczenie nie powiodło się";

    /// <summary>Komunikat dla rodzaju błędu dostawcy (pełny opis zostaje w oknie aplikacji).</summary>
    public static string For(TranslationFailureKind kind, string? providerName)
    {
        var p = ShortName(providerName);
        var text = kind switch
        {
            TranslationFailureKind.MissingApiKey => $"⚠ Brak klucza {p}",
            TranslationFailureKind.InvalidApiKey => $"⚠ Klucz {p} został odrzucony",
            TranslationFailureKind.QuotaExceeded => $"⚠ Limit znaków {p} wyczerpany",
            TranslationFailureKind.RateLimited => $"⏳ {p} ogranicza zapytania",
            TranslationFailureKind.NetworkError => "⚠ Brak połączenia z dostawcą",
            TranslationFailureKind.Timeout => $"⏳ {p} nie odpowiada na czas",
            TranslationFailureKind.TextTooLong => $"⚠ Tekst za długi dla {p}",
            TranslationFailureKind.ServiceUnavailable => $"⚠ {p} chwilowo niedostępny",
            TranslationFailureKind.InvalidRequest => $"⚠ {p} odrzucił zapytanie",
            TranslationFailureKind.ModelNotFound => $"⚠ Model {p} niedostępny — sprawdź ustawienia",
            TranslationFailureKind.ContentRefused => $"⚠ {p} odmówił tłumaczenia fragmentu",
            TranslationFailureKind.InvalidConfiguration => $"⚠ Ustawienia {p} są niepełne",
            _ => $"⚠ Błąd tłumaczenia ({p})",
        };
        return Fit(text);
    }

    /// <summary>Pusty wynik od dostawcy.</summary>
    public static string EmptyResult(string? providerName) => Fit($"⚠ {ShortName(providerName)} zwrócił pusty wynik");

    /// <summary>„Cache-only: N tekstów bez tłumaczenia” z poprawną polską odmianą.</summary>
    public static string CacheOnlyMisses(int count)
    {
        var noun = PolishPlural(count, "tekst", "teksty", "tekstów");
        return Fit($"Cache-only: {count} {noun} bez tłumaczenia");
    }

    private static string PolishPlural(int count, string one, string few, string many)
    {
        if (count == 1) return one;
        var lastTwo = Math.Abs(count) % 100;
        var last = lastTwo % 10;
        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }

    private static string ShortName(string? providerName)
    {
        var name = providerName?.Trim();
        if (string.IsNullOrEmpty(name)) return "API";
        return name.Length <= MaxProviderNameLength ? name : name[..MaxProviderNameLength];
    }

    private static string Fit(string text) => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}

/// <summary>Gotowe komunikaty nakładki (klucz powtórzeń, waga, czas, krytyczność).</summary>
public static class OverlayNotices
{
    public static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Błędy, których bez reakcji gracza nic nie naprawi (klucz, limit) — pokazujemy je
    /// także przy nakładce schowanej skrótem i z wyższą wagą niż chwilowe problemy sieci.
    /// </summary>
    public static bool IsCritical(TranslationFailureKind kind) => kind is
        TranslationFailureKind.MissingApiKey or TranslationFailureKind.InvalidApiKey or TranslationFailureKind.QuotaExceeded;

    private static NoticeSeverity SeverityOf(TranslationFailureKind kind) => kind switch
    {
        TranslationFailureKind.MissingApiKey or TranslationFailureKind.InvalidApiKey or TranslationFailureKind.QuotaExceeded
            or TranslationFailureKind.ModelNotFound or TranslationFailureKind.InvalidConfiguration => NoticeSeverity.Error,
        _ => NoticeSeverity.Warning,
    };

    private static TimeSpan DurationOf(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Error => ErrorDuration,
        NoticeSeverity.Warning => WarningDuration,
        _ => InfoDuration,
    };

    public static OverlayNotice Failure(TranslationFailureKind kind, string? providerName)
    {
        var severity = SeverityOf(kind);
        return new OverlayNotice($"failure:{kind}", severity, NoticeTexts.For(kind, providerName), DurationOf(severity))
        {
            IsCritical = IsCritical(kind),
        };
    }

    public static OverlayNotice SessionLimit() =>
        new("issue:session-limit", NoticeSeverity.Error, NoticeTexts.SessionLimit, ErrorDuration) { IsCritical = true };

    public static OverlayNotice EmptyResult(string? providerName) =>
        new("issue:empty-result", NoticeSeverity.Warning, NoticeTexts.EmptyResult(providerName), WarningDuration);

    public static OverlayNotice CacheOnlyMisses(int count) =>
        new(OverlayNoticePolicy.CacheOnlyMissKey, NoticeSeverity.Info, NoticeTexts.CacheOnlyMisses(count), WarningDuration);

    public static OverlayNotice CacheDegraded() =>
        new("cache-degraded", NoticeSeverity.Warning, NoticeTexts.CacheDegraded, ErrorDuration);

    public static OverlayNotice LiveStarted() =>
        new("live-state", NoticeSeverity.Info, NoticeTexts.LiveStarted, InfoDuration) { IsStateChange = true };

    public static OverlayNotice LiveStopped() =>
        new("live-state", NoticeSeverity.Error, NoticeTexts.LiveStopped, InfoDuration) { IsStateChange = true, IsCritical = true };

    public static OverlayNotice NoTextFound() =>
        new("manual:no-text", NoticeSeverity.Info, NoticeTexts.NoTextFound, InfoDuration);

    public static OverlayNotice TranslationFailed() =>
        new("manual:failed", NoticeSeverity.Warning, NoticeTexts.TranslationFailed, WarningDuration);

    /// <summary>
    /// Komunikat dla pierwszego problemu w wynikach (błąd dostawcy, limit sesji, pusty
    /// wynik); null, gdy wszystko przetłumaczono. Pudła Cache-only są pomijane — zbiera je
    /// <see cref="OverlayNoticePolicy.OfferCacheOnlyMisses"/>.
    /// </summary>
    public static OverlayNotice? FromOutcomes(IEnumerable<TranslationOutcome> outcomes, string? providerName)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        foreach (var outcome in outcomes)
        {
            if (outcome.IsTranslated) continue;
            switch (outcome.Issue)
            {
                case OutcomeIssue.Provider:
                    return Failure(outcome.FailureKind ?? TranslationFailureKind.Unknown, providerName);
                case OutcomeIssue.SessionLimit:
                    return SessionLimit();
                case OutcomeIssue.EmptyResult:
                    return EmptyResult(providerName);
            }
        }
        return null;
    }

    /// <summary>
    /// Komunikat po ręcznym tłumaczeniu regionu w trybie nakładki — zamiast ciszy, gdy
    /// czegoś nie przetłumaczono: brak tekstu w zaznaczeniu, błąd dostawcy (limit, klucz),
    /// pudła Cache-only albo ogólna porażka. Null, gdy wszystkie bloki przetłumaczono.
    /// Ręczne tłumaczenie to jawna prośba gracza, więc wynik nie przechodzi przez okno
    /// powtórzeń polityki — każda próba dostaje odpowiedź — i jest oznaczony jako
    /// <see cref="OverlayNotice.IsExplicitRequest"/>: pokazuje się także wtedy, gdy gracz
    /// schował nakładkę skrótem (przy braku przetłumaczonych bloków nic jej nie odkrywa).
    /// </summary>
    public static OverlayNotice? ForManualResult(IReadOnlyList<TranslationOutcome> outcomes, string? providerName)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        return ManualResultNotice(outcomes, providerName) is { } notice ? notice with { IsExplicitRequest = true } : null;
    }

    /// <summary>
    /// Ogólna porażka ręcznego tłumaczenia (wyjątek OCR, bazy itp.) — jak
    /// <see cref="ForManualResult"/> przechodzi przez ukrycie nakładki skrótem.
    /// </summary>
    public static OverlayNotice ManualTranslationFailed() => TranslationFailed() with { IsExplicitRequest = true };

    private static OverlayNotice? ManualResultNotice(IReadOnlyList<TranslationOutcome> outcomes, string? providerName)
    {
        if (outcomes.Count == 0) return NoTextFound();
        if (FromOutcomes(outcomes, providerName) is { } failure) return failure;
        var misses = CacheOnlyMissTexts(outcomes).Distinct(StringComparer.Ordinal).Count();
        if (misses > 0) return CacheOnlyMisses(misses);
        return outcomes.Any(static o => !o.IsTranslated) ? TranslationFailed() : null;
    }

    /// <summary>Znormalizowane teksty bez tłumaczenia w trybie Cache-only.</summary>
    public static IEnumerable<string> CacheOnlyMissTexts(IEnumerable<TranslationOutcome> outcomes) =>
        outcomes.Where(static o => !o.IsTranslated && o.Issue == OutcomeIssue.CacheOnlyMiss)
            .Select(static o => o.NormalizedText);
}
