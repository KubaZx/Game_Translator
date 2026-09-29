using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Infrastructure.Providers;

/// <summary>Dodatkowe pole ustawień pokazywane w oknie aplikacji dla danego dostawcy.</summary>
public enum ProviderSetting
{
    AzureRegion,
    LlmEndpoint,
    LlmModel,
    ClaudeModel,
}

/// <summary>
/// Opis dostawcy dla interfejsu: nazwa, sekret w DPAPI, podpowiedzi i dodatkowe pola.
/// <see cref="Id"/> jest równe <see cref="ITranslationProvider.Name"/> i zapisywane w ustawieniach.
/// </summary>
public sealed record TranslationProviderInfo(
    string Id,
    string DisplayName,
    string? SecretName,
    bool KeyOptional,
    string KeyHint,
    IReadOnlyList<ProviderSetting> ExtraSettings)
{
    public bool UsesApiKey => SecretName is not null;
}

public static class TranslationProviderCatalog
{
    public static TranslationProviderInfo DeepL { get; } = new(
        DeepLTranslationProvider.ProviderName,
        "DeepL",
        "deepl-api-key",
        KeyOptional: false,
        "Darmowy klucz DeepL API Free kończy się na „:fx” (500 tys. znaków miesięcznie).",
        []);

    public static TranslationProviderInfo Azure { get; } = new(
        AzureTranslatorProvider.ProviderName,
        "Azure AI Translator",
        "azure-translator-key",
        KeyOptional: false,
        "Klucz zasobu Translator z portalu Azure. Plan F0 daje 2 mln znaków miesięcznie. " +
        "Region podaj dla zasobu regionalnego (np. westeurope); zasób globalny działa bez regionu.",
        [ProviderSetting.AzureRegion]);

    public static TranslationProviderInfo Google { get; } = new(
        GoogleTranslateProvider.ProviderName,
        "Google Cloud Translation",
        "google-translate-key",
        KeyOptional: false,
        "Klucz API projektu Google Cloud z włączonym Cloud Translation API (500 tys. znaków miesięcznie bez opłat).",
        []);

    public static TranslationProviderInfo Llm { get; } = new(
        OpenAiCompatibleTranslationProvider.ProviderName,
        "Model językowy (OpenAI / Ollama / LM Studio…)",
        "llm-api-key",
        KeyOptional: true,
        "Dowolny serwer zgodny z API OpenAI. Lokalna Ollama albo LM Studio działają bez klucza " +
        "i bez wysyłania tekstu poza komputer. Terminy ze słownika i nazwa gry trafiają do promptu.",
        [ProviderSetting.LlmEndpoint, ProviderSetting.LlmModel]);

    public static TranslationProviderInfo Claude { get; } = new(
        ClaudeTranslationProvider.ProviderName,
        "Claude (Anthropic)",
        "anthropic-api-key",
        KeyOptional: false,
        "Klucz z console.anthropic.com. Claude tłumaczy z kontekstem całego ekranu, nazwą gry " +
        "i terminami ze słownika; odmowa filtra bezpieczeństwa jest ponawiana na modelu zastępczym.",
        [ProviderSetting.ClaudeModel]);

    public static TranslationProviderInfo Mock { get; } = new(
        MockTranslationProvider.ProviderName,
        "Mock (test bez internetu)",
        SecretName: null,
        KeyOptional: true,
        "Dostawca testowy: dodaje „[PL]” przed oryginałem, niczego nie wysyła i nie wymaga klucza.",
        []);

    public static IReadOnlyList<TranslationProviderInfo> All { get; } = [DeepL, Azure, Google, Llm, Claude, Mock];

    /// <summary>Znany dostawca o podanym identyfikatorze; nieznany lub pusty → DeepL (domyślny).</summary>
    public static TranslationProviderInfo Resolve(string? id) =>
        All.FirstOrDefault(p => p.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DeepL;

    /// <summary>Sugerowane modele Claude; pole pozostaje edytowalne.</summary>
    public static IReadOnlyList<string> SuggestedClaudeModels { get; } =
        [ClaudeTranslationProvider.DefaultModel, "claude-sonnet-5-5", "claude-haiku-4-5"];

    /// <summary>Gotowe adresy popularnych serwerów zgodnych z OpenAI.</summary>
    public static IReadOnlyList<(string Name, string Endpoint)> LlmPresets { get; } =
    [
        ("OpenAI", LlmEndpoint.OpenAiDefault),
        ("Ollama", "http://localhost:11434/v1"),
        ("LM Studio", "http://localhost:1234/v1"),
    ];
}
