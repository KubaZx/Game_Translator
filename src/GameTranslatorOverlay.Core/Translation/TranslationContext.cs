using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Translation;

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
    public IReadOnlyList<string> RecentTexts { get; init; } = [];

    /// <summary>
    /// Cały aktywny słownik — dla dostawców z własnymi glosariuszami (DeepL). Wypełniany
    /// tylko wtedy, gdy partia zawiera co najmniej jeden termin (<see cref="Terms"/>),
    /// żeby glosariusz nie powstawał dla tekstów, w których nic z niego nie występuje.
    /// </summary>
    public IReadOnlyList<GlossaryTerm> GlossaryTerms { get; init; } = [];

    public bool IsEmpty => string.IsNullOrWhiteSpace(GameName) && Terms.Count == 0 && RecentTexts.Count == 0;
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
