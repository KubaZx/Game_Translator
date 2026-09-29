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
/// Błąd glosariusza nigdy nie blokuje tłumaczenia — przez pewien czas tłumaczymy bez niego.
/// </summary>
internal sealed class DeepLGlossaryManager(
    HttpClient httpClient,
    HttpProviderOptions options,
    ILogger logger,
    TimeProvider? timeProvider = null)
{
    internal const string NamePrefix = "GameTranslatorOverlay ";
    internal const int MaxEntries = 10_000;
    internal static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Ready? _ready;
    private long _disabledUntilTicks = long.MinValue;

    private sealed record Ready(string Key, string GlossaryId);

    /// <summary>
    /// Id glosariusza dla tego słownika i pary języków albo null (brak terminów, glosariusz
    /// chwilowo niedostępny). Pierwsze wywołanie dla nowej zawartości słownika tworzy glosariusz.
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
        var name = NamePrefix + contentHash;
        // Klucz tylko w pamięci: inne konto DeepL (inny klucz) ma własne glosariusze.
        var key = $"{ShortHash(apiKey)}|{baseUrl}|{source}|{target}|{contentHash}";

        if (_ready is { } ready && ready.Key == key) return ready.GlossaryId;
        if (IsCoolingDown()) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready is { } again && again.Key == key) return again.GlossaryId;
            if (IsCoolingDown()) return null;

            var existing = await ListAsync(apiKey, baseUrl, cancellationToken).ConfigureAwait(false);
            var reusable = existing.FirstOrDefault(g =>
                g.Name == name && Same(g.SourceLang, source) && Same(g.TargetLang, target) && g.Ready != false);
            var glossaryId = reusable?.GlossaryId
                ?? await CreateAsync(apiKey, baseUrl, name, source, target, tsv, cancellationToken).ConfigureAwait(false);

            _ready = new Ready(key, glossaryId);
            logger.LogInformation("Glosariusz DeepL gotowy ({Entries} wpisów, {Mode})",
                entries.Count, reusable is null ? "nowy" : "ponownie użyty");

            // Poprzednie wersje słownika tej aplikacji dla tej pary języków są już niepotrzebne.
            // Usuwamy je w tle — sprzątanie nie może opóźniać tłumaczenia, na które czeka gracz,
            // ani zostać przerwane zmianą sceny.
            var stale = existing
                .Where(g => g.GlossaryId != glossaryId && g.Name?.StartsWith(NamePrefix, StringComparison.Ordinal) == true
                            && Same(g.SourceLang, source) && Same(g.TargetLang, target))
                .Select(static g => g.GlossaryId!)
                .ToList();
            if (stale.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var id in stale)
                        await TryDeleteAsync(apiKey, baseUrl, id, CancellationToken.None).ConfigureAwait(false);
                }, CancellationToken.None);
            }

            return glossaryId;
        }
        catch (TranslationException ex)
        {
            DisableForAWhile();
            logger.LogWarning("Glosariusz DeepL niedostępny ({Kind}: {Message}) — tłumaczę bez niego przez {Minutes} min",
                ex.Kind, ex.Message, FailureCooldown.TotalMinutes);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

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
    /// i znaków sterujących, każde źródło raz (wygrywa wyższy priorytet, potem termin
    /// z rozróżnianiem wielkości liter). Kolejność stała, żeby skrót treści nie zależał
    /// od kolejności wczytania słowników.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Target)> BuildEntries(IReadOnlyList<GlossaryTerm> terms)
    {
        var winners = new Dictionary<string, GlossaryTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            var source = Clean(term.Source);
            var target = Clean(term.Target);
            if (source.Length == 0 || target.Length == 0) continue;
            var cleaned = term with { Source = source, Target = target };

            if (!winners.TryGetValue(source, out var existing)
                || cleaned.Priority > existing.Priority
                || (cleaned.Priority == existing.Priority && cleaned.CaseSensitive && !existing.CaseSensitive))
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
            DeepLTranslationProvider.ProviderName, options, MapError, logger, cancellationToken).ConfigureAwait(false);
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
            DeepLTranslationProvider.ProviderName, options, MapError, logger, cancellationToken,
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
                DeepLTranslationProvider.ProviderName, options, MapError, logger, cancellationToken,
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
