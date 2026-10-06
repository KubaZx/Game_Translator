using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Wcześniejsza linia i tłumaczenie, które zwrócił dla niej ten sam dostawca w tej sesji
/// (surowy wynik — jeden akapit na akapit źródła, przed przywróceniem podziału wierszy).
/// </summary>
public sealed record RecentExchange(string Source, string Translation);

/// <summary>
/// Wskazówki dla dostawców, którzy potrafią je wykorzystać: nazwa gry z aktywnego profilu,
/// terminy słownika występujące w tłumaczonych tekstach oraz ostatnie linie wysłane
/// wcześniej do tego samego dostawcy. Nie zawiera obrazów ani tekstu, który nie opuścił
/// już wcześniej komputera.
/// </summary>
public sealed record TranslationContext(string? GameName, IReadOnlyList<GlossaryTerm> Terms)
{
    public static TranslationContext Empty { get; } = new(null, []);

    /// <summary>
    /// Wcześniejsze teksty (od najstarszego), już przetłumaczone przez tego dostawcę w tej
    /// sesji — np. poprzednie kwestie dialogu. Tylko kontekst: nie są tłumaczone ponownie.
    /// </summary>
    /// <remarks>
    /// Pipeline wypełnia to pole źródłami z <see cref="RecentExchanges"/> — dla dostawców,
    /// którzy przyjmują tylko kontekst w języku źródłowym (DeepL).
    /// </remarks>
    public IReadOnlyList<string> RecentTexts { get; init; } = [];

    /// <summary>
    /// Wcześniejsze pary źródło → tłumaczenie (od najstarszej) — model językowy widzi, co sam
    /// już napisał („gotowy” czy „gotowa”), i może trzymać się tych samych form i nazw.
    /// </summary>
    public IReadOnlyList<RecentExchange> RecentExchanges { get; init; } = [];

    /// <summary>
    /// Płeć postaci gracza z ustawień; wykorzystują ją tylko dostawcy
    /// <see cref="IGenderAwareTranslationProvider"/> (modele językowe).
    /// </summary>
    public PlayerGender PlayerGender { get; init; }

    /// <summary>
    /// Aktywny słownik bez terminów z trybu prywatnego — dla dostawców z własnymi,
    /// trwałymi glosariuszami (DeepL). Wypełniany tylko wtedy, gdy partia zawiera co
    /// najmniej jeden taki termin (<see cref="Terms"/>), żeby glosariusz nie powstawał
    /// dla tekstów, w których nic z niego nie występuje.
    /// </summary>
    public IReadOnlyList<GlossaryTerm> GlossaryTerms { get; init; } = [];

    public string? Scene { get; init; }

    public IReadOnlyList<string?> TextNotes { get; init; } = [];

    public bool IsEmpty => string.IsNullOrWhiteSpace(GameName) && Terms.Count == 0 && RecentTexts.Count == 0
        && RecentExchanges.Count == 0 && PlayerGender == PlayerGender.Unknown
        && string.IsNullOrWhiteSpace(Scene) && !TextNotes.Any(static note => !string.IsNullOrWhiteSpace(note));
}

/// <summary>
/// Dostawca, który potrafi zawczasu nawiązać połączenie (DNS, TCP, TLS), zanim pojawi się
/// tekst do tłumaczenia. Rozgrzewka nie wysyła klucza ani treści i nigdy nie rzuca wyjątku
/// poza anulowaniem.
/// </summary>
public interface IWarmableTranslationProvider
{
    Task WarmUpAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Dostawca, który przyjmuje dodatkowy kontekst tłumaczenia. Pipeline wywołuje ten wariant
/// zamiast <see cref="ITranslationProvider.TranslateBatchAsync"/>, jeśli dostawca go implementuje.
/// </summary>
public interface IContextualTranslationProvider : ITranslationProvider
{
    Task<IReadOnlyList<string>> TranslateWithContextAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        TranslationContext context,
        CancellationToken cancellationToken = default);
}
