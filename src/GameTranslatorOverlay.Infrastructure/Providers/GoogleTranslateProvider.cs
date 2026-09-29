using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed class GoogleTranslateOptions : HttpProviderOptions
{
    /// <summary>API przyjmuje do 128 segmentów na zapytanie.</summary>
    public int MaxBatchSize { get; set; } = 100;
}

/// <summary>
/// Google Cloud Translation (Basic, v2) z kluczem API projektu. Klucz idzie w nagłówku
/// X-goog-api-key (nie w adresie, który mógłby trafić do logów pośredników) i nigdy
/// nie jest logowany. format=text wyłącza zamianę znaków na encje HTML.
/// </summary>
public sealed class GoogleTranslateProvider(
    HttpClient httpClient,
    Func<string?> apiKeyAccessor,
    GoogleTranslateOptions? options = null,
    ILogger<GoogleTranslateProvider>? logger = null) : ITranslationProvider, IWarmableTranslationProvider
{
    public const string ProviderName = "Google";
    private const string Endpoint = "https://translation.googleapis.com/language/translate/v2";

    private readonly GoogleTranslateOptions _options = options ?? new GoogleTranslateOptions();
    private readonly ILogger _logger = logger ?? NullLogger<GoogleTranslateProvider>.Instance;

    public string Name => ProviderName;
    public bool RequiresApiKey => true;

    internal static string MapLanguage(string language) => language.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];

        var apiKey = apiKeyAccessor()?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new TranslationException(TranslationFailureKind.MissingApiKey, "Nie skonfigurowano klucza Google Cloud Translation.");
        }

        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(Math.Max(1, _options.MaxBatchSize)))
        {
            var request = new GoogleTranslateRequest(chunk, MapLanguage(sourceLanguage), MapLanguage(targetLanguage));
            using var response = await ProviderHttp.SendAsync(
                httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(request) };
                    message.Headers.Add("X-goog-api-key", apiKey);
                    return message;
                },
                ProviderName,
                _options,
                MapError,
                _logger,
                cancellationToken,
                isRetryable: static failure =>
                    (failure.StatusCode == 429 && !IsDailyQuota(failure)) || failure.StatusCode >= 500).ConfigureAwait(false);

            var payload = await ProviderHttp.ReadJsonAsync<GoogleTranslateResponse>(response, ProviderName, cancellationToken)
                .ConfigureAwait(false);
            var translations = payload?.Data?.Translations;
            if (translations is null || translations.Count != chunk.Length)
            {
                throw new TranslationException(TranslationFailureKind.Unknown, "Google Translate zwrócił niekompletną odpowiedź.");
            }
            results.AddRange(translations.Select(static t => t.TranslatedText ?? string.Empty));
        }
        return results;
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(apiKeyAccessor())
            ? Task.CompletedTask
            : ProviderHttp.WarmUpAsync(httpClient, new Uri("https://translation.googleapis.com/"), cancellationToken);

    private static bool IsDailyQuota(ProviderHttpFailure failure) =>
        failure.BodyContains("dailyLimitExceeded") || failure.BodyContains("Daily Limit") || failure.BodyContains("quotaExceeded");

    private static TranslationException MapError(ProviderHttpFailure failure) => failure.StatusCode switch
    {
        400 when failure.BodyContains("API key not valid") || failure.BodyContains("API_KEY_INVALID") =>
            new TranslationException(TranslationFailureKind.InvalidApiKey, "Google odrzucił klucz API (HTTP 400)."),
        401 => new TranslationException(TranslationFailureKind.InvalidApiKey, "Google odrzucił klucz API (HTTP 401)."),
        403 or 429 when IsDailyQuota(failure) => new TranslationException(TranslationFailureKind.QuotaExceeded,
            $"Limit Google Cloud Translation został wyczerpany (HTTP {failure.StatusCode})."),
        403 when failure.BodyContains("SERVICE_DISABLED") || failure.BodyContains("accessNotConfigured") =>
            new TranslationException(TranslationFailureKind.InvalidApiKey,
                "Cloud Translation API nie jest włączone w projekcie Google tego klucza (HTTP 403)."),
        403 => new TranslationException(TranslationFailureKind.InvalidApiKey,
            "Google odmówił dostępu (HTTP 403) — sprawdź ograniczenia klucza i rozliczenia projektu."),
        429 => new TranslationException(TranslationFailureKind.RateLimited, "Google ogranicza liczbę zapytań (HTTP 429)."),
        400 => new TranslationException(TranslationFailureKind.InvalidRequest, "Google odrzucił żądanie (HTTP 400)."),
        413 => new TranslationException(TranslationFailureKind.TextTooLong, "Tekst jest zbyt długi dla Google Translate."),
        >= 500 => new TranslationException(TranslationFailureKind.ServiceUnavailable,
            $"Google Translate jest chwilowo niedostępny (HTTP {failure.StatusCode})."),
        _ => new TranslationException(TranslationFailureKind.Unknown,
            $"Google Translate zwrócił nieoczekiwany status HTTP {failure.StatusCode}."),
    };

    public async Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKeyAccessor()))
        {
            return new ProviderStatus(false, "Brak klucza Google Cloud Translation. Wpisz klucz i zapisz go, zanim przetestujesz połączenie.");
        }

        return await ProviderHttp.TestAsync(async () =>
        {
            var result = await TranslateBatchAsync(["Hello"], "en", "pl", cancellationToken).ConfigureAwait(false);
            return new ProviderStatus(true, $"Połączono z Google Cloud Translation: „Hello” → „{result[0]}”.");
        }).ConfigureAwait(false);
    }

    private sealed record GoogleTranslateRequest(
        [property: JsonPropertyName("q")] IReadOnlyList<string> Q,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("format")] string Format = "text");

    private sealed record GoogleTranslateResponse(
        [property: JsonPropertyName("data")] GoogleTranslateData? Data);

    private sealed record GoogleTranslateData(
        [property: JsonPropertyName("translations")] List<GoogleTranslation>? Translations);

    private sealed record GoogleTranslation(
        [property: JsonPropertyName("translatedText")] string? TranslatedText);
}
