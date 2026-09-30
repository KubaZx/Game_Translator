using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.Infrastructure.Providers;

/// <summary>
/// Glosariusz DeepL budowany ze słownika użytkownika. DeepL stosuje wpisy glosariusza także
/// wewnątrz zdań i odmienia je gramatycznie, więc nazwy przedmiotów, postaci i miejsc są
/// tłumaczone spójnie. Jeden glosariusz na zawartość słownika: nazwa zawiera skrót treści,
/// więc po restarcie aplikacji istniejący glosariusz jest ponownie używany, a po zmianie
/// słownika powstaje nowy, a poprzednie glosariusze tej aplikacji są usuwane.
/// Przygotowanie glosariusza (lista + utworzenie) działa w tle i nigdy nie blokuje
/// tłumaczenia dłużej niż <see cref="DeepLOptions.GlossaryWaitBudget"/>; błąd oznacza
/// przerwę, w której tłumaczymy bez glosariusza.
/// </summary>
internal sealed class DeepLGlossaryManager(
    HttpClient httpClient,
    DeepLOptions options,
    ILogger logger,
    TimeProvider? timeProvider = null)
{
    internal const string NamePrefix = "GameTranslatorOverlay ";
    internal const int MaxEntries = 10_000;
    internal static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // _state chroni wybór przygotowania (single-flight per klucz) — trzymany tylko przez
    // chwilę, bez żadnego I/O. _remote szereguje zapytania do konta DeepL między kolejnymi
    // przygotowaniami (lista przed utworzeniem = brak duplikatów), ale czeka na niego
    // wyłącznie zadanie w tle, nigdy tłumaczenie.
    private readonly Lock _state = new();
    private readonly SemaphoreSlim _remote = new(1, 1);
    private volatile Ready? _ready;
    private Preparation? _pending;
    private long _disabledUntilTicks = long.MinValue;

    private sealed record Ready(string Key, string GlossaryId);

    private sealed class Preparation(string key)
    {
        public string Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<string?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<string?>(null);
    }

    private sealed record PreparationRequest(
        string Key, string ApiKey, string BaseUrl, string Source, string Target, string Name, string Tsv, int EntryCount);

    /// <summary>
    /// Id glosariusza dla tego słownika i pary języków albo null (brak terminów, glosariusz
    /// chwilowo niedostępny albo jeszcze w przygotowaniu). Gotowy glosariusz wraca od razu;
    /// nowy jest przygotowywany w tle, a wywołujący czeka na niego najwyżej
    /// <see cref="DeepLOptions.GlossaryWaitBudget"/> — potem ta partia idzie bez glosariusza.
    /// </summary>
    public async Task<string?> GetGlossaryIdAsync(
        IReadOnlyList<GlossaryTerm> terms,
        string apiKey,
        string baseUrl,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var entries = BuildEntries(terms);
        if (entries.Count == 0) return null;

        var source = GlossaryLanguage(sourceLanguage);
        var target = GlossaryLanguage(targetLanguage);
        var tsv = ToTsv(entries);
        var contentHash = ShortHash(tsv);
        // Klucz tylko w pamięci: inne konto DeepL (inny klucz) ma własne glosariusze.
        var key = $"{ShortHash(apiKey)}|{baseUrl}|{source}|{target}|{contentHash}";

        if (_ready is { } ready && ready.Key == key) return ready.GlossaryId;
        if (IsCoolingDown()) return null;

        Task<string?> preparation;
        lock (_state)
        {
            if (_ready is { } again && again.Key == key) return again.GlossaryId;

            if (_pending is { } pending && pending.Key == key && !pending.Task.IsCompleted)
            {
                preparation = pending.Task;
            }
            else
            {
                // Zmiana słownika, konta albo pary języków — stare przygotowanie jest już
                // niepotrzebne; przerywamy jego zapytania, żeby nie zajmowało kolejki.
                _pending?.Cancellation.Cancel();
                var job = new Preparation(key);
                var request = new PreparationRequest(
                    key, apiKey, baseUrl, source, target, NamePrefix + contentHash, tsv, entries.Count);
                // Zadanie startuje wewnątrz blokady, a jego „finally” też ją bierze — więc
                // nie może się zakończyć, zanim _pending wskaże właśnie to zadanie.
                job.Task = Task.Run(() => PrepareAsync(job, request), CancellationToken.None);
                _pending = job;
                preparation = job.Task;
            }
        }

        return await WaitWithinBudgetAsync(preparation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> WaitWithinBudgetAsync(Task<string?> preparation, CancellationToken cancellationToken)
    {
        if (preparation.IsCompleted) return await preparation.ConfigureAwait(false);

        var budget = options.GlossaryWaitBudget;
        if (budget <= TimeSpan.Zero) return null;

        try
        {
            return await preparation.WaitAsync(budget, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Przygotowanie trwa dalej w tle — kolejne partie użyją gotowego glosariusza.
            logger.LogDebug("Glosariusz DeepL jeszcze niegotowy — ta partia bez glosariusza");
            return null;
        }
    }

    private async Task<string?> PrepareAsync(Preparation job, PreparationRequest request)
    {
        var cancellationToken = job.Cancellation.Token;
        try
        {
            await _remote.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ready is { } done && done.Key == request.Key) return done.GlossaryId;
                if (IsCoolingDown()) return null;

                var existing = await ListAsync(request.ApiKey, request.BaseUrl, cancellationToken).ConfigureAwait(false);
                var reusable = existing.FirstOrDefault(g =>
                    g.Name == request.Name && Same(g.SourceLang, request.Source) && Same(g.TargetLang, request.Target)
                    && g.Ready != false);
                var glossaryId = reusable?.GlossaryId
                    ?? await CreateAsync(request.ApiKey, request.BaseUrl, request.Name, request.Source, request.Target,
                        request.Tsv, cancellationToken).ConfigureAwait(false);

                _ready = new Ready(request.Key, glossaryId);
                logger.LogInformation("Glosariusz DeepL gotowy ({Entries} wpisów, {Mode})",
                    request.EntryCount, reusable is null ? "nowy" : "ponownie użyty");

                // Poprzednie wersje słownika tej aplikacji dla tej pary języków są już niepotrzebne.
                // Usuwamy je w tle — sprzątanie nie może opóźniać tłumaczenia, na które czeka gracz,
                // ani zostać przerwane zmianą sceny.
                var stale = existing
                    .Where(g => g.GlossaryId != glossaryId && g.Name?.StartsWith(NamePrefix, StringComparison.Ordinal) == true
                                && Same(g.SourceLang, request.Source) && Same(g.TargetLang, request.Target))
                    .Select(static g => g.GlossaryId!)
                    .ToList();
                if (stale.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        foreach (var id in stale)
                            await TryDeleteAsync(request.ApiKey, request.BaseUrl, id, CancellationToken.None).ConfigureAwait(false);
                    }, CancellationToken.None);
                }

                return glossaryId;
            }
            finally
            {
                _remote.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Słownik zmienił się w trakcie — nowsze przygotowanie przejęło sprawę.
            return null;
        }
        catch (TranslationException ex)
        {
            DisableForAWhile();
            logger.LogWarning("Glosariusz DeepL niedostępny ({Kind}: {Message}) — tłumaczę bez niego przez {Minutes} min",
                ex.Kind, ex.Message, FailureCooldown.TotalMinutes);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Zadanie w tle nie może zostawić nieobsłużonego wyjątku — traktujemy to jak awarię.
            DisableForAWhile();
            logger.LogWarning("Glosariusz DeepL niedostępny ({Type}) — tłumaczę bez niego przez {Minutes} min",
                ex.GetType().Name, FailureCooldown.TotalMinutes);
            return null;
        }
        finally
        {
            lock (_state)
            {
                if (ReferenceEquals(_pending, job)) _pending = null;
            }
        }
    }

    /// <summary>
    /// Ustawienia pojedynczego zapytania o glosariusze: krótszy limit czasu i bez ponawiania —
    /// glosariusz jest dodatkiem, a nie powodem, żeby kolejka tła wisiała sekundami.
    /// </summary>
    private HttpProviderOptions GlossaryRequestOptions() => new()
    {
        RequestTimeout = options.GlossaryRequestTimeout > TimeSpan.Zero ? options.GlossaryRequestTimeout : options.RequestTimeout,
        MaxRetries = 0,
        MaxRetryDelay = options.MaxRetryDelay,
    };

    /// <summary>Tłumaczenie z tym glosariuszem się nie powiodło — przez pewien czas bez glosariusza.</summary>
    public void Invalidate(string glossaryId)
    {
        if (_ready?.GlossaryId == glossaryId) _ready = null;
        DisableForAWhile();
    }

    private bool IsCoolingDown() => _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _disabledUntilTicks);

    private void DisableForAWhile() =>
        Interlocked.Exchange(ref _disabledUntilTicks, (_time.GetUtcNow() + FailureCooldown).UtcTicks);

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>„EN-GB” → „en”: glosariusze DeepL używają języków bez wariantu regionalnego.</summary>
    internal static string GlossaryLanguage(string language)
    {
        var trimmed = language.Trim();
        var dash = trimmed.IndexOf('-');
        return (dash > 0 ? trimmed[..dash] : trimmed).ToLowerInvariant();
    }

    /// <summary>
    /// Wpisy w formacie akceptowanym przez DeepL: bez pustych pól, tabulatorów, końców linii
    /// i znaków sterujących, każde źródło raz. Zwycięzcę wybiera ta sama reguła co lokalne
    /// tłumaczenie (<see cref="GlossaryPrecedence"/>: priorytet, termin z rozróżnianiem
    /// wielkości liter, a przy remisie późniejszy), więc DeepL tłumaczy termin tak samo jak
    /// <see cref="IGlossaryService.TryTranslateExact"/>. Terminy „label” (tylko całe etykiety)
    /// nie trafiają do glosariusza — DeepL podmieniałby je także w zdaniach. Kolejność wpisów
    /// stała (alfabetyczna), żeby skrót treści nie zależał od kolejności wczytania słowników.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Target)> BuildEntries(IReadOnlyList<GlossaryTerm> terms)
    {
        var winners = new Dictionary<string, GlossaryTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (term.IsLabelOnly) continue;
            var source = Clean(term.Source);
            var target = Clean(term.Target);
            if (source.Length == 0 || target.Length == 0) continue;
            var cleaned = term with { Source = source, Target = target };

            if (!winners.TryGetValue(source, out var existing) || GlossaryPrecedence.Replaces(existing, cleaned))
            {
                winners[source] = cleaned;
            }
        }

        return winners.Values
            .OrderBy(static t => t.Source, StringComparer.Ordinal)
            .Take(MaxEntries)
            .Select(static t => (t.Source, t.Target))
            .ToList();
    }

    private static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string ToTsv(IReadOnlyList<(string Source, string Target)> entries) =>
        string.Join('\n', entries.Select(static e => e.Source + "\t" + e.Target));

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private HttpRequestMessage Request(HttpMethod method, string url, string apiKey, HttpContent? content = null)
    {
        var message = new HttpRequestMessage(method, url) { Content = content };
        message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        return message;
    }

    private async Task<IReadOnlyList<GlossaryInfo>> ListAsync(string apiKey, string baseUrl, CancellationToken cancellationToken)
    {
        using var response = await ProviderHttp.SendAsync(
            httpClient,
            () => Request(HttpMethod.Get, $"{baseUrl}/v2/glossaries", apiKey),
            DeepLTranslationProvider.ProviderName, GlossaryRequestOptions(), MapError, logger, cancellationToken,
            allowRetry: false).ConfigureAwait(false);
        var payload = await ProviderHttp.ReadJsonAsync<GlossaryList>(response, DeepLTranslationProvider.ProviderName, cancellationToken)
            .ConfigureAwait(false);
        return payload?.Glossaries?.Where(static g => !string.IsNullOrEmpty(g.GlossaryId)).ToList() ?? [];
    }

    private async Task<string> CreateAsync(
        string apiKey, string baseUrl, string name, string source, string target, string tsv, CancellationToken cancellationToken)
    {
        var body = new CreateGlossaryRequest(name, source, target, tsv, "tsv");
        // Bez ponawiania: po przekroczeniu czasu glosariusz mógł jednak powstać — kolejna
        // próba (po przerwie) najpierw go znajdzie na liście, zamiast tworzyć duplikat.
        using var response = await ProviderHttp.SendAsync(
            httpClient,
            () => Request(HttpMethod.Post, $"{baseUrl}/v2/glossaries", apiKey, JsonContent.Create(body)),
            DeepLTranslationProvider.ProviderName, GlossaryRequestOptions(), MapError, logger, cancellationToken,
            allowRetry: false).ConfigureAwait(false);
        var created = await ProviderHttp.ReadJsonAsync<GlossaryInfo>(response, DeepLTranslationProvider.ProviderName, cancellationToken)
            .ConfigureAwait(false);
        return created?.GlossaryId is { Length: > 0 } id
            ? id
            : throw new TranslationException(TranslationFailureKind.Unknown, "DeepL nie zwrócił identyfikatora glosariusza.");
    }

    private async Task TryDeleteAsync(string apiKey, string baseUrl, string glossaryId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await ProviderHttp.SendAsync(
                httpClient,
                () => Request(HttpMethod.Delete, $"{baseUrl}/v2/glossaries/{Uri.EscapeDataString(glossaryId)}", apiKey),
                DeepLTranslationProvider.ProviderName, GlossaryRequestOptions(), MapError, logger, cancellationToken,
                allowRetry: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TranslationException or OperationCanceledException)
        {
            // Sprzątanie jest nieobowiązkowe — stary glosariusz najwyżej zostanie na koncie.
            logger.LogDebug("Nie usunięto starego glosariusza DeepL: {Message}", ex.Message);
        }
    }

    private static TranslationException MapError(ProviderHttpFailure failure) => failure.StatusCode switch
    {
        401 or 403 => new TranslationException(TranslationFailureKind.InvalidApiKey,
            "DeepL odrzucił dostęp do glosariuszy (HTTP 403)."),
        456 => new TranslationException(TranslationFailureKind.QuotaExceeded,
            "Osiągnięto limit glosariuszy na koncie DeepL (HTTP 456)."),
        429 => new TranslationException(TranslationFailureKind.RateLimited, "DeepL ogranicza liczbę zapytań (HTTP 429)."),
        400 => new TranslationException(TranslationFailureKind.InvalidRequest,
            "DeepL odrzucił glosariusz (HTTP 400) — np. nieobsługiwana para języków."),
        >= 500 => new TranslationException(TranslationFailureKind.ServiceUnavailable,
            string.Create(CultureInfo.InvariantCulture, $"DeepL jest chwilowo niedostępny (HTTP {failure.StatusCode}).")),
        _ => new TranslationException(TranslationFailureKind.Unknown,
            string.Create(CultureInfo.InvariantCulture, $"DeepL zwrócił nieoczekiwany status HTTP {failure.StatusCode} dla glosariusza.")),
    };

    private sealed record CreateGlossaryRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("source_lang")] string SourceLang,
        [property: JsonPropertyName("target_lang")] string TargetLang,
        [property: JsonPropertyName("entries")] string Entries,
        [property: JsonPropertyName("entries_format")] string EntriesFormat);

    private sealed record GlossaryList(
        [property: JsonPropertyName("glossaries")] List<GlossaryInfo>? Glossaries);

    private sealed record GlossaryInfo(
        [property: JsonPropertyName("glossary_id")] string? GlossaryId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("source_lang")] string? SourceLang,
        [property: JsonPropertyName("target_lang")] string? TargetLang,
        [property: JsonPropertyName("ready")] bool? Ready);
}
