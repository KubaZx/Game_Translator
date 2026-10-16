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
    : IContextualTranslationProvider, IRetryableTranslationProvider, IGenderAwareTranslationProvider, IStreamingTranslationProvider
{
    protected LlmProviderOptions Options { get; } = options ?? new LlmProviderOptions();
    protected ILogger Logger { get; } = logger;

    public abstract string Name { get; }
    public abstract bool RequiresApiKey { get; }

    /// <summary>Sprawdza konfigurację przed wysłaniem czegokolwiek (klucz, model, adres).</summary>
    protected abstract void EnsureConfigured();

    /// <summary>Jedno wywołanie modelu: prompt systemowy + wiadomość użytkownika → tekst odpowiedzi.</summary>
    protected abstract Task<string?> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken);

    protected virtual Task<string?> CompleteStreamingAsync(
        string systemPrompt, string userMessage, LlmTranslationStreamParser stream, CancellationToken cancellationToken) =>
        CompleteAsync(systemPrompt, userMessage, cancellationToken);

    public abstract Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default) =>
        TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, TranslationContext.Empty, cancellationToken);

    public Task<IReadOnlyList<string>> TranslateWithContextAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        TranslationContext context,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(texts, sourceLanguage, targetLanguage, context, onTranslation: null, cancellationToken);

    public Task<IReadOnlyList<string>> TranslateStreamingAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        TranslationContext context,
        Action<int, string> onTranslation,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(texts, sourceLanguage, targetLanguage, context, onTranslation, cancellationToken);

    private async Task<IReadOnlyList<string>> TranslateCoreAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        TranslationContext context,
        Action<int, string>? onTranslation,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return [];
        EnsureConfigured();

        var systemPrompt = LlmTranslationPrompt.BuildSystemPrompt(sourceLanguage, targetLanguage, context);
        var notes = context.TextNotes.Count == texts.Count ? context.TextNotes : null;
        var results = new List<string>(texts.Count);
        var offset = 0;
        foreach (var chunk in texts.Chunk(Math.Max(1, Options.MaxBatchSize)))
        {
            var chunkNotes = notes?.Skip(offset).Take(chunk.Length).ToList();
            var chunkOffset = offset;
            offset += chunk.Length;
            Action<int, string>? onChunkTranslation = onTranslation is null
                ? null
                : (index, text) => onTranslation(index == PartialResults.Reset ? PartialResults.Reset : chunkOffset + index, text);
            results.AddRange(await TranslateChunkAsync(chunk, chunkNotes, systemPrompt, context.RecentExchanges, onChunkTranslation,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    private async Task<string?> CompleteChunkAsync(
        string systemPrompt, string userMessage, int count, Action<int, string>? onTranslation, CancellationToken cancellationToken)
    {
        if (onTranslation is null)
        {
            return await CompleteAsync(systemPrompt, userMessage, cancellationToken).ConfigureAwait(false);
        }

        LlmTranslationStreamParser? stream = null;
        stream = new LlmTranslationStreamParser(count, (index, text) =>
        {
            try
            {
                onTranslation(index, text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "{Provider}: odbiorca wczesnych tłumaczeń zgłosił błąd", Name);
                stream!.Stop();
            }
        });
        return await CompleteStreamingAsync(systemPrompt, userMessage, stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> TranslateChunkAsync(
        string[] chunk, IReadOnlyList<string?>? notes, string systemPrompt, IReadOnlyList<RecentExchange> previousLines,
        Action<int, string>? onTranslation, CancellationToken cancellationToken)
    {
        var content = await CompleteChunkAsync(systemPrompt, LlmTranslationPrompt.BuildUserMessage(chunk, previousLines, notes),
                chunk.Length, onTranslation, cancellationToken)
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
        if (onTranslation is not null)
        {
            try
            {
                onTranslation(PartialResults.Reset, string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "{Provider}: odbiorca wczesnych tłumaczeń zgłosił błąd", Name);
            }
        }
        var singles = new List<string>(chunk.Length);
        for (var i = 0; i < chunk.Length; i++)
        {
            var text = chunk[i];
            IReadOnlyList<string?>? note = notes is not null ? [notes[i]] : null;
            var single = await CompleteAsync(systemPrompt, LlmTranslationPrompt.BuildUserMessage([text], previousLines, note), cancellationToken)
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
