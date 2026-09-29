using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed class AzureTranslatorOptions : HttpProviderOptions
{
    /// <summary>API przyjmuje do 1000 elementów i 50 000 znaków na zapytanie; zostawiamy zapas.</summary>
    public int MaxBatchSize { get; set; } = 100;
}

/// <summary>
/// Microsoft Azure AI Translator (Text Translation v3). Darmowy plan F0 obejmuje
/// 2 mln znaków miesięcznie. Region jest wymagany dla zasobów regionalnych
/// (np. „westeurope”); zasób globalny działa bez niego. Klucz nigdy nie jest logowany.
/// </summary>
public sealed class AzureTranslatorProvider(
    HttpClient httpClient,
    Func<string?> apiKeyAccessor,
    Func<string?> regionAccessor,
    AzureTranslatorOptions? options = null,
    ILogger<AzureTranslatorProvider>? logger = null) : ITranslationProvider
{
    public const string ProviderName = "Azure";
    private const string Endpoint = "https://api.cognitive.microsofttranslator.com/translate";

    private readonly AzureTranslatorOptions _options = options ?? new AzureTranslatorOptions();
    private readonly ILogger _logger = logger ?? NullLogger<AzureTranslatorProvider>.Instance;

    public string Name => ProviderName;
    public bool RequiresApiKey => true;

    internal static string MapLanguage(string language) => language.Trim().ToLowerInvariant();

    /// <summary>Pusty region = zasób globalny; inaczej nazwa regionu Azure (litery, cyfry, myślniki).</summary>
    public static bool IsValidRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) || region.Trim().All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

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
            throw new TranslationException(TranslationFailureKind.MissingApiKey, "Nie skonfigurowano klucza Azure Translator.");
        }
        var region = regionAccessor()?.Trim();
        if (!IsValidRegion(region))
        {
            throw new TranslationException(TranslationFailureKind.InvalidConfiguration,
                "Region Azure może zawierać tylko litery, cyfry i myślniki (np. westeurope).");
        }

        var url = $"{Endpoint}?api-version=3.0&from={Uri.EscapeDataString(MapLanguage(sourceLanguage))}" +
                  $"&to={Uri.EscapeDataString(MapLanguage(targetLanguage))}&textType=plain";

        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(Math.Max(1, _options.MaxBatchSize)))
        {
            var body = chunk.Select(static text => new AzureTextItem(text)).ToArray();
            using var response = await ProviderHttp.SendAsync(
                httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                    message.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
                    if (!string.IsNullOrEmpty(region))
                    {
                        message.Headers.Add("Ocp-Apim-Subscription-Region", region);
                    }
                    return message;
                },
                ProviderName,
                _options,
                MapError,
                _logger,
                cancellationToken,
                // 403 Azure oznacza wyczerpany limit darmowego planu — ponawianie nic nie da.
                isRetryable: static failure => failure.StatusCode is 408 or 429 || failure.StatusCode >= 500).ConfigureAwait(false);

            var payload = await ProviderHttp.ReadJsonAsync<List<AzureTranslationResult>>(response, ProviderName, cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || payload.Count != chunk.Length)
            {
                throw new TranslationException(TranslationFailureKind.Unknown, "Azure Translator zwrócił niekompletną odpowiedź.");
            }
            results.AddRange(payload.Select(static item => item.Translations is { Count: > 0 } t ? t[0].Text ?? string.Empty : string.Empty));
        }
        return results;
    }

    private static TranslationException MapError(ProviderHttpFailure failure) => failure.StatusCode switch
    {
        401 => new TranslationException(TranslationFailureKind.InvalidApiKey,
            "Azure Translator odrzucił klucz (HTTP 401). Sprawdź klucz i region zasobu."),
        403 => new TranslationException(TranslationFailureKind.QuotaExceeded,
            "Limit znaków Azure Translator został wyczerpany (HTTP 403)."),
        408 => new TranslationException(TranslationFailureKind.Timeout, "Azure Translator nie zdążył odpowiedzieć (HTTP 408)."),
        429 => new TranslationException(TranslationFailureKind.RateLimited, "Azure Translator ogranicza liczbę zapytań (HTTP 429)."),
        400 when failure.BodyContains("language") => new TranslationException(TranslationFailureKind.InvalidRequest,
            "Azure Translator nie obsługuje wybranej pary języków (HTTP 400)."),
        400 => new TranslationException(TranslationFailureKind.InvalidRequest, "Azure Translator odrzucił żądanie (HTTP 400)."),
        413 => new TranslationException(TranslationFailureKind.TextTooLong, "Tekst jest zbyt długi dla Azure Translator."),
        >= 500 => new TranslationException(TranslationFailureKind.ServiceUnavailable,
            $"Azure Translator jest chwilowo niedostępny (HTTP {failure.StatusCode})."),
        _ => new TranslationException(TranslationFailureKind.Unknown,
            $"Azure Translator zwrócił nieoczekiwany status HTTP {failure.StatusCode}."),
    };

    public async Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKeyAccessor()))
        {
            return new ProviderStatus(false, "Brak klucza Azure Translator. Wpisz klucz i zapisz go, zanim przetestujesz połączenie.");
        }

        // Azure nie ma endpointu zużycia dla kluczy — próba to tłumaczenie krótkiego tekstu (kilka znaków).
        return await ProviderHttp.TestAsync(async () =>
        {
            var result = await TranslateBatchAsync(["Hello"], "en", "pl", cancellationToken).ConfigureAwait(false);
            var region = regionAccessor()?.Trim();
            var where = string.IsNullOrEmpty(region) ? "zasób globalny" : $"region {region}";
            return new ProviderStatus(true, $"Połączono z Azure Translator ({where}): „Hello” → „{result[0]}”.");
        }).ConfigureAwait(false);
    }

    private sealed record AzureTextItem([property: JsonPropertyName("Text")] string Text);

    private sealed record AzureTranslationResult(
        [property: JsonPropertyName("translations")] List<AzureTranslation>? Translations);

    private sealed record AzureTranslation(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("to")] string? To);
}
