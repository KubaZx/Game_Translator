using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.ProviderEval;

/// <summary>
/// Buduje dostawców z kluczami WYŁĄCZNIE ze zmiennych środowiskowych — narzędzie nie czyta
/// ustawień ani sekretów DPAPI aplikacji. Klucz nigdy nie jest wypisywany; komunikat
/// o pominięciu podaje tylko nazwę brakującej zmiennej.
/// </summary>
internal static class ProviderFactory
{
    public const string DeepLKey = "GTO_DEEPL_KEY";
    public const string AzureKey = "GTO_AZURE_KEY";
    public const string AzureRegion = "GTO_AZURE_REGION";
    public const string GoogleKey = "GTO_GOOGLE_KEY";
    public const string AnthropicKey = "ANTHROPIC_API_KEY";
    public const string ClaudeModel = "GTO_CLAUDE_MODEL";
    public const string LlmEndpointVariable = "GTO_LLM_ENDPOINT";
    public const string LlmModel = "GTO_LLM_MODEL";
    public const string LlmKey = "GTO_LLM_KEY";

    public static (ITranslationProvider? Provider, string? SkipReason) Create(
        string id, HttpClient httpClient, Func<HttpClient> claudeHttpClient, bool deepLGlossary)
    {
        switch (id)
        {
            case "mock":
                return (new MockTranslationProvider(), null);

            case "deepl":
                if (Env(DeepLKey) is not { } deepLKey) return Missing(DeepLKey);
                // Glosariusz DeepL powstaje na koncie DeepL (jak w aplikacji); --no-deepl-glossary
                // pozwala porównać bez niego i niczego nie zostawiać na koncie.
                return (new DeepLTranslationProvider(httpClient, () => deepLKey, new DeepLOptions { UseGlossary = deepLGlossary }), null);

            case "azure":
                if (Env(AzureKey) is not { } azureKey) return Missing(AzureKey);
                // Region jest opcjonalny: zasób globalny Azure działa bez niego.
                var region = Env(AzureRegion);
                return (new AzureTranslatorProvider(httpClient, () => azureKey, () => region), null);

            case "google":
                if (Env(GoogleKey) is not { } googleKey) return Missing(GoogleKey);
                return (new GoogleTranslateProvider(httpClient, () => googleKey), null);

            case "claude":
                if (Env(AnthropicKey) is not { } anthropicKey) return Missing(AnthropicKey);
                var model = Env(ClaudeModel) ?? ClaudeTranslationProvider.DefaultModel;
                // Osobny HttpClient jak w aplikacji — SDK Anthropic konfiguruje klienta po swojemu.
                return (new ClaudeTranslationProvider(claudeHttpClient(), () => anthropicKey, () => model), null);

            case "llm":
                var endpoint = Env(LlmEndpointVariable);
                var llmModel = Env(LlmModel);
                if (endpoint is null || llmModel is null)
                    return (null, $"brak zmiennych {LlmEndpointVariable} i/lub {LlmModel}.");
                if (!LlmEndpoint.TryNormalize(endpoint, out var baseUri, out var error))
                    return (null, $"{LlmEndpointVariable}: {error}");
                // Klucz jest opcjonalny (lokalna Ollama/LM Studio) i należy do podanego serwera.
                var llmKey = Env(LlmKey);
                var keyHost = baseUri!.Authority;
                return (new OpenAiCompatibleTranslationProvider(
                    httpClient, () => llmKey, () => keyHost, () => endpoint, () => llmModel), null);

            default:
                return (null, "nieznany dostawca.");
        }
    }

    private static (ITranslationProvider?, string?) Missing(string variable) => (null, $"brak zmiennej środowiskowej {variable}.");

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
