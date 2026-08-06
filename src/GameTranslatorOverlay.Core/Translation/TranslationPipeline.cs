using System.Collections.Concurrent;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Core.Translation;

public sealed class TranslationPipelineOptions
{
    public bool CacheOnlyMode { get; set; }
    public string GameProfile { get; set; } = string.Empty;
    public int MaxBatchSize { get; set; } = 50;
}

public enum TranslationOrigin
{
    Glossary,
    Cache,
    Provider,
    Unavailable,
}

public sealed record TranslationOutcome(
    string SourceText,
    string NormalizedText,
    string? TranslatedText,
    TranslationOrigin Origin,
    string? ErrorMessage = null)
{
    public bool IsTranslated => TranslatedText is not null;
}

/// <summary>
/// Pionowy przepływ tłumaczenia: słownik → cache → dostawca API.
/// Ten sam tekst pojawiający się równolegle wywołuje najwyżej jedno zapytanie
/// (deduplikacja in-flight). W trybie Cache-only nic nie wychodzi do sieci.
/// </summary>
public sealed class TranslationPipeline(
    IGlossaryService glossary,
    ITranslationCache cache,
    ITranslationProvider provider,
    UsageTracker usage,
    TranslationPipelineOptions options,
    ILogger<TranslationPipeline>? logger = null)
{
    private const string CacheOnlyMessage = "Tryb Cache-only — tego tekstu nie ma jeszcze w lokalnej bazie tłumaczeń.";
    private const string SessionLimitMessage = "Osiągnięto limit znaków dla tej sesji. Zwiększ limit w ustawieniach albo zrestartuj sesję.";

    private readonly ILogger _logger = logger ?? NullLogger<TranslationPipeline>.Instance;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _inFlight = new();

    public ITranslationProvider Provider => provider;
    public TranslationPipelineOptions Options => options;

    public async Task<IReadOnlyList<TranslationOutcome>> TranslateAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];

        var normalizedInputs = texts
            .Select(static text => (Source: text, Normalized: TextNormalizer.Normalize(text)))
            .ToList();

        var outcomes = new Dictionary<string, TranslationOutcome>(StringComparer.Ordinal);
        var pending = new List<(string Source, string Normalized)>();

        foreach (var (source, normalized) in normalizedInputs)
        {
            if (outcomes.ContainsKey(normalized) || pending.Any(p => p.Normalized == normalized)) continue;

            if (normalized.Length == 0)
            {
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, "Pusty tekst.");
                continue;
            }

            var cached = await cache.LookupAsync(normalized, sourceLanguage, targetLanguage, options.GameProfile, cancellationToken)
                .ConfigureAwait(false);

            // Testowe wpisy Mocka („[PL] …”) nie mogą udawać prawdziwych tłumaczeń
            // po przełączeniu na rzeczywistego dostawcę.
            if (cached is { IsManual: false }
                && cached.Provider.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
                && !provider.Name.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                cached = null;
            }

            // Ręczna korekta użytkownika ma absolutne pierwszeństwo — także przed słownikiem.
            if (cached is { IsManual: true })
            {
                usage.RecordCacheHit();
                outcomes[normalized] = new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache);
                continue;
            }

            if (glossary.TryTranslateExact(normalized, out var glossaryTranslation))
            {
                usage.RecordGlossaryHit();
                outcomes[normalized] = new TranslationOutcome(source, normalized, glossaryTranslation, TranslationOrigin.Glossary);
                continue;
            }

            if (cached is not null)
            {
                usage.RecordCacheHit();
                outcomes[normalized] = new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache);
                continue;
            }

            pending.Add((source, normalized));
        }

        if (pending.Count > 0)
        {
            if (options.CacheOnlyMode)
            {
                foreach (var (source, normalized) in pending)
                {
                    outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, CacheOnlyMessage);
                }
            }
            else if (usage.WouldExceedSessionLimit(pending.Sum(static p => p.Normalized.Length)))
            {
                foreach (var (source, normalized) in pending)
                {
                    outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, SessionLimitMessage);
                }
            }
            else
            {
                await TranslatePendingAsync(pending, sourceLanguage, targetLanguage, outcomes, cancellationToken).ConfigureAwait(false);
            }
        }

        return normalizedInputs
            .Select(input => outcomes.TryGetValue(input.Normalized, out var outcome)
                ? outcome with { SourceText = input.Source }
                : new TranslationOutcome(input.Source, input.Normalized, null, TranslationOrigin.Unavailable, "Brak wyniku."))
            .ToList();
    }

    private async Task TranslatePendingAsync(
        List<(string Source, string Normalized)> pending,
        string sourceLanguage,
        string targetLanguage,
        Dictionary<string, TranslationOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var mine = new List<(string Source, string Normalized, string Key, TaskCompletionSource<string> Tcs)>();
        var awaited = new List<(string Source, string Normalized, Task<string> Task)>();

        foreach (var (source, normalized) in pending)
        {
            var key = $"{TextHasher.Sha256Hex(normalized)}|{sourceLanguage}|{targetLanguage}|{options.GameProfile}";
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registered = _inFlight.GetOrAdd(key, tcs);

            if (ReferenceEquals(registered, tcs))
            {
                mine.Add((source, normalized, key, tcs));
            }
            else
            {
                awaited.Add((source, normalized, registered.Task));
            }
        }

        try
        {
            foreach (var chunk in mine.Chunk(Math.Max(1, options.MaxBatchSize)))
            {
                await TranslateChunkAsync(chunk, sourceLanguage, targetLanguage, outcomes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var (_, _, key, tcs) in mine)
            {
                tcs.TrySetCanceled(CancellationToken.None);
                _inFlight.TryRemove(key, out _);
            }
        }

        foreach (var (source, normalized, task) in awaited)
        {
            try
            {
                var translated = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                outcomes[normalized] = new TranslationOutcome(source, normalized, translated, TranslationOrigin.Provider);
            }
            catch (TranslationException ex)
            {
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, ex.UserFriendlyMessage);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable,
                    "Równoległe tłumaczenie tego tekstu zostało anulowane.");
            }
        }
    }

    private async Task TranslateChunkAsync(
        (string Source, string Normalized, string Key, TaskCompletionSource<string> Tcs)[] chunk,
        string sourceLanguage,
        string targetLanguage,
        Dictionary<string, TranslationOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var textsToSend = chunk.Select(static c => c.Normalized).ToList();

        IReadOnlyList<string> translations;
        try
        {
            translations = await provider.TranslateBatchAsync(textsToSend, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            usage.RecordFailure();
            _logger.LogWarning(ex, "Dostawca {Provider} zwrócił błąd ({Kind})", provider.Name, ex.Kind);
            foreach (var (source, normalized, _, tcs) in chunk)
            {
                tcs.TrySetException(ex);
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, ex.UserFriendlyMessage);
            }
            return;
        }

        if (translations.Count != chunk.Length)
        {
            usage.RecordFailure();
            var mismatch = new TranslationException(TranslationFailureKind.Unknown,
                $"Dostawca zwrócił {translations.Count} tłumaczeń dla {chunk.Length} tekstów.");
            foreach (var (source, normalized, _, tcs) in chunk)
            {
                tcs.TrySetException(mismatch);
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, mismatch.UserFriendlyMessage);
            }
            return;
        }

        usage.RecordApiRequest(textsToSend.Sum(static t => t.Length));

        for (var i = 0; i < chunk.Length; i++)
        {
            var (source, normalized, _, tcs) = chunk[i];
            var translated = translations[i];
            tcs.TrySetResult(translated);
            outcomes[normalized] = new TranslationOutcome(source, normalized, translated, TranslationOrigin.Provider);

            try
            {
                // Automatyczne wyniki z API lądują w cache GLOBALNYM — ten sam tekst w innej grze
                // (albo bez profilu) nie może być drugi raz bilingowany. Klucz profilu jest
                // zarezerwowany dla ręcznych korekt i wpisów dostarczanych z profilem.
                // Zapis idzie BEZ tokena operacji: znaki są już zbilingowane, a anulowanie
                // (latest-wins, przebudowa pipeline'u) tuż po odpowiedzi API gubiłoby
                // opłacone tłumaczenie i wymuszało ponowny biling tego samego tekstu.
                await cache.StoreAsync(
                    new NewCacheEntry(source, normalized, sourceLanguage, targetLanguage, translated, provider.Name, GameProfile: string.Empty),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Nie udało się zapisać tłumaczenia do cache");
            }
        }
    }
}
