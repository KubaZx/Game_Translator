using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Providers;

/// <summary>
/// Claude (Anthropic) przez oficjalne SDK. Odpowiedź wymuszona schematem JSON (structured
/// outputs), niski poziom effort dla krótkich tekstów gry, a dla modeli z obsługą — serwerowy
/// fallback na inny model, gdy filtr bezpieczeństwa odrzuci zapytanie. Klucz nigdy nie jest logowany.
/// </summary>
public sealed class ClaudeTranslationProvider(
    HttpClient httpClient,
    Func<string?> apiKeyAccessor,
    Func<string?> modelAccessor,
    LlmProviderOptions? options = null,
    ILogger<ClaudeTranslationProvider>? logger = null)
    : LlmTranslationProviderBase(options, logger ?? NullLogger<ClaudeTranslationProvider>.Instance)
{
    public const string ProviderName = "Claude";
    public const string DefaultModel = "claude-opus-5-5";

    private const string DefaultFallbacksBeta = "server-side-fallback-2026-07-01";

    // Modele, dla których serwerowy fallback „default” jest zalecany i dostępny w Claude API.
    private static readonly string[] DefaultFallbackModels =
        ["claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];

    // Rodziny przyjmujące output_config.effort (np. Haiku 4.5 i Sonnet 4.5 zwracają na nim 400).
    private static readonly string[] EffortModelPrefixes =
    [
        "claude-fable-", "claude-mythos-", "claude-opus-5", "claude-opus-4-5", "claude-opus-4-6",
        "claude-opus-4-7", "claude-opus-4-8", "claude-sonnet-5", "claude-sonnet-4-6",
    ];

    private static readonly IReadOnlyDictionary<string, JsonElement> TranslationsSchema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            translations = new { type = "array", items = new { type = "string" } },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "translations" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public override string Name => ProviderName;
    public override bool RequiresApiKey => true;

    public static bool UsesDefaultFallbacks(string model) =>
        DefaultFallbackModels.Contains(model, StringComparer.OrdinalIgnoreCase);

    public static bool SupportsEffort(string model) =>
        EffortModelPrefixes.Any(prefix => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private string ResolveModel()
    {
        var model = modelAccessor()?.Trim();
        return string.IsNullOrEmpty(model) ? DefaultModel : model;
    }

    private string ResolveApiKey()
    {
        var apiKey = apiKeyAccessor()?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new TranslationException(TranslationFailureKind.MissingApiKey, "Nie skonfigurowano klucza API Anthropic (Claude).");
        }
        return apiKey;
    }

    protected override void EnsureConfigured() => _ = ResolveApiKey();

    private sealed record ClientForKey(string ApiKey, AnthropicClient Client);

    private ClientForKey? _client;

    /// <summary>Jeden klient SDK na klucz — zmiana klucza w ustawieniach tworzy nowego.</summary>
    private AnthropicClient CreateClient(string apiKey)
    {
        var current = Volatile.Read(ref _client);
        if (current is not null && current.ApiKey == apiKey) return current.Client;

        var created = new ClientForKey(apiKey, new AnthropicClient
        {
            HttpClient = httpClient,
            ApiKey = apiKey,
            MaxRetries = Options.MaxRetries,
            Timeout = Options.RequestTimeout,
        });
        Volatile.Write(ref _client, created);
        return created.Client;
    }

    protected override async Task<string?> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken)
    {
        var model = ResolveModel();
        var client = CreateClient(ResolveApiKey());

        var outputConfig = SupportsEffort(model)
            ? new BetaOutputConfig { Format = new BetaJsonOutputFormat { Schema = TranslationsSchema }, Effort = Effort.Low }
            : new BetaOutputConfig { Format = new BetaJsonOutputFormat { Schema = TranslationsSchema } };

        var parameters = UsesDefaultFallbacks(model)
            ? new MessageCreateParams
            {
                Model = model,
                MaxTokens = Options.MaxOutputTokens,
                System = systemPrompt,
                Messages = [new() { Role = Role.User, Content = userMessage }],
                OutputConfig = outputConfig,
                // Odmowa filtra bezpieczeństwa jest ponawiana po stronie serwera na modelu
                // wybranym przez Anthropic dla danej kategorii — gracz nie traci tłumaczenia.
                Fallbacks = new Default(),
                Betas = [DefaultFallbacksBeta],
            }
            : new MessageCreateParams
            {
                Model = model,
                MaxTokens = Options.MaxOutputTokens,
                System = systemPrompt,
                Messages = [new() { Role = Role.User, Content = userMessage }],
                OutputConfig = outputConfig,
            };

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (MapException(ex, cancellationToken) is { } mapped)
        {
            throw mapped;
        }

        if (response.StopReason == "refusal")
        {
            throw new TranslationException(TranslationFailureKind.ContentRefused,
                "Claude odmówił przetłumaczenia tekstu (filtr bezpieczeństwa).");
        }

        // Po fallbacku odpowiedź modelu zastępczego jest ostatnim blokiem tekstu.
        string? text = null;
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                text = textBlock.Text;
            }
        }
        return text;
    }

    /// <summary>
    /// Mapuje wyjątki SDK na <see cref="TranslationException"/>. Anulowanie przez użytkownika
    /// zwraca null (wyjątek leci dalej bez zmian). Treść błędów API nie jest dołączana —
    /// log dostaje tylko rodzaj i status.
    /// </summary>
    private static TranslationException? MapException(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested && ex is OperationCanceledException) return null;

        return ex switch
        {
            AnthropicUnauthorizedException => new(TranslationFailureKind.InvalidApiKey, "Anthropic odrzucił klucz API (HTTP 401)."),
            AnthropicForbiddenException => new(TranslationFailureKind.InvalidApiKey,
                "Anthropic odmówił dostępu (HTTP 403) — sprawdź uprawnienia klucza i organizacji."),
            AnthropicNotFoundException => new(TranslationFailureKind.ModelNotFound,
                "Anthropic nie zna wskazanego modelu albo nie jest on dostępny dla tego klucza (HTTP 404)."),
            AnthropicRateLimitException => new(TranslationFailureKind.RateLimited, "Anthropic ogranicza liczbę zapytań (HTTP 429)."),
            Anthropic5xxException => new(TranslationFailureKind.ServiceUnavailable, "Claude jest chwilowo niedostępny lub przeciążony."),
            AnthropicBadRequestException bad when bad.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) =>
                new(TranslationFailureKind.QuotaExceeded, "Na koncie Anthropic zabrakło środków."),
            AnthropicBadRequestException => new(TranslationFailureKind.InvalidRequest, "Anthropic odrzucił żądanie (HTTP 400)."),
            AnthropicApiException { StatusCode: var status } when (int)status == 402 =>
                new(TranslationFailureKind.QuotaExceeded, "Na koncie Anthropic zabrakło środków (HTTP 402)."),
            AnthropicApiException { StatusCode: var status } when (int)status == 413 =>
                new(TranslationFailureKind.TextTooLong, "Tekst jest zbyt długi dla Claude."),
            AnthropicApiException { StatusCode: var status } =>
                new(TranslationFailureKind.Unknown, $"Anthropic zwrócił nieoczekiwany status HTTP {(int)status}."),
            AnthropicIOException { InnerException.InnerException: OperationCanceledException or TimeoutException }
                or OperationCanceledException or TimeoutException =>
                new(TranslationFailureKind.Timeout, "Claude nie odpowiedział w wyznaczonym czasie."),
            AnthropicIOException or HttpRequestException =>
                new(TranslationFailureKind.NetworkError, "Nie udało się połączyć z Anthropic. Sprawdź połączenie z internetem.", ex),
            AnthropicInvalidDataException => new(TranslationFailureKind.NetworkError,
                "Anthropic zwrócił odpowiedź w nieoczekiwanym formacie (przechwycenie przez portal/proxy sieci?)."),
            FormatException => new(TranslationFailureKind.InvalidConfiguration,
                "Klucz API Anthropic zawiera niedozwolone znaki."),
            _ => null,
        };
    }

    public override async Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = apiKeyAccessor()?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            return new ProviderStatus(false, "Brak klucza API Anthropic. Wpisz klucz i zapisz go, zanim przetestujesz połączenie.");
        }

        var model = ResolveModel();
        return await ProviderHttp.TestAsync(async () =>
        {
            try
            {
                // Odczyt opisu modelu nic nie kosztuje, a sprawdza jednocześnie klucz i nazwę modelu.
                var info = await CreateClient(apiKey).Models.Retrieve(model, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return new ProviderStatus(true, $"Połączono z Anthropic. Model {info.DisplayName} ({info.ID}) jest dostępny.");
            }
            catch (Exception ex) when (MapException(ex, cancellationToken) is { } mapped)
            {
                throw mapped;
            }
        }).ConfigureAwait(false);
    }
}
