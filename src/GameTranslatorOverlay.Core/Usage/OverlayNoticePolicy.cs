using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Usage;

/// <summary>Waga komunikatu w nakładce — ważniejszy wypiera mniej ważny, nigdy odwrotnie.</summary>
public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Krótki komunikat dla gracza pokazywany w nakładce nad grą (główne okno jest wtedy
/// zwykle schowane). Tekst nigdy nie zawiera treści z ekranu — tylko stały opis problemu.
/// </summary>
/// <param name="DedupeKey">Klucz powtórzeń: ten sam klucz pojawia się najwyżej raz na <see cref="OverlayNoticePolicy.DedupeWindow"/>.</param>
/// <param name="Duration">Jak długo komunikat jest widoczny (z wygaszaniem po stronie UI).</param>
public sealed record OverlayNotice(string DedupeKey, NoticeSeverity Severity, string Text, TimeSpan Duration)
{
    /// <summary>Zmiana stanu (live włączony/wyłączony) — pokazywana zawsze, bez okna powtórzeń.</summary>
    public bool IsStateChange { get; init; }

    /// <summary>
    /// Komunikat, którego gracz nie może przegapić (zatrzymany live, brak/odrzucony klucz,
    /// wyczerpany limit) — pokazywany także wtedy, gdy gracz sam schował nakładkę.
    /// </summary>
    public bool IsCritical { get; init; }
}

/// <summary>
/// Decyduje, które komunikaty trafiają do nakładki: bez zegara systemowego (czas podaje
/// wołający), więc reguły da się sprawdzić testami co do milisekundy.
/// Reguły: ten sam klucz najwyżej raz na 30 s (pętla live widzi ten sam błąd co klatkę —
/// bez tego komunikat migałby bez końca); ważniejszy komunikat wypiera widoczny mniej
/// ważny, a mniej ważny czeka, aż ważniejszy wygaśnie; zmiany stanu przechodzą zawsze;
/// pudła Cache-only są zbierane w jeden licznik.
/// Nie jest bezpieczna wątkowo — każda instancja należy do jednego wątku (pętla live
/// albo wątek UI).
/// </summary>
public sealed class OverlayNoticePolicy
{
    /// <summary>Najkrótszy odstęp między dwoma pokazaniami komunikatu o tym samym kluczu.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);

    public const string CacheOnlyMissKey = "cache-only-miss";

    // Ograniczenie pamięci zbioru pudeł Cache-only: wielogodzinna sesja w nowej grze nie
    // może rosnąć bez końca. Po przekroczeniu licznik stoi (komunikat i tak mówi „dużo”).
    private const int MaxTrackedMisses = 5000;

    private readonly Dictionary<string, TimeSpan> _lastShown = new(StringComparer.Ordinal);
    private OverlayNotice? _current;
    private TimeSpan _currentShownAt;

    // Pudła Cache-only liczymy po skrócie znormalizowanego tekstu — sama liczba wystarcza
    // komunikatowi, a treść ekranu nie musi leżeć w pamięci dłużej niż klatka.
    private readonly HashSet<int> _cacheOnlyMisses = [];
    private bool _cacheOnlyMissesGrew;
    private bool _cacheDegradedShown;

    /// <summary>Ile różnych tekstów nie znalazło tłumaczenia w trybie Cache-only od startu.</summary>
    public int CacheOnlyMissCount => _cacheOnlyMisses.Count;

    /// <summary>
    /// Proponuje komunikat. True = należy go pokazać teraz (staje się bieżącym);
    /// false = powtórka w oknie 30 s albo widoczny jest ważniejszy komunikat.
    /// Odrzucenie z powodu ważniejszego komunikatu nie zużywa okna powtórzeń — ten sam
    /// problem może się pokazać, gdy tamten wygaśnie.
    /// </summary>
    public bool Offer(OverlayNotice notice, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!notice.IsStateChange)
        {
            if (_lastShown.TryGetValue(notice.DedupeKey, out var last) && now - last < DedupeWindow) return false;
            if (Current(now) is { } visible && visible.Severity > notice.Severity) return false;
        }

        _lastShown[notice.DedupeKey] = now;
        _current = notice;
        _currentShownAt = now;
        return true;
    }

    /// <summary>Komunikat widoczny w chwili <paramref name="now"/>; null, gdy żaden nie trwa.</summary>
    public OverlayNotice? Current(TimeSpan now)
    {
        if (_current is null) return null;
        if (now - _currentShownAt >= _current.Duration || now < _currentShownAt)
        {
            _current = null;
            return null;
        }
        return _current;
    }

    /// <summary>
    /// Zbiera pudła Cache-only (znormalizowane teksty bez tłumaczenia) w jeden komunikat
    /// „Cache-only: N tekstów bez tłumaczenia”. Nowy komunikat pojawia się tylko, gdy od
    /// ostatniego doszły nowe teksty, i najwyżej raz na 30 s; N to liczba różnych tekstów
    /// od startu. Zwraca komunikat do pokazania albo null.
    /// </summary>
    public OverlayNotice? OfferCacheOnlyMisses(IEnumerable<string> normalizedTexts, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(normalizedTexts);
        RecordCacheOnlyMisses(normalizedTexts);
        if (!_cacheOnlyMissesGrew) return null;

        var notice = OverlayNotices.CacheOnlyMisses(_cacheOnlyMisses.Count);
        if (!Offer(notice, now)) return null;
        _cacheOnlyMissesGrew = false;
        return notice;
    }

    /// <summary>
    /// Komunikat dla jednej klatki live (najwyżej jeden na aktualizację), w kolejności
    /// ważności: pierwszy problem w wynikach (błąd dostawcy, limit sesji, pusty wynik),
    /// potem jednorazowe ostrzeżenie o niedziałającym cache, na końcu zbiorczy licznik
    /// pudeł Cache-only. Null = nic nowego do pokazania.
    /// </summary>
    public OverlayNotice? OfferFrame(
        IReadOnlyList<TranslationOutcome> outcomes, string? providerName, bool cacheDegraded, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        // Pudła zbieramy zawsze — także gdy klatka pokaże inny komunikat — żeby licznik był pełny.
        var misses = OverlayNotices.CacheOnlyMissTexts(outcomes).ToList();

        if (OverlayNotices.FromOutcomes(outcomes, providerName) is { } failure && Offer(failure, now))
        {
            RecordCacheOnlyMisses(misses);
            return failure;
        }
        // Ostrzeżenie o cache raz na sesję: to stan trwały (do przebudowy pipeline'u),
        // a nie zdarzenie — powtarzanie go co 30 s tylko zasłaniałoby grę.
        if (cacheDegraded && !_cacheDegradedShown)
        {
            var degraded = OverlayNotices.CacheDegraded();
            if (Offer(degraded, now))
            {
                _cacheDegradedShown = true;
                RecordCacheOnlyMisses(misses);
                return degraded;
            }
        }
        return OfferCacheOnlyMisses(misses, now);
    }

    private void RecordCacheOnlyMisses(IEnumerable<string> normalizedTexts)
    {
        foreach (var text in normalizedTexts)
        {
            if (text.Length == 0 || _cacheOnlyMisses.Count >= MaxTrackedMisses) continue;
            if (_cacheOnlyMisses.Add(StringComparer.Ordinal.GetHashCode(text))) _cacheOnlyMissesGrew = true;
        }
    }

    /// <summary>Zapomina historię (np. nowa sesja live) — następne komunikaty pokażą się od razu.</summary>
    public void Reset()
    {
        _lastShown.Clear();
        _current = null;
        _cacheOnlyMisses.Clear();
        _cacheOnlyMissesGrew = false;
        _cacheDegradedShown = false;
    }
}
