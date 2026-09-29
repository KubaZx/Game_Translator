using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class TranslationProviderCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("deepl", "DeepL")]
    [InlineData("Azure", "Azure")]
    [InlineData(" google ", "Google")]
    [InlineData("LLM", "LLM")]
    [InlineData("claude", "Claude")]
    [InlineData("Mock", "Mock")]
    [InlineData("nieznany", "DeepL")]
    [InlineData(null, "DeepL")]
    public void Resolve_znajduje_dostawce_albo_wraca_do_DeepL(string? id, string expected)
    {
        Assert.Equal(expected, TranslationProviderCatalog.Resolve(id).Id);
    }

    [Fact]
    public void Identyfikatory_i_sekrety_sa_unikalne_a_identyfikator_to_nazwa_dostawcy()
    {
        var all = TranslationProviderCatalog.All;

        Assert.Equal(all.Count, all.Select(static p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var secrets = all.Where(static p => p.SecretName is not null).Select(static p => p.SecretName).ToList();
        Assert.Equal(secrets.Count, secrets.Distinct().Count());
        Assert.Equal("deepl-api-key", TranslationProviderCatalog.DeepL.SecretName);
        Assert.Equal(MockTranslationProvider.ProviderName, TranslationProviderCatalog.Mock.Id);
        Assert.False(TranslationProviderCatalog.Mock.UsesApiKey);
        Assert.Contains(ClaudeTranslationProvider.DefaultModel, TranslationProviderCatalog.SuggestedClaudeModels);
    }

    [Fact]
    public void Gotowe_adresy_LLM_przechodza_walidacje()
    {
        Assert.All(TranslationProviderCatalog.LlmPresets, static preset =>
            Assert.True(LlmEndpoint.TryNormalize(preset.Endpoint, out _, out _), preset.Name));
    }

    [Fact]
    public void Ustawienia_dostawcow_przetrwaja_zapis_i_odczyt()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);
        store.Save(new AppSettings
        {
            Provider = "LLM",
            AzureRegion = "westeurope",
            LlmEndpoint = "http://localhost:11434/v1",
            LlmModel = "qwen2.5:7b",
            LlmKeyHost = "localhost:11434",
            ClaudeModel = "claude-sonnet-5-5",
        });

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("LLM", loaded.Provider);
        Assert.Equal("westeurope", loaded.AzureRegion);
        Assert.Equal("http://localhost:11434/v1", loaded.LlmEndpoint);
        Assert.Equal("qwen2.5:7b", loaded.LlmModel);
        Assert.Equal("localhost:11434", loaded.LlmKeyHost);
        Assert.Equal("claude-sonnet-5-5", loaded.ClaudeModel);
    }

    [Fact]
    public void Stare_ustawienia_bez_nowych_pol_dostaja_bezpieczne_domyslne()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """{ "provider": "DeepL", "sourceLanguage": "en" }""");

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal(LlmEndpoint.OpenAiDefault, loaded.LlmEndpoint);
        Assert.Equal(ClaudeTranslationProvider.DefaultModel, loaded.ClaudeModel);
        Assert.Null(loaded.AzureRegion);
    }
}
