using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>
    /// Opis problemu wykrytego przez kontrolę jakości (<see cref="TranslationQualityGate"/>)
    /// w pokazanym tłumaczeniu, np. zmienione liczby; null, gdy wynik przeszedł kontrolę.
    /// </summary>
    public string? QualityWarning { get; init; }
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

    // Tłumaczenia wieloliniowe w starym formacie (sprzed sklejania wierszy), pominięte, żeby
    // przetłumaczyć je lepiej. Gdy dostawca zawiedzie (sieć, limit, błąd), gracz dostaje
    // stary wynik zamiast komunikatu o błędzie.
    // Profil zapamiętujemy, bo nowy wynik musi nadpisać TEN wpis: wpis profilu wygrywa
    // przy odczycie z globalnym, więc zapis do globalnego zostawiałby nieaktualny wpis
    // na zawsze i każde wystąpienie tekstu szłoby do płatnego dostawcy.
    private readonly ConcurrentDictionary<string, StaleEntry> _staleFallbacks = new(StringComparer.Ordinal);

    // TranslatedText null = wpis zastępowany, którego nie wolno pokazać jako zapasowy
    // (np. wynik atrapy Mock przy prawdziwym dostawcy) — liczy się tylko jego profil.
    private sealed record StaleEntry(string? TranslatedText, string GameProfile, TranslationCacheContext Context);

    // Błąd odczytu cache logujemy raz na pipeline — w trybie live pytamy co klatkę.
    private int _cacheLookupFailureLogged;

    // Gdy trwała baza nie działa (uszkodzony plik, blokada, pełny dysk), każde kolejne
    // wystąpienie tego samego tekstu szłoby do płatnego dostawcy. Do czasu zbudowania
    // nowego pipeline'u pamiętamy więc wyniki w pamięci procesu (nic nie trafia na dysk,
    // więc obietnice trybu prywatnego zostają). Rozmiar ograniczony — to awaryjna łatka.
    private const int MaxDegradedResults = 2000;
    private readonly ConcurrentDictionary<string, string> _degradedResults = new(StringComparer.Ordinal);
    private int _cacheDegraded;

    /// <summary>
    /// Czy cache zgłosił błąd odczytu lub zapisu w tym pipeline. Wyniki są wtedy pamiętane
    /// tylko w pamięci (awaryjnie), a UI może pokazać ostrzeżenie o niedziałającej bazie.
    /// </summary>
    public bool IsCacheDegraded => Volatile.Read(ref _cacheDegraded) == 1;

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

        foreach (var (_, normalized) in normalizedInputs)
        {
            if (!_staleFallbacks.TryRemove(normalized, out var stale)) continue;
            if (stale.TranslatedText is not null
                && outcomes.TryGetValue(normalized, out var failed) && failed.TranslatedText is null)
            {
                outcomes[normalized] = failed with
                {
                    TranslatedText = stale.TranslatedText,
                    Origin = TranslationOrigin.Cache,
                    ErrorMessage = null,
                    QualityWarning = TranslationQualityGate.Describe(stale.Context.QualityIssues),
                };
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
        CachedTranslation? cached;
        string? degradedHit = null;
        try
        {
            cached = await cache.LookupAsync(normalized, sourceLanguage, targetLanguage, options.GameProfile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cache to tylko przyspieszenie: zablokowana, pełna albo tylko-do-odczytu baza nie
            // może zatrzymać tłumaczenia. Brak wpisu = zwykła ścieżka (w Cache-only nic nie
            // wyjdzie do sieci). W logu bez treści tekstu — sam typ i opis błędu bazy.
            if (Interlocked.Exchange(ref _cacheLookupFailureLogged, 1) == 0)
                _logger.LogWarning(ex, "Odczyt cache tłumaczeń nie powiódł się — traktuję to jako brak wpisu (kolejne takie błędy nie będą logowane)");
            cached = null;
            Volatile.Write(ref _cacheDegraded, 1);
            _degradedResults.TryGetValue(DegradedKey(normalized, sourceLanguage, targetLanguage), out degradedHit);
        }
        // Mock cache entries must never impersonate translations from a real provider.
        // Profil zapamiętujemy jak przy nieaktualnych wpisach: prawdziwy wynik musi nadpisać
        // wpis atrapy w jego profilu, inaczej wygrywałby on przy każdym odczycie.
        if (cached is { IsManual: false }
            && cached.Provider.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            && !provider.Name.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            if (!options.CacheOnlyMode && cached.GameProfile.Length > 0)
                _staleFallbacks[normalized] = new StaleEntry(null, cached.GameProfile, default);
            cached = null;
        }

        // Automatyczne tłumaczenie tekstu wieloliniowego sprzed sklejania wierszy było
        // tłumaczone kawałkami (gramatyka rozbita na granicach wierszy) — tłumaczymy je
        // ponownie raz, w nowym formacie; stary wynik zostaje jako zapasowy. W Cache-only
        // nic nie wychodzi do sieci, więc stary wynik jest nadal najlepszym dostępnym.
        // Tak samo raz ponawiamy wynik oznaczony przez kontrolę jakości (np. zmienione liczby).
        // Ręczne korekty zawsze zostają.
        if (!options.CacheOnlyMode && cached is { IsManual: false }
            && TranslationCacheContext.IsStale(cached.Context, normalized))
        {
            _staleFallbacks[normalized] = new StaleEntry(
                cached.TranslatedText, cached.GameProfile, TranslationCacheContext.Parse(cached.Context));
            cached = null;
        }

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
            return new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache)
            {
                QualityWarning = TranslationQualityGate.Describe(TranslationCacheContext.Parse(cached.Context).QualityIssues),
            };
        }
        if (degradedHit is not null)
        {
            usage.RecordCacheHit();
            return new TranslationOutcome(source, normalized, degradedHit, TranslationOrigin.Cache);
        }
        return null;
    }

    private static string DegradedKey(string normalized, string sourceLanguage, string targetLanguage) =>
        $"{sourceLanguage}|{targetLanguage}|{normalized}";

    private void RememberDegraded(string normalized, string sourceLanguage, string targetLanguage, string translated)
    {
        // Proste ograniczenie: po przekroczeniu limitu zaczynamy od zera zamiast LRU —
        // to tryb awaryjny, liczy się tylko, żeby nie rósł bez końca.
        if (_degradedResults.Count >= MaxDegradedResults) _degradedResults.Clear();
        _degradedResults[DegradedKey(normalized, sourceLanguage, targetLanguage)] = translated;
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
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, WaiterMessage(ex));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable,
                    "Równoległe tłumaczenie tego tekstu zostało anulowane.");
            }
        }
    }

    /// <summary>
    /// Lokalne odmowy (limit sesji, pusty wynik) używają istniejących rodzajów błędów, ale
    /// czekający na ten sam tekst ma dostać to samo wyjaśnienie co właściciel zapytania,
    /// a nie ogólny komunikat dostawcy.
    /// </summary>
    private static string WaiterMessage(TranslationException ex) => ex switch
    {
        { Kind: TranslationFailureKind.QuotaExceeded, Message: SessionLimitMessage } => SessionLimitMessage,
        { Kind: TranslationFailureKind.Unknown, Message: TranslationQualityGate.EmptyResultMessage } => TranslationQualityGate.EmptyResultMessage,
        _ => ex.UserFriendlyMessage,
    };

    private Task<IReadOnlyList<string>> SendToProviderAsync(
        IReadOnlyList<string> texts, TranslationContext context, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken) =>
        // Dostawcy kontekstowi (modele językowe) dostają nazwę gry i terminy słownika
        // występujące w tej partii — spójne nazwy także wewnątrz dłuższych zdań.
        provider is IContextualTranslationProvider contextual
            ? contextual.TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, context, cancellationToken)
            : provider.TranslateBatchAsync(texts, sourceLanguage, targetLanguage, cancellationToken);

    private TranslationContext BuildContext(IReadOnlyList<string> texts)
    {
        var terms = glossary.FindTermsIn(texts, options.MaxContextTerms);
        var gameName = string.IsNullOrWhiteSpace(options.GameName) ? null : options.GameName.Trim();
        var recent = RecentTextsExcluding(texts);
        return gameName is null && terms.Count == 0 && recent.Count == 0
            ? TranslationContext.Empty
            : new TranslationContext(gameName, terms)
            {
                RecentTexts = recent,
                GlossaryTerms = GlossaryTermsFor(terms),
            };
    }

    /// <summary>
    /// Słownik dla glosariusza DeepL (przechowywanego na koncie użytkownika): tylko terminy,
    /// które wolno zapisać poza komputerem, i tylko gdy partia zawiera co najmniej jeden
    /// z nich. Termin z trybu prywatnego (bywa całą linią czatu) nigdy nie trafia na konto
    /// DeepL — ani sam, ani jako wyzwalacz utworzenia glosariusza. Podpowiedzi dla LLM
    /// (<see cref="TranslationContext.Terms"/>) idą razem z tekstem w tym samym zapytaniu
    /// i niczego nie utrwalają, więc mogą korzystać ze wszystkich terminów.
    /// </summary>
    private IReadOnlyList<GlossaryTerm> GlossaryTermsFor(IReadOnlyList<GlossaryTerm> foundTerms)
    {
        if (foundTerms.Count == 0) return [];
        var persistable = glossary.PersistableTerms;
        if (persistable.Count == 0) return [];
        var persistableSet = persistable.ToHashSet();
        return foundTerms.Any(persistableSet.Contains) ? persistable : [];
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
        // Migawka zastępowanych wpisów PRZED zwolnieniem czekających (TrySetResult): słownik
        // jest wspólny dla równoległych wywołań i kluczowany samym tekstem, więc czekający
        // na ten sam tekst może usunąć wpis, zanim właściciel zdąży z niego skorzystać —
        // wynik trafiłby wtedy do globalnego cache, a wpis profilu zostałby nieaktualny.
        var staleEntries = chunk
            .Select(c => _staleFallbacks.TryGetValue(c.Normalized, out var s) ? s : null)
            .ToArray();
        // Wiersze jednego zdania sklejamy przed wysłaniem (tłumacz nie rozbija gramatyki
        // na granicach wierszy), a po tłumaczeniu przywracamy tę samą liczbę wierszy.
        var reflowed = chunk.Select(static c => TextReflow.Unwrap(c.Normalized)).ToList();
        var textsToSend = reflowed.Select(static r => r.Text).ToList();
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
        var context = provider is IContextualTranslationProvider ? BuildContext(textsToSend) : TranslationContext.Empty;
        var providerStarted = Stopwatch.GetTimestamp();
        try
        {
            translations = await SendToProviderAsync(textsToSend, context, sourceLanguage, targetLanguage, cancellationToken)
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
        // Tylko udane odpowiedzi: błąd lub timeout zafałszowałby typowy czas dostawcy.
        usage.Latency.Record(LatencyStage.Provider,
            Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds);
        RememberSent(textsToSend);

        var results = new string[chunk.Length];
        var issues = new TranslationQualityFlags[chunk.Length];
        for (var i = 0; i < chunk.Length; i++)
        {
            results[i] = TextReflow.Rewrap(translations[i] ?? string.Empty, reflowed[i].Plan);
            issues[i] = TranslationQualityGate.Check(textsToSend[i], results[i]);
        }
        // Ponowne tłumaczenie wpisu już oznaczonego przez kontrolę jakości to samo w sobie
        // „ponowienie” — bez dodatkowej próby w tej samej partii. Tekst z trwałym problemem
        // (np. fałszywy alarm „10:30 PM” → „22:30”) kosztuje wtedy najwyżej 3 zapytania:
        // pierwsze + ponowienie przy pierwszym wystąpieniu i jedno przy drugim.
        var skipRetry = staleEntries.Select(static s => s?.Context.NeedsQualityRetry == true).ToArray();
        await RetryFlaggedAsync(textsToSend, reflowed, results, issues, skipRetry, context, sourceLanguage, targetLanguage,
                cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < chunk.Length; i++)
        {
            var (source, normalized, _, tcs) = chunk[i];
            var translated = results[i];
            var quality = issues[i];
            var stale = staleEntries[i];
            // Automatyczne wyniki z API lądują w cache GLOBALNYM — ten sam tekst w innej grze
            // (albo bez profilu) nie może być drugi raz bilingowany. Klucz profilu jest
            // zarezerwowany dla ręcznych korekt i wpisów dostarczanych z profilem.
            // Wyjątek: nowy wynik zastępujący nieaktualny automatyczny wpis profilu musi
            // nadpisać właśnie jego — wpis profilu wygrywa przy odczycie, więc zapis do
            // globalnego zostawiłby go i tekst byłby tłumaczony przy każdym wystąpieniu.
            var profile = stale is { GameProfile.Length: > 0 } ? stale.GameProfile : string.Empty;

            if (quality.HasFlag(TranslationQualityFlags.Empty))
            {
                // Pusty wynik to błąd dostawcy, nie tłumaczenie: nie trafia do cache (inaczej
                // tekst zostałby „przetłumaczony” na nic na zawsze) i liczy się jako porażka.
                usage.RecordQualityIssue(quality);
                usage.RecordFailure();
                tcs.TrySetException(new TranslationException(TranslationFailureKind.Unknown, TranslationQualityGate.EmptyResultMessage));
                outcomes[normalized] = new TranslationOutcome(
                    source, normalized, null, TranslationOrigin.Unavailable, TranslationQualityGate.EmptyResultMessage);
                // Pusty wynik przy ponownym tłumaczeniu wpisu z problemem jakości: stary wynik
                // (pokazany jako zapasowy) zapisujemy jako ostateczny — inaczej każde kolejne
                // wystąpienie byłoby kolejnym płatnym zapytaniem z pustą odpowiedzią.
                if (stale is { TranslatedText: not null, Context.NeedsQualityRetry: true })
                {
                    await TryStoreAsync(new NewCacheEntry(source, normalized, sourceLanguage, targetLanguage,
                        stale.TranslatedText, provider.Name, GameProfile: profile,
                        Context: TranslationCacheContext.Build(stale.Context.QualityIssues, final: true))).ConfigureAwait(false);
                }
                continue;
            }

            if (quality != TranslationQualityFlags.None) usage.RecordQualityIssue(quality);
            tcs.TrySetResult(translated);
            outcomes[normalized] = new TranslationOutcome(source, normalized, translated, TranslationOrigin.Provider)
            {
                QualityWarning = TranslationQualityGate.Describe(quality),
            };

            // Wynik z problemem jakości jest oznaczany, żeby następnym razem przetłumaczyć go
            // ponownie — ale tylko raz: gdy zastępujemy już oznaczony wpis, a problem nadal
            // jest, znacznik jest ostateczny (bez pętli płatnych zapytań).
            var cacheContext = TranslationCacheContext.Build(quality, final: stale?.Context.NeedsQualityRetry == true);

            if (IsCacheDegraded) RememberDegraded(normalized, sourceLanguage, targetLanguage, translated);
            if (!await TryStoreAsync(new NewCacheEntry(source, normalized, sourceLanguage, targetLanguage, translated,
                    provider.Name, GameProfile: profile, Context: cacheContext)).ConfigureAwait(false))
            {
                RememberDegraded(normalized, sourceLanguage, targetLanguage, translated);
            }
        }
    }

    /// <summary>Zapis do cache; false, gdy baza zgłosiła błąd (sam błąd jest logowany).</summary>
    private async Task<bool> TryStoreAsync(NewCacheEntry entry)
    {
        try
        {
            // Scene/session cancellation does not discard an already paid response.
            // A separate settings/privacy epoch may forbid writes to this captured cache.
            cacheWriteCancellationToken.ThrowIfCancellationRequested();
            await cache.StoreAsync(entry, cacheWriteCancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Volatile.Write(ref _cacheDegraded, 1);
            _logger.LogWarning(ex, "Nie udało się zapisać tłumaczenia do cache");
            return false;
        }
    }

    /// <summary>
    /// Jedno ponowienie tekstów z problemem jakości (poza pustymi) — tylko dla dostawców,
    /// u których ma to sens (<see cref="IRetryableTranslationProvider"/>). Ponowienie jest
    /// zwykłym zapytaniem: rezerwuje znaki w limicie sesji i jest liczone w statystykach.
    /// Zostaje wariant z mniejszą liczbą problemów; każdy błąd ponowienia zostawia pierwszy
    /// (już zapłacony) wynik.
    /// </summary>
    private async Task RetryFlaggedAsync(
        List<string> textsToSend,
        List<(string Text, ReflowPlan Plan)> reflowed,
        string[] results,
        TranslationQualityFlags[] issues,
        bool[] skipRetry,
        TranslationContext context,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (provider is not IRetryableTranslationProvider) return;

        var flagged = Enumerable.Range(0, results.Length)
            .Where(i => !skipRetry[i] && issues[i] != TranslationQualityFlags.None && !issues[i].HasFlag(TranslationQualityFlags.Empty))
            .ToList();
        if (flagged.Count == 0) return;

        var retryTexts = flagged.Select(i => textsToSend[i]).ToList();
        using var reservation = usage.TryReserveApiCharacters(retryTexts.Sum(static t => t.Length));
        // Limit sesji wyczerpany — pokazujemy pierwszy wynik z ostrzeżeniem zamiast odmowy.
        if (reservation is null) return;
        usage.RecordQualityRetry();

        IReadOnlyList<string> retried;
        try
        {
            retried = await SendToProviderAsync(retryTexts, context, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            _logger.LogWarning(ex, "Ponowienie tłumaczenia u dostawcy {Provider} nie powiodło się ({Kind})", provider.Name, ex.Kind);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Pierwsza odpowiedź jest już zapłacona — zostaje w wynikach i w cache.
            return;
        }

        if (retried.Count != flagged.Count)
        {
            _logger.LogWarning("Ponowienie u dostawcy {Provider}: {Count} wyników dla {Expected} tekstów",
                provider.Name, retried.Count, flagged.Count);
            return;
        }
        reservation.Complete();

        for (var k = 0; k < flagged.Count; k++)
        {
            var i = flagged[k];
            var candidate = TextReflow.Rewrap(retried[k] ?? string.Empty, reflowed[i].Plan);
            var candidateIssues = TranslationQualityGate.Check(textsToSend[i], candidate);
            if (TranslationQualityGate.Severity(candidateIssues) < TranslationQualityGate.Severity(issues[i]))
            {
                results[i] = candidate;
                issues[i] = candidateIssues;
            }
        }
    }
}
