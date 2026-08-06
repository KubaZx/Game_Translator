using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed class DeepLOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxRetries { get; set; } = 2;
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>
    /// Górny limit czekania między ponowieniami. Nagłówek Retry-After powyżej tej wartości
    /// oznacza rezygnację z retry — użytkownik dostaje od razu czytelny błąd zamiast
    /// zawieszonej na godziny aplikacji.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Dostawca DeepL API. Klucze z sufiksem „:fx” trafiają na api-free.deepl.com,
/// pozostałe na api.deepl.com. Klucz nigdy nie jest logowany.
/// </summary>
public sealed class DeepLTranslationProvider(
    HttpClient httpClient,
    Func<string?> apiKeyAccessor,
    DeepLOptions? options = null,
    ILogger<DeepLTranslationProvider>? logger = null) : ITranslationProvider
{
    public const string ProviderName = "DeepL";

    private readonly DeepLOptions _options = options ?? new DeepLOptions();
    private readonly ILogger _logger = logger ?? NullLogger<DeepLTranslationProvider>.Instance;

    public string Name => ProviderName;
    public bool RequiresApiKey => true;

    internal static string GetBaseUrl(string apiKey) =>
        apiKey.TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com"
            : "https://api.deepl.com";

    internal static string MapLanguage(string language) => language.Trim().ToUpperInvariant();

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];

        var apiKey = apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationException(TranslationFailureKind.MissingApiKey, "Nie skonfigurowano klucza API DeepL.");
        }

        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(Math.Max(1, _options.MaxBatchSize)))
        {
            results.AddRange(await TranslateChunkAsync(chunk, apiKey, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> TranslateChunkAsync(
        string[] chunk, string apiKey, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var request = new DeepLTranslateRequest(chunk, MapLanguage(sourceLanguage), MapLanguage(targetLanguage));
        var url = $"{GetBaseUrl(apiKey)}/v2/translate";

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.RequestTimeout);

                using var message = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(request),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
                response = await httpClient.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new TranslationException(TranslationFailureKind.Timeout,
                    $"DeepL nie odpowiedział w ciągu {_options.RequestTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new TranslationException(TranslationFailureKind.NetworkError,
                    "Nie udało się połączyć z DeepL. Sprawdź połączenie z internetem.", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<DeepLTranslateResponse>(cancellationToken)
                        .ConfigureAwait(false);
                    var translations = payload?.Translations?.Select(static t => t.Text ?? string.Empty).ToList();

                    if (translations is null || translations.Count != chunk.Length)
                    {
                        throw new TranslationException(TranslationFailureKind.Unknown,
                            "DeepL zwrócił niekompletną odpowiedź.");
                    }
                    return translations;
                }

                var status = (int)response.StatusCode;
                var retryable = status == 429 || status >= 500;
                var retryAfter = response.Headers.RetryAfter?.Delta;

                // Serwer/pośrednik może przysłać absurdalny Retry-After (godziny) —
                // wtedy nie ponawiamy, tylko od razu zwracamy czytelny błąd.
                if (retryAfter > _options.MaxRetryDelay)
                {
                    retryable = false;
                }

                if (retryable && attempt < _options.MaxRetries)
                {
                    var delay = retryAfter ?? TimeSpan.FromSeconds(attempt + 1);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    _logger.LogInformation("DeepL zwrócił {Status} — ponawiam za {Delay} (próba {Attempt}/{Max})",
                        status, delay, attempt + 1, _options.MaxRetries);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw MapError(response.StatusCode);
            }
        }
    }

    private static TranslationException MapError(HttpStatusCode statusCode) => (int)statusCode switch
    {
        401 or 403 => new TranslationException(TranslationFailureKind.InvalidApiKey, "DeepL odrzucił klucz API (HTTP 403)."),
        456 => new TranslationException(TranslationFailureKind.QuotaExceeded, "Limit znaków DeepL został wyczerpany (HTTP 456)."),
        429 => new TranslationException(TranslationFailureKind.RateLimited, "DeepL ogranicza liczbę zapytań (HTTP 429)."),
        400 => new TranslationException(TranslationFailureKind.InvalidRequest, "DeepL odrzucił żądanie (HTTP 400)."),
        413 or 414 => new TranslationException(TranslationFailureKind.TextTooLong, "Tekst jest zbyt długi dla DeepL."),
        >= 500 => new TranslationException(TranslationFailureKind.ServiceUnavailable, $"DeepL jest chwilowo niedostępny (HTTP {(int)statusCode})."),
        _ => new TranslationException(TranslationFailureKind.Unknown, $"DeepL zwrócił nieoczekiwany status HTTP {(int)statusCode}."),
    };

    public async Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ProviderStatus(false, "Brak klucza API DeepL. Wpisz klucz i zapisz go, zanim przetestujesz połączenie.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.RequestTimeout);

            using var message = new HttpRequestMessage(HttpMethod.Get, $"{GetBaseUrl(apiKey)}/v2/usage");
            message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
            using var response = await httpClient.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderStatus(false, MapError(response.StatusCode).UserFriendlyMessage);
            }

            var usage = await response.Content.ReadFromJsonAsync<DeepLUsageResponse>(cancellationToken).ConfigureAwait(false);
            return new ProviderStatus(
                true,
                $"Połączono z DeepL. Zużycie: {usage?.CharacterCount:N0} / {usage?.CharacterLimit:N0} znaków.",
                usage?.CharacterCount,
                usage?.CharacterLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProviderStatus(false, "DeepL nie odpowiedział w wyznaczonym czasie.");
        }
        catch (HttpRequestException)
        {
            return new ProviderStatus(false, "Nie udało się połączyć z DeepL. Sprawdź połączenie z internetem.");
        }
    }

    private sealed record DeepLTranslateRequest(
        [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
        [property: JsonPropertyName("source_lang")] string SourceLang,
        [property: JsonPropertyName("target_lang")] string TargetLang);

    private sealed record DeepLTranslateResponse(
        [property: JsonPropertyName("translations")] List<DeepLTranslationItem>? Translations);

    private sealed record DeepLTranslationItem(
        [property: JsonPropertyName("detected_source_language")] string? DetectedSourceLanguage,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record DeepLUsageResponse(
        [property: JsonPropertyName("character_count")] long CharacterCount,
        [property: JsonPropertyName("character_limit")] long CharacterLimit);
}
