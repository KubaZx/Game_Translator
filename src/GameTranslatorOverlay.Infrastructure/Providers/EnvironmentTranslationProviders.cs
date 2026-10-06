using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed record EnvironmentProviderOptions
{
    public bool DeepLGlossary { get; init; } = true;
    public LlmServerOptions? LlmServerOptions { get; init; }
    public bool UseLlmPreset { get; init; } = true;
    public LlmProviderOptions? LlmProviderOptions { get; init; }
    public DeepLOptions? DeepLOptions { get; init; }
}

public sealed record EnvironmentProvider(ITranslationProvider? Provider, string? SkipReason)
{
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public bool IsLocal { get; init; }
    public LlmServerOptions? ServerOptions { get; init; }
    public string? PresetName { get; init; }
}

public sealed class EnvironmentTranslationProviders : IDisposable
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

    private readonly Func<string, string?> _environment;
    private readonly Func<HttpClient> _createClient;
    private readonly List<HttpClient> _owned = [];
    private readonly Lock _gate = new();
    private HttpClient? _shared;

    public EnvironmentTranslationProviders()
        : this(Environment.GetEnvironmentVariable, ProviderHttpClientFactory.Create)
    {
    }

    public EnvironmentTranslationProviders(Func<string, string?> environment, Func<HttpClient> createClient)
    {
        _environment = environment;
        _createClient = createClient;
    }

    public EnvironmentProvider Create(string id, EnvironmentProviderOptions? options = null)
    {
        options ??= new EnvironmentProviderOptions();
        switch (id.Trim().ToLowerInvariant())
        {
            case "mock":
                return new EnvironmentProvider(new MockTranslationProvider(), null) { IsLocal = true };

            case "deepl":
                if (Env(DeepLKey) is not { } deepLKey) return Missing(DeepLKey);
                var deepLOptions = options.DeepLOptions ?? new DeepLOptions();
                deepLOptions.UseGlossary = options.DeepLGlossary;
                return new EnvironmentProvider(new DeepLTranslationProvider(SharedClient(), () => deepLKey, deepLOptions), null);

            case "azure":
                if (Env(AzureKey) is not { } azureKey) return Missing(AzureKey);
                var region = Env(AzureRegion);
                return new EnvironmentProvider(new AzureTranslatorProvider(SharedClient(), () => azureKey, () => region), null);

            case "google":
                if (Env(GoogleKey) is not { } googleKey) return Missing(GoogleKey);
                return new EnvironmentProvider(new GoogleTranslateProvider(SharedClient(), () => googleKey), null);

            case "claude":
                if (Env(AnthropicKey) is not { } anthropicKey) return Missing(AnthropicKey);
                var claudeModel = Env(ClaudeModel) ?? ClaudeTranslationProvider.DefaultModel;
                return new EnvironmentProvider(new ClaudeTranslationProvider(OwnedClient(), () => anthropicKey, () => claudeModel), null)
                {
                    Model = claudeModel,
                };

            case "llm":
                return CreateLlm(options);

            default:
                return new EnvironmentProvider(null, "nieznany dostawca.");
        }
    }

    private EnvironmentProvider CreateLlm(EnvironmentProviderOptions options)
    {
        var endpoint = Env(LlmEndpointVariable);
        var model = Env(LlmModel);
        if (endpoint is null || model is null)
            return new EnvironmentProvider(null, $"brak zmiennych {LlmEndpointVariable} i/lub {LlmModel}.");
        if (!LlmEndpoint.TryNormalize(endpoint, out var baseUri, out var error))
            return new EnvironmentProvider(null, $"{LlmEndpointVariable}: {error}");

        var key = Env(LlmKey);
        var host = baseUri!.Authority;
        var preset = TranslationProviderCatalog.FindLlmPreset(baseUri);
        LlmServerOptions? serverOptions = null;
        string? presetName = null;
        if (options.LlmServerOptions is { HasRequestFields: true } explicitOptions)
        {
            serverOptions = explicitOptions.Sanitized().ForHost(host);
        }
        else if (options.UseLlmPreset && preset?.ServerOptionsForHost() is { } presetOptions)
        {
            serverOptions = presetOptions.Sanitized();
            presetName = preset.Name;
        }

        var provider = new OpenAiCompatibleTranslationProvider(
            SharedClient(), () => key, () => host, () => endpoint, () => model, options.LlmProviderOptions,
            serverOptionsAccessor: () => serverOptions);
        return new EnvironmentProvider(provider, null)
        {
            Endpoint = host,
            Model = model,
            IsLocal = LlmEndpoint.IsLoopback(baseUri),
            ServerOptions = serverOptions,
            PresetName = presetName,
        };
    }

    private HttpClient SharedClient()
    {
        lock (_gate)
        {
            if (_shared is null)
            {
                _shared = _createClient();
                _owned.Add(_shared);
            }
            return _shared;
        }
    }

    private HttpClient OwnedClient()
    {
        lock (_gate)
        {
            var client = _createClient();
            _owned.Add(client);
            return client;
        }
    }

    private static EnvironmentProvider Missing(string variable) =>
        new(null, $"brak zmiennej środowiskowej {variable}.");

    private string? Env(string name)
    {
        var value = _environment(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _owned) client.Dispose();
            _owned.Clear();
            _shared = null;
        }
    }
}
