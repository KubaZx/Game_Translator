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

    /// <summary>Czytelna nazwa gry z profilu — wskazówka dla dostawców kontekstowych (LLM).</summary>
    public string? GameName { get; set; }

    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Najwięcej terminów słownika przekazywanych dostawcy kontekstowemu w jednej partii.</summary>
    public int MaxContextTerms { get; set; } = 40;

    /// <summary>Ile ostatnich wysłanych tekstów dołączać jako kontekst (np. poprzednie kwestie dialogu).</summary>
    public int MaxRecentContextTexts { get; set; } = 6;
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
    ILogger<TranslationPipeline>? logger = null,
    CancellationToken cacheWriteCancellationToken = default)
{
    private const string CacheOnlyMessage = "Tryb Cache-only — tego tekstu nie ma jeszcze w lokalnej bazie tłumaczeń.";
    private const string SessionLimitMessage = "Osiągnięto limit znaków dla tej sesji. Zwiększ limit w ustawieniach albo zrestartuj sesję.";

    private readonly ILogger _logger = logger ?? NullLogger<TranslationPipeline>.Instance;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _inFlight = new();

    // Ostatnie teksty wysłane do dostawcy tego pipeline'u (od najstarszego). Pipeline jest
    // budowany od nowa przy zmianie dostawcy, więc kontekst nigdy nie trafia do innego
    // dostawcy niż ten, który już widział te teksty.
    private readonly Lock _recentGate = new();
    private readonly LinkedList<string> _recentSent = new();

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

            var local = await TryTranslateLocallyAsync(source, normalized, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
            if (local is not null)
            {
                outcomes[normalized] = local;
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

    private async Task<TranslationOutcome?> TryTranslateLocallyAsync(
        string source, string normalized, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = await cache.LookupAsync(normalized, sourceLanguage, targetLanguage, options.GameProfile, cancellationToken)
            .ConfigureAwait(false);
        // Mock cache entries must never impersonate translations from a real provider.
        if (cached is { IsManual: false }
            && cached.Provider.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            && !provider.Name.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            cached = null;

        // Manual corrections retain precedence over the glossary on both lookups.
        if (cached is { IsManual: true })
        {
            usage.RecordCacheHit();
            return new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache);
        }
        if (glossary.TryTranslateExact(normalized, out var translation))
        {
            usage.RecordGlossaryHit();
            return new TranslationOutcome(source, normalized, translation, TranslationOrigin.Glossary);
        }
        if (cached is not null)
        {
            usage.RecordCacheHit();
            return new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache);
        }
        return null;
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
                var toSend = new List<(string Source, string Normalized, string Key, TaskCompletionSource<string> Tcs)>();
                foreach (var item in chunk)
                {
                    // A previous owner may have filled the cache after our first miss
                    // and removed its in-flight entry before we registered ours.
                    var local = await TryTranslateLocallyAsync(
                        item.Source, item.Normalized, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
                    if (local is not null)
                    {
                        outcomes[item.Normalized] = local;
                        item.Tcs.TrySetResult(local.TranslatedText!);
                    }
                    else
                    {
                        toSend.Add(item);
                    }
                }
                if (toSend.Count > 0)
                    await TranslateChunkAsync(toSend.ToArray(), sourceLanguage, targetLanguage, outcomes, cancellationToken)
                        .ConfigureAwait(false);
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
                // Local admission uses the existing quota failure kind, but must retain
                // the session-limit explanation rather than a remote provider-quota message.
                var message = ex.Kind == TranslationFailureKind.QuotaExceeded && ex.Message == SessionLimitMessage
                    ? SessionLimitMessage : ex.UserFriendlyMessage;
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable,
                    "Równoległe tłumaczenie tego tekstu zostało anulowane.");
            }
        }
    }

    private TranslationContext BuildContext(IReadOnlyList<string> texts)
    {
        var terms = glossary.FindTermsIn(texts, options.MaxContextTerms);
        var gameName = string.IsNullOrWhiteSpace(options.GameName) ? null : options.GameName.Trim();
        var recent = RecentTextsExcluding(texts);
        return gameName is null && terms.Count == 0 && recent.Count == 0
            ? TranslationContext.Empty
            : new TranslationContext(gameName, terms) { RecentTexts = recent };
    }

    private IReadOnlyList<string> RecentTextsExcluding(IReadOnlyList<string> texts)
    {
        lock (_recentGate)
        {
            if (_recentSent.Count == 0) return [];
            var current = texts.ToHashSet(StringComparer.Ordinal);
            return _recentSent.Where(text => !current.Contains(text)).ToList();
        }
    }

    private void RememberSent(IEnumerable<string> texts)
    {
        var limit = Math.Max(0, options.MaxRecentContextTexts);
        if (limit == 0) return;
        lock (_recentGate)
        {
            foreach (var text in texts)
            {
                _recentSent.Remove(text);
                _recentSent.AddLast(text);
            }
            while (_recentSent.Count > limit) _recentSent.RemoveFirst();
        }
    }

    private async Task TranslateChunkAsync(
        (string Source, string Normalized, string Key, TaskCompletionSource<string> Tcs)[] chunk,
        string sourceLanguage,
        string targetLanguage,
        Dictionary<string, TranslationOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var textsToSend = chunk.Select(static c => c.Normalized).ToList();
        // Only owned, non-deduplicated texts reserve budget. Waiters reuse the owner's
        // admission and outcome; a different pipeline shares the same UsageTracker gate.
        using var reservation = usage.TryReserveApiCharacters(textsToSend.Sum(static t => t.Length));
        if (reservation is null)
        {
            var denied = new TranslationException(TranslationFailureKind.QuotaExceeded, SessionLimitMessage);
            foreach (var (source, normalized, _, tcs) in chunk)
            {
                tcs.TrySetException(denied);
                outcomes[normalized] = new TranslationOutcome(
                    source, normalized, null, TranslationOrigin.Unavailable, SessionLimitMessage);
            }
            // A local admission denial is not a failed request to the provider.
            return;
        }

        IReadOnlyList<string> translations;
        try
        {
            // Dostawcy kontekstowi (modele językowe) dostają nazwę gry i terminy słownika
            // występujące w tej partii — spójne nazwy także wewnątrz dłuższych zdań.
            translations = provider is IContextualTranslationProvider contextual
                ? await contextual.TranslateWithContextAsync(
                        textsToSend, sourceLanguage, targetLanguage, BuildContext(textsToSend), cancellationToken)
                    .ConfigureAwait(false)
                : await provider.TranslateBatchAsync(textsToSend, sourceLanguage, targetLanguage, cancellationToken)
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

        reservation.Complete();
        RememberSent(textsToSend);

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
                // Scene/session cancellation does not discard an already paid response.
                // A separate settings/privacy epoch may forbid writes to this captured cache.
                cacheWriteCancellationToken.ThrowIfCancellationRequested();
                await cache.StoreAsync(
                    new NewCacheEntry(source, normalized, sourceLanguage, targetLanguage, translated, provider.Name, GameProfile: string.Empty),
                    cacheWriteCancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Nie udało się zapisać tłumaczenia do cache");
            }
        }
    }
}
