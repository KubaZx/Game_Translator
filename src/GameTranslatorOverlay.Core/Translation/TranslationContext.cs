using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Wskazówki dla dostawców, którzy potrafią je wykorzystać (modele językowe): nazwa gry
/// z aktywnego profilu i terminy słownika występujące w tłumaczonych tekstach.
/// Nie zawiera obrazów ani tekstów spoza tłumaczonej partii.
/// </summary>
public sealed record TranslationContext(string? GameName, IReadOnlyList<GlossaryTerm> Terms)
{
    public static TranslationContext Empty { get; } = new(null, []);

    public bool IsEmpty => string.IsNullOrWhiteSpace(GameName) && Terms.Count == 0;
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
