using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Providers;

/// <summary>
/// Model językowy przez API zgodne z OpenAI (/chat/completions): OpenAI, OpenRouter, Groq,
/// a także lokalne serwery (Ollama, LM Studio) — wtedy tekst nie opuszcza komputera.
/// Klucz jest opcjonalny (serwery lokalne go nie wymagają), nigdy nie jest logowany
/// i trafia wyłącznie do serwera, dla którego go zapisano (<paramref name="keyHostAccessor"/>).
/// </summary>
public sealed class OpenAiCompatibleTranslationProvider(
    HttpClient httpClient,
    Func<string?> apiKeyAccessor,
    Func<string?> keyHostAccessor,
    Func<string?> endpointAccessor,
    Func<string?> modelAccessor,
    LlmProviderOptions? options = null,
    ILogger<OpenAiCompatibleTranslationProvider>? logger = null)
    : LlmTranslationProviderBase(options, logger ?? NullLogger<OpenAiCompatibleTranslationProvider>.Instance),
      IWarmableTranslationProvider
{
    public const string ProviderName = "LLM";

    /// <summary>Czy klucz zapisany dla <paramref name="keyHost"/> może być wysłany na <paramref name="endpoint"/>.</summary>
    public static bool KeyBelongsTo(string? keyHost, Uri endpoint) =>
        !string.IsNullOrWhiteSpace(keyHost)
        && keyHost.Trim().Equals(endpoint.Authority, StringComparison.OrdinalIgnoreCase);

    public override string Name => ProviderName;
    public override bool RequiresApiKey => false;

    protected override void EnsureConfigured()
    {
        _ = ResolveEndpoint();
        _ = ResolveModel();
    }

    private Uri ResolveEndpoint()
    {
        if (!LlmEndpoint.TryNormalize(endpointAccessor(), out var baseUri, out var error))
        {
            throw new TranslationException(TranslationFailureKind.InvalidConfiguration, error);
        }
        return baseUri!;
    }

    private string ResolveModel()
    {
        var model = modelAccessor()?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            throw new TranslationException(TranslationFailureKind.InvalidConfiguration,
                "Podaj nazwę modelu LLM w ustawieniach (np. gpt-4o-mini albo nazwę modelu z Ollamy).");
        }
        return model;
    }

    protected override async Task<string?> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint();
        var model = ResolveModel();
        var storedKey = apiKeyAccessor()?.Trim();
        // Klucz zapisany dla innego serwera nie wychodzi pod nowy adres (np. klucz OpenAI
        // po zmianie adresu na inny serwis albo na lokalny proces nasłuchujący na porcie).
        var keyWithheld = !string.IsNullOrEmpty(storedKey) && !KeyBelongsTo(keyHostAccessor(), baseUri);
        var apiKey = keyWithheld ? null : storedKey;
        var request = new ChatRequest(model, [new ChatMessage("system", systemPrompt), new ChatMessage("user", userMessage)]);

        HttpResponseMessage response;
        try
        {
            response = await ProviderHttp.SendAsync(
                httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"))
                    {
                        Content = JsonContent.Create(request),
                    };
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    return message;
                },
                ProviderName,
                Options,
                MapError,
                Logger,
                cancellationToken,
                isRetryable: static failure =>
                    (failure.StatusCode == 429 && !IsQuotaExhausted(failure)) || failure.StatusCode >= 500).ConfigureAwait(false);
        }
        catch (TranslationException ex) when (ex.Kind == TranslationFailureKind.NetworkError && LlmEndpoint.IsLoopback(baseUri))
        {
            throw new TranslationException(TranslationFailureKind.NetworkError,
                $"Serwer LLM na tym komputerze ({baseUri.Authority}) nie odpowiada. Uruchom Ollamę albo LM Studio i spróbuj ponownie.",
                ex.InnerException);
        }
        catch (TranslationException ex) when (ex.Kind == TranslationFailureKind.InvalidApiKey && keyWithheld)
        {
            var owner = keyHostAccessor()?.Trim() is { Length: > 0 } host ? $" ({host})" : string.Empty;
            throw new TranslationException(TranslationFailureKind.InvalidConfiguration,
                $"Serwer {baseUri.Authority} wymaga klucza, a zapisany klucz należy do innego serwera{owner}. " +
                "Zapisz klucz ponownie dla tego adresu.");
        }

        using (response)
        {
            var payload = await ProviderHttp.ReadJsonAsync<ChatResponse>(response, ProviderName, cancellationToken)
                .ConfigureAwait(false);
            var choice = payload?.Choices is { Count: > 0 } choices ? choices[0] : null;
            if (!string.IsNullOrWhiteSpace(choice?.Message?.Refusal) || choice?.FinishReason == "content_filter")
            {
                throw new TranslationException(TranslationFailureKind.ContentRefused,
                    "Model odmówił przetłumaczenia tekstu (filtr treści dostawcy).");
            }
            return choice?.Message?.Content;
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        // Serwer lokalny nie wymaga DNS ani TLS — nie ma czego rozgrzewać.
        if (!LlmEndpoint.TryNormalize(endpointAccessor(), out var baseUri, out _) || LlmEndpoint.IsLoopback(baseUri!))
        {
            return Task.CompletedTask;
        }
        return ProviderHttp.WarmUpAsync(httpClient, new Uri(baseUri!.GetLeftPart(UriPartial.Authority) + "/"), cancellationToken);
    }

    private static bool IsQuotaExhausted(ProviderHttpFailure failure) =>
        failure.BodyContains("insufficient_quota") || failure.BodyContains("quota exceeded");

    private static bool MentionsMissingModel(ProviderHttpFailure failure) =>
        failure.BodyContains("model") && (failure.BodyContains("not found") || failure.BodyContains("does not exist")
            || failure.BodyContains("not exist") || failure.BodyContains("try pulling"));

    private static TranslationException MapError(ProviderHttpFailure failure) => failure.StatusCode switch
    {
        401 or 403 => new TranslationException(TranslationFailureKind.InvalidApiKey,
            $"Serwer LLM odrzucił klucz API (HTTP {failure.StatusCode})."),
        402 => new TranslationException(TranslationFailureKind.QuotaExceeded,
            "Na koncie dostawcy LLM zabrakło środków (HTTP 402)."),
        429 when IsQuotaExhausted(failure) => new TranslationException(TranslationFailureKind.QuotaExceeded,
            "Limit lub środki na koncie dostawcy LLM zostały wyczerpane (HTTP 429)."),
        429 => new TranslationException(TranslationFailureKind.RateLimited, "Serwer LLM ogranicza liczbę zapytań (HTTP 429)."),
        404 or 400 when MentionsMissingModel(failure) => new TranslationException(TranslationFailureKind.ModelNotFound,
            "Serwer LLM nie zna wskazanego modelu — sprawdź nazwę (w Ollamie: ollama pull <model>)."),
        404 => new TranslationException(TranslationFailureKind.InvalidConfiguration,
            "Serwer LLM nie obsługuje tego adresu (HTTP 404). Sprawdź, czy adres kończy się na /v1."),
        400 => new TranslationException(TranslationFailureKind.InvalidRequest, "Serwer LLM odrzucił żądanie (HTTP 400)."),
        413 => new TranslationException(TranslationFailureKind.TextTooLong, "Tekst jest zbyt długi dla serwera LLM."),
        >= 500 => new TranslationException(TranslationFailureKind.ServiceUnavailable,
            $"Serwer LLM jest chwilowo niedostępny (HTTP {failure.StatusCode})."),
        _ => new TranslationException(TranslationFailureKind.Unknown,
            $"Serwer LLM zwrócił nieoczekiwany status HTTP {failure.StatusCode}."),
    };

    public override Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var where = LlmEndpoint.TryNormalize(endpointAccessor(), out var baseUri, out _) ? baseUri!.Authority : "serwer LLM";
        var local = baseUri is not null && LlmEndpoint.IsLoopback(baseUri) ? " (lokalnie)" : string.Empty;
        return ProbeTranslationAsync($"Połączono z {where}{local}, model {modelAccessor()?.Trim()}", cancellationToken);
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream = false);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("refusal")] string? Refusal);
}
