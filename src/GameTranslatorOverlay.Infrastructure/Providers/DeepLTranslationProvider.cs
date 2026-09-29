using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed class DeepLOptions : HttpProviderOptions
{
    public int MaxBatchSize { get; set; } = 50;
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

        // Teksty z jednej klatki są dla siebie kontekstem: krótka kwestia („Fine.”, „Leave.”)
        // tłumaczona w izolacji bywa losowa, a z sąsiednimi blokami trafia w sens.
        // DeepL nie tłumaczy ani nie bilinguje parametru context.
        var context = BuildContext(texts);

        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(Math.Max(1, _options.MaxBatchSize)))
        {
            results.AddRange(await TranslateChunkAsync(chunk, context, apiKey, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    private const int MaxContextChars = 1500;

    private static string? BuildContext(IReadOnlyList<string> texts)
    {
        if (texts.Count < 2) return null;
        var builder = new System.Text.StringBuilder();
        foreach (var text in texts)
        {
            var line = text.Replace('\n', ' ').Trim();
            if (line.Length == 0) continue;
            if (builder.Length + line.Length + 1 > MaxContextChars) break;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }
        return builder.Length > 0 ? builder.ToString() : null;
    }

    private async Task<IReadOnlyList<string>> TranslateChunkAsync(
        string[] chunk, string? context, string apiKey, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var request = new DeepLTranslateRequest(chunk, MapLanguage(sourceLanguage), MapLanguage(targetLanguage), context);
        var url = $"{GetBaseUrl(apiKey)}/v2/translate";

        using var response = await ProviderHttp.SendAsync(
            httpClient,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(request) };
                message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
                return message;
            },
            ProviderName,
            _options,
            MapError,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var payload = await ProviderHttp.ReadJsonAsync<DeepLTranslateResponse>(response, ProviderName, cancellationToken)
            .ConfigureAwait(false);
        var translations = payload?.Translations?.Select(static t => t.Text ?? string.Empty).ToList();

        if (translations is null || translations.Count != chunk.Length)
        {
            throw new TranslationException(TranslationFailureKind.Unknown,
                "DeepL zwrócił niekompletną odpowiedź.");
        }
        return translations;
    }

    private static TranslationException MapError(ProviderHttpFailure failure) => failure.StatusCode switch
    {
        401 or 403 => new TranslationException(TranslationFailureKind.InvalidApiKey, "DeepL odrzucił klucz API (HTTP 403)."),
        456 => new TranslationException(TranslationFailureKind.QuotaExceeded, "Limit znaków DeepL został wyczerpany (HTTP 456)."),
        429 => new TranslationException(TranslationFailureKind.RateLimited, "DeepL ogranicza liczbę zapytań (HTTP 429)."),
        400 => new TranslationException(TranslationFailureKind.InvalidRequest, "DeepL odrzucił żądanie (HTTP 400)."),
        413 or 414 => new TranslationException(TranslationFailureKind.TextTooLong, "Tekst jest zbyt długi dla DeepL."),
        >= 500 => new TranslationException(TranslationFailureKind.ServiceUnavailable, $"DeepL jest chwilowo niedostępny (HTTP {failure.StatusCode})."),
        _ => new TranslationException(TranslationFailureKind.Unknown, $"DeepL zwrócił nieoczekiwany status HTTP {failure.StatusCode}."),
    };

    public async Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ProviderStatus(false, "Brak klucza API DeepL. Wpisz klucz i zapisz go, zanim przetestujesz połączenie.");
        }

        return await ProviderHttp.TestAsync(async () =>
        {
            using var response = await ProviderHttp.SendAsync(
                httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Get, $"{GetBaseUrl(apiKey)}/v2/usage");
                    message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
                    return message;
                },
                ProviderName,
                _options,
                MapError,
                _logger,
                cancellationToken,
                allowRetry: false).ConfigureAwait(false);

            var usage = await ProviderHttp.ReadJsonAsync<DeepLUsageResponse>(response, ProviderName, cancellationToken)
                .ConfigureAwait(false);
            return new ProviderStatus(
                true,
                $"Połączono z DeepL. Zużycie: {usage?.CharacterCount:N0} / {usage?.CharacterLimit:N0} znaków.",
                usage?.CharacterCount,
                usage?.CharacterLimit);
        }).ConfigureAwait(false);
    }

    private sealed record DeepLTranslateRequest(
        [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
        [property: JsonPropertyName("source_lang")] string SourceLang,
        [property: JsonPropertyName("target_lang")] string TargetLang,
        [property: JsonPropertyName("context")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Context = null);

    private sealed record DeepLTranslateResponse(
        [property: JsonPropertyName("translations")] List<DeepLTranslationItem>? Translations);

    private sealed record DeepLTranslationItem(
        [property: JsonPropertyName("detected_source_language")] string? DetectedSourceLanguage,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record DeepLUsageResponse(
        [property: JsonPropertyName("character_count")] long CharacterCount,
        [property: JsonPropertyName("character_limit")] long CharacterLimit);
}
