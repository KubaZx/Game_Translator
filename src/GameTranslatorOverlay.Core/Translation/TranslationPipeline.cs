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

    /// <summary>
    /// Łączna długość (źródło + tłumaczenie) par pamięci dialogu dołączanych jako kontekst —
    /// długie opisy nie mogą rozdmuchać każdego zapytania do modelu.
    /// </summary>
    public int MaxRecentContextChars { get; set; } = 1500;

    /// <summary>Płeć postaci gracza — wskazówka dla dostawców świadomych płci (modele językowe).</summary>
    public PlayerGender PlayerGender { get; set; }
}

public enum TranslationOrigin
{
    Glossary,
    Cache,
    Provider,
    Unavailable,
}

/// <summary>
/// Lokalny powód braku tłumaczenia (bez treści tekstu) — pozwala pokazać graczowi krótki
/// komunikat w nakładce zamiast parsować <see cref="TranslationOutcome.ErrorMessage"/>.
/// </summary>
public enum OutcomeIssue
{
    None,

    /// <summary>Tryb Cache-only: tekstu nie ma w lokalnej bazie, nic nie wyszło do sieci.</summary>
    CacheOnlyMiss,

    /// <summary>Wyczerpany limit znaków tej sesji (lokalna odmowa, bez zapytania).</summary>
    SessionLimit,

    /// <summary>Dostawca zgłosił błąd — rodzaj w <see cref="TranslationOutcome.FailureKind"/>.</summary>
    Provider,

    /// <summary>Dostawca odpowiedział pustym tłumaczeniem.</summary>
    EmptyResult,
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

    /// <summary>
    /// Rodzaj błędu dostawcy, gdy tekst nie został przetłumaczony przez jego wyjątek
    /// (także u czekających na to samo zapytanie); null w pozostałych przypadkach.
    /// </summary>
    public TranslationFailureKind? FailureKind { get; init; }

    /// <summary>Dlaczego zabrakło tłumaczenia (Cache-only, limit sesji, błąd dostawcy, pusty wynik).</summary>
    public OutcomeIssue Issue { get; init; }
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

    // Ostatnie teksty wysłane do dostawcy tego pipeline'u razem z jego tłumaczeniami (od
    // najstarszego). Pipeline jest budowany od nowa przy zmianie dostawcy, więc kontekst
    // nigdy nie trafia do innego dostawcy niż ten, który już widział te teksty.
    private readonly DialogMemory _dialog = new(
        Math.Max(0, options.MaxRecentContextTexts), Math.Max(0, options.MaxRecentContextChars));

    // Tłumaczenia wieloliniowe w starym formacie (sprzed sklejania wierszy), pominięte, żeby
    // przetłumaczyć je lepiej. Gdy dostawca zawiedzie (sieć, limit, błąd), gracz dostaje
    // stary wynik zamiast komunikatu o błędzie.
    // Profil zapamiętujemy, bo nowy wynik musi nadpisać TEN wpis: wpis profilu wygrywa
    // przy odczycie z globalnym, więc zapis do globalnego zostawiałby nieaktualny wpis
    // na zawsze i każde wystąpienie tekstu szłoby do płatnego dostawcy.
    // Słownik takich wpisów jest osobny dla każdego wywołania TranslateAsync (nie wspólny dla
    // pipeline'u): dwa równoległe wywołania z tym samym nieaktualnym tekstem (właściciel
    // zapytania i czekający na nie) muszą oba dostać stary wynik, gdy dostawca zawiedzie —
    // przy wspólnym słowniku kluczowanym samym tekstem pierwsze zabierało go drugiemu.

    // Teksty, które już raz poszły do dostawcy z powodu innej płci gracza we wpisie cache.
    // Znacznik qa-final nie zatrzymuje takiej ponownej próby (płeć nadal się różni), a gdy
    // dostawca zwróci pusty wynik albo błąd, nic nowego nie jest zapisywane — bez tej listy
    // każde wystąpienie linii byłoby kolejnym płatnym zapytaniem. Najwyżej jedna próba na
    // tekst i pipeline (nowy pipeline po zmianie ustawień próbuje od nowa — też raz).
    // Tylko w pamięci procesu; rozmiar ograniczony jak awaryjna pamięć wyników.
    private readonly ConcurrentDictionary<string, byte> _genderRetried = new(StringComparer.Ordinal);

    // TranslatedText null = wpis zastępowany, którego nie wolno pokazać jako zapasowy
    // (np. wynik atrapy Mock przy prawdziwym dostawcy) — liczy się tylko jego profil.
    // GenderStale = wpis jest nieaktualny (także) z powodu innej płci gracza.
    private sealed record StaleEntry(
        string? TranslatedText, string GameProfile, TranslationCacheContext Context, bool GenderStale = false);

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

    /// <summary>
    /// Płeć gracza, która wpływa na wynik tego dostawcy: ustawienie dla dostawcy świadomego
    /// płci, Unknown dla pozostałych (ich tłumaczenia od niej nie zależą, więc nie może ona
    /// ani trafiać do znacznika cache, ani unieważniać wpisów).
    /// </summary>
    private PlayerGender EffectiveGender =>
        provider is IGenderAwareTranslationProvider ? options.PlayerGender : PlayerGender.Unknown;

    /// <summary>
    /// Ręczna korekta gracza zastępuje w pamięci dialogu tłumaczenie tej linii, jeśli tam jest —
    /// kolejne linie mają się trzymać poprawionej formy (np. „gotowa” zamiast „gotowy”), a nie
    /// błędu, który gracz właśnie poprawił. Zwraca false, gdy linii nie ma w pamięci.
    /// </summary>
    public bool RememberManualCorrection(string normalizedText, string correctedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(correctedText)) return false;
        var (source, plan) = TextReflow.Unwrap(normalizedText);
        if (source.Length == 0) return false;
        var translation = TextReflow.ToParagraphs(correctedText, plan);
        return translation.Length > 0 && _dialog.ReplaceTranslation(source, translation);
    }

    public Task<IReadOnlyList<TranslationOutcome>> TranslateAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default) =>
        TranslateAsync(texts, sourceLanguage, targetLanguage, knownLocal: null, cancellationToken);

    /// <summary>
    /// Jak <see cref="TranslateAsync(IReadOnlyList{string}, string, string, CancellationToken)"/>,
    /// ale z wynikiem wcześniejszego <see cref="TranslateLocalAsync"/> dla tych samych tekstów.
    /// Znane już lokalnie teksty nie są szukane w cache drugi raz: odczyt z trwałej bazy podbija
    /// jej licznik użyć (use_count), więc częściowo znana klatka live liczyłaby każdy znany
    /// tekst podwójnie, a każde pudło kosztowałoby drugie zapytanie. Trafienia z próby liczymy
    /// tutaj (próba częściowo znanej partii ich nie liczy). Teksty null z próby (brak, wpis
    /// nieaktualny) przechodzą pełną ścieżkę — w międzyczasie mogły trafić do cache.
    /// Lista o innej długości niż <paramref name="texts"/> jest ignorowana.
    /// </summary>
    public async Task<IReadOnlyList<TranslationOutcome>> TranslateAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<TranslationOutcome?>? knownLocal,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];
        if (knownLocal is not null && knownLocal.Count != texts.Count) knownLocal = null;

        var normalizedInputs = NormalizeAll(texts);

        // Najpierw zbieramy teksty, o które trzeba zapytać cache (pierwsze wystąpienie, niepuste,
        // nieznane z próby lokalnej) — dokładnie te, o które pytała dotąd pętla niżej, tekst po
        // tekście — i pytamy o nie jednym odczytem partii: cache trwały obsługuje wtedy całą
        // klatkę jednym połączeniem zamiast osobnego zadania i połączenia na każdy tekst.
        var lookupSlots = new int[normalizedInputs.Length];
        var lookupTexts = new List<string>(normalizedInputs.Length);
        var seen = new HashSet<string>(normalizedInputs.Length, StringComparer.Ordinal);
        for (var index = 0; index < normalizedInputs.Length; index++)
        {
            var normalized = normalizedInputs[index].Normalized;
            lookupSlots[index] = NoLookup;
            if (!seen.Add(normalized)) lookupSlots[index] = DuplicateText;
            else if (normalized.Length > 0 && !(knownLocal?[index] is { } known && known.NormalizedText == normalized))
            {
                lookupSlots[index] = lookupTexts.Count;
                lookupTexts.Add(normalized);
            }
        }
        var lookups = await LookupManyAsync(lookupTexts, sourceLanguage, targetLanguage, cancellationToken)
            .ConfigureAwait(false);

        var outcomes = new Dictionary<string, TranslationOutcome>(normalizedInputs.Length, StringComparer.Ordinal);
        var pending = new List<(string Source, string Normalized)>();
        var staleFallbacks = new Dictionary<string, StaleEntry>(StringComparer.Ordinal);

        for (var index = 0; index < normalizedInputs.Length; index++)
        {
            // Powtórzony tekst dostaje wynik pierwszego wystąpienia (niżej, przy składaniu wyniku).
            if (lookupSlots[index] == DuplicateText) continue;
            var (source, normalized) = normalizedInputs[index];

            if (normalized.Length == 0)
            {
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, "Pusty tekst.");
                continue;
            }

            if (knownLocal?[index] is { } known && known.NormalizedText == normalized)
            {
                if (known.Origin == TranslationOrigin.Glossary) usage.RecordGlossaryHit();
                else if (known.Origin == TranslationOrigin.Cache) usage.RecordCacheHit();
                outcomes[normalized] = known;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var local = TryTranslateLocally(source, normalized, sourceLanguage, targetLanguage,
                lookups[lookupSlots[index]], staleFallbacks);
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
                    outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, CacheOnlyMessage)
                    {
                        Issue = OutcomeIssue.CacheOnlyMiss,
                    };
                }
            }
            else
            {
                await TranslatePendingAsync(pending, sourceLanguage, targetLanguage, outcomes, staleFallbacks, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (var (_, normalized) in normalizedInputs)
        {
            if (!staleFallbacks.TryGetValue(normalized, out var stale)) continue;
            if (stale.TranslatedText is not null
                && outcomes.TryGetValue(normalized, out var failed) && failed.TranslatedText is null)
            {
                outcomes[normalized] = failed with
                {
                    TranslatedText = stale.TranslatedText,
                    Origin = TranslationOrigin.Cache,
                    ErrorMessage = null,
                    FailureKind = null,
                    Issue = OutcomeIssue.None,
                    QualityWarning = TranslationQualityGate.Describe(stale.Context.QualityIssues),
                };
            }
        }

        var results = new List<TranslationOutcome>(normalizedInputs.Length);
        foreach (var (source, normalized) in normalizedInputs)
        {
            results.Add(outcomes.TryGetValue(normalized, out var outcome)
                ? WithSource(outcome, source)
                : new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, "Brak wyniku."));
        }
        return results;
    }

    private const int NoLookup = -1;
    private const int DuplicateText = -2;

    private static (string Source, string Normalized)[] NormalizeAll(IReadOnlyList<string> texts)
    {
        var inputs = new (string Source, string Normalized)[texts.Count];
        for (var i = 0; i < inputs.Length; i++) inputs[i] = (texts[i], TextNormalizer.Normalize(texts[i]));
        return inputs;
    }

    // Wynik ma nieść tekst źródłowy tego wystąpienia; kopia rekordu tylko wtedy, gdy się różni
    // (zwykle wynik powstał właśnie dla tego tekstu i kopia byłaby identyczna).
    private static TranslationOutcome WithSource(TranslationOutcome outcome, string source) =>
        string.Equals(outcome.SourceText, source, StringComparison.Ordinal) ? outcome : outcome with { SourceText = source };

    /// <summary>
    /// Odczyt cache dla całej partii. Błąd pojedynczego odczytu trafia do jego wyniku (obsługa
    /// jak dotąd w <see cref="TryTranslateLocally"/>); gdyby cache rzucił błędem całego
    /// wywołania, traktujemy go jak błąd każdego odczytu partii.
    /// </summary>
    private async Task<IReadOnlyList<CacheLookupResult>> LookupManyAsync(
        IReadOnlyList<string> normalizedTexts, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (normalizedTexts.Count == 0) return [];
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var results = await cache.LookupManyAsync(
                    normalizedTexts, sourceLanguage, targetLanguage, options.GameProfile, cancellationToken)
                .ConfigureAwait(false);
            if (results.Count == normalizedTexts.Count) return results;
            throw new InvalidOperationException("Cache zwrócił inną liczbę wyników niż liczba tekstów.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new CacheLookupResult[normalizedTexts.Count];
            Array.Fill(failed, new CacheLookupResult(null, ex));
            return failed;
        }
    }

    /// <summary>
    /// Tylko lokalna część <see cref="TranslateAsync"/>: korekta ręczna → słownik → cache,
    /// z tymi samymi regułami (filtr atrapy Mock, nieaktualne wpisy, awaryjna pamięć przy
    /// uszkodzonej bazie). Nie woła dostawcy i nie rezerwuje budżetu znaków, więc tryb live
    /// może pokazać w pełni znaną klatkę bez czekania na wolne miejsce w kolejce tłumaczeń.
    /// Element null = tekstu nie da się przetłumaczyć lokalnie. Nieaktualny wpis (stary
    /// format, oznaczony przez kontrolę jakości) NIE jest tu lokalny — TranslateAsync spróbuje
    /// go odświeżyć — i nie zostawia wpisu zapasowego. Trafienia liczymy tylko, gdy lokalna
    /// jest cała partia: w przeciwnym razie wołający i tak przejdzie przez TranslateAsync,
    /// który policzy te same trafienia, a podwójne liczenie zawyżałoby statystyki.
    /// </summary>
    public async Task<IReadOnlyList<TranslationOutcome?>> TranslateLocalAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];

        var normalizedInputs = NormalizeAll(texts);
        var outcomes = new Dictionary<string, TranslationOutcome?>(normalizedInputs.Length, StringComparer.Ordinal);

        // Jak w TranslateAsync: jeden odczyt cache dla wszystkich różnych niepustych tekstów.
        var lookupTexts = new List<string>(normalizedInputs.Length);
        var seen = new HashSet<string>(normalizedInputs.Length, StringComparer.Ordinal);
        foreach (var (_, normalized) in normalizedInputs)
        {
            if (seen.Add(normalized) && normalized.Length > 0) lookupTexts.Add(normalized);
        }
        var lookups = await LookupManyAsync(lookupTexts, sourceLanguage, targetLanguage, cancellationToken)
            .ConfigureAwait(false);

        var allLocal = true;
        var nextLookup = 0;
        foreach (var (source, normalized) in normalizedInputs)
        {
            if (outcomes.ContainsKey(normalized)) continue;
            TranslationOutcome? local;
            if (normalized.Length == 0)
            {
                // Ten sam wynik co w TranslateAsync — też bez dostawcy.
                local = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, "Pusty tekst.");
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                local = TryTranslateLocally(source, normalized, sourceLanguage, targetLanguage,
                    lookups[nextLookup++], staleFallbacks: null);
            }
            outcomes[normalized] = local;
            allLocal &= local is not null;
        }

        if (allLocal)
        {
            foreach (var outcome in outcomes.Values)
            {
                if (outcome is { Origin: TranslationOrigin.Glossary }) usage.RecordGlossaryHit();
                else if (outcome is { Origin: TranslationOrigin.Cache }) usage.RecordCacheHit();
            }
        }

        var results = new List<TranslationOutcome?>(normalizedInputs.Length);
        foreach (var (source, normalized) in normalizedInputs)
            results.Add(outcomes[normalized] is { } outcome ? WithSource(outcome, source) : null);
        return results;
    }

    /// <param name="staleFallbacks">
    /// Wpisy zastępowane (zapasowe) tego wywołania TranslateAsync. null = sprawdzenie bez
    /// skutków ubocznych: bez liczenia trafień i bez zapamiętywania wpisów zapasowych (te są
    /// potrzebne tylko ścieżce, która naprawdę zapyta dostawcę).
    /// </param>
    private async Task<TranslationOutcome?> TryTranslateLocallyAsync(
        string source, string normalized, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken, Dictionary<string, StaleEntry>? staleFallbacks)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheLookupResult lookup;
        try
        {
            lookup = new CacheLookupResult(await cache.LookupAsync(
                    normalized, sourceLanguage, targetLanguage, options.GameProfile, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lookup = new CacheLookupResult(null, ex);
        }
        return TryTranslateLocally(source, normalized, sourceLanguage, targetLanguage, lookup, staleFallbacks);
    }

    /// <summary>Jak <see cref="TryTranslateLocallyAsync"/>, z gotowym wynikiem odczytu cache tego tekstu.</summary>
    private TranslationOutcome? TryTranslateLocally(
        string source, string normalized, string sourceLanguage, string targetLanguage,
        CacheLookupResult lookup, Dictionary<string, StaleEntry>? staleFallbacks)
    {
        var probeOnly = staleFallbacks is null;
        var cached = lookup.Translation;
        string? degradedHit = null;
        if (lookup.Error is { } ex)
        {
            // Cache to tylko przyspieszenie: zablokowana, pełna albo tylko-do-odczytu baza nie
            // może zatrzymać tłumaczenia. Brak wpisu = zwykła ścieżka (w Cache-only nic nie
            // wyjdzie do sieci). W logu bez treści tekstu — sam typ i opis błędu bazy.
            if (Interlocked.Exchange(ref _cacheLookupFailureLogged, 1) == 0)
                _logger.LogWarning(ex, "Odczyt cache tłumaczeń nie powiódł się — traktuję to jako brak wpisu (kolejne takie błędy nie będą logowane)");
            cached = null;
            Volatile.Write(ref _cacheDegraded, 1);
        }
        // Mock cache entries must never impersonate translations from a real provider.
        // Profil zapamiętujemy jak przy nieaktualnych wpisach: prawdziwy wynik musi nadpisać
        // wpis atrapy w jego profilu, inaczej wygrywałby on przy każdym odczycie.
        if (cached is { IsManual: false }
            && cached.Provider.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            && !provider.Name.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            if (staleFallbacks is not null && !options.CacheOnlyMode && cached.GameProfile.Length > 0)
                staleFallbacks[normalized] = new StaleEntry(null, cached.GameProfile, default);
            cached = null;
        }

        // Automatyczne tłumaczenie tekstu wieloliniowego sprzed sklejania wierszy było
        // tłumaczone kawałkami (gramatyka rozbita na granicach wierszy) — tłumaczymy je
        // ponownie raz, w nowym formacie; stary wynik zostaje jako zapasowy. W Cache-only
        // nic nie wychodzi do sieci, więc stary wynik jest nadal najlepszym dostępnym.
        // Tak samo raz ponawiamy wynik oznaczony przez kontrolę jakości (np. zmienione liczby)
        // oraz — u dostawcy świadomego płci — tekst zwracający się do gracza, przetłumaczony
        // z inną płcią gracza niż obecna („gotowy” po przełączeniu na postać kobiecą).
        // Ręczne korekty zawsze zostają.
        // Tekst już raz wysłany z powodu innej płci nie jest nią ponownie unieważniany (patrz
        // _genderRetried) — inne powody (np. niesprawdzony problem jakości) działają dalej.
        var gender = _genderRetried.ContainsKey(normalized) ? PlayerGender.Unknown : EffectiveGender;
        if (!options.CacheOnlyMode && cached is { IsManual: false }
            && TranslationCacheContext.IsStale(cached.Context, normalized, gender))
        {
            if (staleFallbacks is not null)
                staleFallbacks[normalized] = new StaleEntry(
                    cached.TranslatedText, cached.GameProfile, TranslationCacheContext.Parse(cached.Context),
                    TranslationCacheContext.IsStaleForPlayerGender(cached.Context, normalized, gender));
            cached = null;
        }

        // Awaryjna pamięć wyników działa nie tylko przy błędzie odczytu: przy pełnym dysku albo
        // bazie tylko do odczytu SELECT nadal działa (zwraca brak wpisu albo wpis nieaktualny),
        // a zawodzi tylko INSERT — bez tego każde wystąpienie tekstu szłoby znowu do płatnego
        // dostawcy. Pierwszeństwo nadal mają ręczne korekty, słownik i aktualny wpis bazy.
        if (cached is null && IsCacheDegraded)
            _degradedResults.TryGetValue(DegradedKey(normalized, sourceLanguage, targetLanguage), out degradedHit);

        // Manual corrections retain precedence over the glossary on both lookups.
        if (cached is { IsManual: true })
        {
            if (!probeOnly) usage.RecordCacheHit();
            return new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache);
        }
        if (glossary.TryTranslateExact(normalized, out var translation))
        {
            if (!probeOnly) usage.RecordGlossaryHit();
            return new TranslationOutcome(source, normalized, translation, TranslationOrigin.Glossary);
        }
        if (cached is not null)
        {
            if (!probeOnly) usage.RecordCacheHit();
            return new TranslationOutcome(source, normalized, cached.TranslatedText, TranslationOrigin.Cache)
            {
                QualityWarning = TranslationQualityGate.Describe(TranslationCacheContext.Parse(cached.Context).QualityIssues),
            };
        }
        if (degradedHit is not null)
        {
            if (!probeOnly) usage.RecordCacheHit();
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
        Dictionary<string, StaleEntry> staleFallbacks,
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
                        item.Source, item.Normalized, sourceLanguage, targetLanguage, cancellationToken, staleFallbacks)
                        .ConfigureAwait(false);
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
                    await TranslateChunkAsync(toSend.ToArray(), sourceLanguage, targetLanguage, outcomes, staleFallbacks,
                            cancellationToken)
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
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, WaiterMessage(ex))
                {
                    FailureKind = WaiterFailureKind(ex),
                    Issue = WaiterIssue(ex),
                };
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

    // Czekający dostaje ten sam powód co właściciel zapytania: lokalne odmowy (limit sesji,
    // pusty wynik) nie udają błędu dostawcy, więc nie niosą też jego rodzaju błędu.
    private static OutcomeIssue WaiterIssue(TranslationException ex) => ex switch
    {
        { Kind: TranslationFailureKind.QuotaExceeded, Message: SessionLimitMessage } => OutcomeIssue.SessionLimit,
        { Kind: TranslationFailureKind.Unknown, Message: TranslationQualityGate.EmptyResultMessage } => OutcomeIssue.EmptyResult,
        _ => OutcomeIssue.Provider,
    };

    private static TranslationFailureKind? WaiterFailureKind(TranslationException ex) =>
        WaiterIssue(ex) == OutcomeIssue.Provider ? ex.Kind : null;

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
        var recent = _dialog.Excluding(texts);
        // DeepL przyjmuje wyłącznie kontekst w języku źródłowym — dostaje same źródła, z własnym
        // budżetem znaków (bez tłumaczeń, których i tak nie widzi) i także linie, których wynik
        // nie nadaje się na przykład dla modelu.
        var recentSources = _dialog.SourcesExcluding(texts);
        return gameName is null && terms.Count == 0 && recent.Count == 0 && recentSources.Count == 0
               && options.PlayerGender == PlayerGender.Unknown
            ? TranslationContext.Empty
            : new TranslationContext(gameName, terms)
            {
                RecentTexts = recentSources,
                RecentExchanges = recent,
                GlossaryTerms = GlossaryTermsFor(terms),
                PlayerGender = options.PlayerGender,
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

    // Wynik nieprzetłumaczony (echo oryginału) albo „rozgadany” nie jest przykładem, którego
    // model ma się trzymać — pokazany jako jego własne wcześniejsze tłumaczenie utrwalałby błąd
    // w kolejnych liniach. Jego angielski oryginał zostaje jednak w kontekście źródłowym (DeepL),
    // jak przed pamięcią par. Pusty wynik to błąd dostawcy — nie trafia nigdzie. Zmienione
    // liczby (częsty fałszywy alarm, np. „10:30 PM” → „22:30”) nie psują form gramatycznych,
    // więc taka linia zostaje pełnoprawnym przykładem.
    private const TranslationQualityFlags NotExampleIssues =
        TranslationQualityFlags.Untranslated | TranslationQualityFlags.Runaway;

    private async Task TranslateChunkAsync(
        (string Source, string Normalized, string Key, TaskCompletionSource<string> Tcs)[] chunk,
        string sourceLanguage,
        string targetLanguage,
        Dictionary<string, TranslationOutcome> outcomes,
        Dictionary<string, StaleEntry> staleFallbacks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Zastępowane wpisy tego wywołania (profil do nadpisania, kontekst kontroli jakości).
        var staleEntries = chunk
            .Select(c => staleFallbacks.TryGetValue(c.Normalized, out var s) ? s : null)
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
                    source, normalized, null, TranslationOrigin.Unavailable, SessionLimitMessage)
                {
                    Issue = OutcomeIssue.SessionLimit,
                };
            }
            // A local admission denial is not a failed request to the provider.
            return;
        }

        // Próbę z nową płcią liczymy w chwili wysłania (odmowa lokalnego limitu wyżej niczego nie wysyła), bez względu na jej wynik (pusty wynik,
        // błąd, nieczytelna odpowiedź) — patrz _genderRetried.
        for (var i = 0; i < chunk.Length; i++)
        {
            if (staleEntries[i] is not { GenderStale: true }) continue;
            if (_genderRetried.Count >= MaxDegradedResults) _genderRetried.Clear();
            _genderRetried.TryAdd(chunk[i].Normalized, 0);
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
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, ex.UserFriendlyMessage)
                {
                    FailureKind = ex.Kind,
                    Issue = OutcomeIssue.Provider,
                };
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
                outcomes[normalized] = new TranslationOutcome(source, normalized, null, TranslationOrigin.Unavailable, mismatch.UserFriendlyMessage)
                {
                    FailureKind = mismatch.Kind,
                    Issue = OutcomeIssue.Provider,
                };
            }
            return;
        }

        reservation.Complete();
        // Tylko udane odpowiedzi: błąd lub timeout zafałszowałby typowy czas dostawcy.
        usage.Latency.Record(LatencyStage.Provider,
            Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds);

        // Surowe wyniki (przed przywróceniem wierszy) trafiają do pamięci dialogu: model dostał
        // sklejony tekst, więc jako swoje wcześniejsze tłumaczenie widzi też sklejony wynik.
        var raw = new string[chunk.Length];
        var results = new string[chunk.Length];
        var issues = new TranslationQualityFlags[chunk.Length];
        for (var i = 0; i < chunk.Length; i++)
        {
            raw[i] = translations[i] ?? string.Empty;
            results[i] = TextReflow.Rewrap(raw[i], reflowed[i].Plan);
            issues[i] = TranslationQualityGate.Check(textsToSend[i], results[i]);
        }
        // Ponowne tłumaczenie wpisu już oznaczonego przez kontrolę jakości to samo w sobie
        // „ponowienie” — bez dodatkowej próby w tej samej partii. Tekst z trwałym problemem
        // (np. fałszywy alarm „10:30 PM” → „22:30”) kosztuje wtedy najwyżej 3 zapytania:
        // pierwsze + ponowienie przy pierwszym wystąpieniu i jedno przy drugim.
        // Wpis już ostateczny (qa-final), ponawiany tylko z powodu innej płci gracza, też nie
        // dostaje ponowienia w partii — jego problem jakości został już raz sprawdzony.
        var skipRetry = staleEntries
            .Select(static s => s is not null && (s.Context.NeedsQualityRetry || s.Context.QualityFinal))
            .ToArray();
        await RetryFlaggedAsync(textsToSend, reflowed, raw, results, issues, skipRetry, context, sourceLanguage, targetLanguage,
                cancellationToken)
            .ConfigureAwait(false);

        _dialog.RememberResults(Enumerable.Range(0, chunk.Length)
            .Where(i => !issues[i].HasFlag(TranslationQualityFlags.Empty))
            .Select(i => (new RecentExchange(textsToSend[i], raw[i].Trim()),
                (issues[i] & NotExampleIssues) == TranslationQualityFlags.None)));

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
                    source, normalized, null, TranslationOrigin.Unavailable, TranslationQualityGate.EmptyResultMessage)
                {
                    Issue = OutcomeIssue.EmptyResult,
                };
                // Pusty wynik przy ponownym tłumaczeniu wpisu z problemem jakości: stary wynik
                // (pokazany jako zapasowy) zapisujemy jako ostateczny — inaczej każde kolejne
                // wystąpienie byłoby kolejnym płatnym zapytaniem z pustą odpowiedzią.
                if (stale is { TranslatedText: not null, Context.NeedsQualityRetry: true })
                {
                    if (!await TryStoreAsync(new NewCacheEntry(source, normalized, sourceLanguage, targetLanguage,
                            stale.TranslatedText, provider.Name, GameProfile: profile,
                            Context: TranslationCacheContext.Build(stale.Context.QualityIssues, final: true,
                                playerGender: stale.Context.PlayerGender))).ConfigureAwait(false))
                    {
                        // Zapis się nie udał (np. pełny dysk) — ostateczny wynik pamiętamy
                        // awaryjnie, żeby nie pytać dostawcy przy każdym wystąpieniu.
                        RememberDegraded(normalized, sourceLanguage, targetLanguage, stale.TranslatedText);
                    }
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
            // Ostateczność przechodzi też na nowy wynik wpisu qa-final tłumaczonego ponownie tylko
            // z powodu płci — inaczej ten sam fałszywy alarm zaczynałby cykl ponowień od nowa
            // przy każdej zmianie płci.
            var final = stale is not null
                && (stale.Context.NeedsQualityRetry
                    || (stale.Context.QualityFinal && quality != TranslationQualityFlags.None));
            var cacheContext = TranslationCacheContext.Build(quality, final: final, playerGender: EffectiveGender);

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
        string[] raw,
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
            var candidateRaw = retried[k] ?? string.Empty;
            var candidate = TextReflow.Rewrap(candidateRaw, reflowed[i].Plan);
            var candidateIssues = TranslationQualityGate.Check(textsToSend[i], candidate);
            if (TranslationQualityGate.Severity(candidateIssues) < TranslationQualityGate.Severity(issues[i]))
            {
                raw[i] = candidateRaw;
                results[i] = candidate;
                issues[i] = candidateIssues;
            }
        }
    }
}
