using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed class LlmProviderOptions : HttpProviderOptions
{
    public LlmProviderOptions()
    {
        // Model językowy odpowiada wolniej niż klasyczny tłumacz — zwłaszcza lokalny.
        RequestTimeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>Najwięcej tekstów w jednym zapytaniu do modelu.</summary>
    public int MaxBatchSize { get; set; } = 25;

    /// <summary>
    /// Gdy model zwróci nieczytelną odpowiedź dla partii, teksty są tłumaczone pojedynczo —
    /// ale tylko dla partii nie większych niż ta wartość (kontrola liczby zapytań).
    /// </summary>
    public int MaxIndividualFallback { get; set; } = 12;

    /// <summary>Górny limit tokenów odpowiedzi (tam, gdzie API go wymaga).</summary>
    public int MaxOutputTokens { get; set; } = 16000;
}

/// <summary>
/// Wspólna logika dostawców opartych na modelach językowych: prompt z kontekstem gry
/// i terminologią, dzielenie na partie, odporne czytanie odpowiedzi i pojedynczy fallback.
/// Podklasa dostarcza wyłącznie jedno wywołanie modelu. Model odpowiada niedeterministycznie,
/// więc pipeline może raz ponowić wynik, który nie przeszedł kontroli jakości. Model potrafi
/// odmienić zwroty do gracza według jego płci (<see cref="IGenderAwareTranslationProvider"/>).
/// </summary>
public abstract class LlmTranslationProviderBase(LlmProviderOptions? options, ILogger logger)
    : IContextualTranslationProvider, IRetryableTranslationProvider, IGenderAwareTranslationProvider
{
    protected LlmProviderOptions Options { get; } = options ?? new LlmProviderOptions();
    protected ILogger Logger { get; } = logger;

    public abstract string Name { get; }
    public abstract bool RequiresApiKey { get; }

    /// <summary>Sprawdza konfigurację przed wysłaniem czegokolwiek (klucz, model, adres).</summary>
    protected abstract void EnsureConfigured();

    /// <summary>Jedno wywołanie modelu: prompt systemowy + wiadomość użytkownika → tekst odpowiedzi.</summary>
    protected abstract Task<string?> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken);

    public abstract Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default) =>
        TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, TranslationContext.Empty, cancellationToken);

    public async Task<IReadOnlyList<string>> TranslateWithContextAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        TranslationContext context,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];
        EnsureConfigured();

        var systemPrompt = LlmTranslationPrompt.BuildSystemPrompt(sourceLanguage, targetLanguage, context);
        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(Math.Max(1, Options.MaxBatchSize)))
        {
            results.AddRange(await TranslateChunkAsync(chunk, systemPrompt, context.RecentExchanges, cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> TranslateChunkAsync(
        string[] chunk, string systemPrompt, IReadOnlyList<RecentExchange> previousLines, CancellationToken cancellationToken)
    {
        var content = await CompleteAsync(systemPrompt, LlmTranslationPrompt.BuildUserMessage(chunk, previousLines), cancellationToken)
            .ConfigureAwait(false);
        if (LlmTranslationPrompt.ParseTranslations(content, chunk.Length) is { } parsed)
        {
            return parsed;
        }

        if (chunk.Length == 1 || chunk.Length > Options.MaxIndividualFallback)
        {
            throw UnreadableResponse(chunk.Length);
        }

        // Model pomylił liczbę albo format wyników — tłumaczymy pojedynczo zamiast
        // ryzykować przesunięcie tłumaczeń między blokami.
        Logger.LogInformation("{Provider}: nieczytelna odpowiedź dla partii {Count} tekstów — tłumaczę pojedynczo",
            Name, chunk.Length);
        var singles = new List<string>(chunk.Length);
        foreach (var text in chunk)
        {
            var single = await CompleteAsync(systemPrompt, LlmTranslationPrompt.BuildUserMessage([text], previousLines), cancellationToken)
                .ConfigureAwait(false);
            var parsedSingle = LlmTranslationPrompt.ParseTranslations(single, 1) ?? throw UnreadableResponse(1);
            singles.Add(parsedSingle[0]);
        }
        return singles;
    }

    private TranslationException UnreadableResponse(int count) =>
        new(TranslationFailureKind.Unknown,
            $"{Name}: odpowiedzi modelu nie da się odczytać jako listy {count} tłumaczeń.");

    /// <summary>Wspólna próba połączenia: tłumaczy krótki tekst i pokazuje wynik.</summary>
    protected async Task<ProviderStatus> ProbeTranslationAsync(string description, CancellationToken cancellationToken)
    {
        return await ProviderHttp.TestAsync(async () =>
        {
            EnsureConfigured();
            var result = await TranslateWithContextAsync(["Hello, adventurer!"], "en", "pl", TranslationContext.Empty, cancellationToken)
                .ConfigureAwait(false);
            return new ProviderStatus(true, $"{description}: „Hello, adventurer!” → „{result[0]}”.");
        }).ConfigureAwait(false);
    }
}
