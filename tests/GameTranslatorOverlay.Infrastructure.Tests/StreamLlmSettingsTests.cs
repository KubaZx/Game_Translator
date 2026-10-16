using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class StreamLlmSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Domyslnie_strumieniowanie_jest_wlaczone()
    {
        Assert.True(new AppSettings().StreamLlmResponses);
    }

    [Fact]
    public void Stary_plik_ustawien_bez_pola_wlacza_strumieniowanie()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """{"provider": "Claude", "privateMode": true}""");

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("Claude", loaded.Provider);
        Assert.True(loaded.StreamLlmResponses);
    }

    [Fact]
    public void Wylaczenie_przechodzi_zapis_i_odczyt()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);
        store.Save(new AppSettings { StreamLlmResponses = false });

        Assert.False(store.Load().StreamLlmResponses);
        Assert.Contains("\"streamLlmResponses\": false", File.ReadAllText(paths.SettingsPath));
    }

    [Fact]
    public void Przelacznik_jest_tylko_przy_dostawcach_modeli_jezykowych()
    {
        var withStreaming = TranslationProviderCatalog.All
            .Where(static p => p.ExtraSettings.Contains(ProviderSetting.StreamResponses))
            .Select(static p => p.Id);

        Assert.Equal([TranslationProviderCatalog.Llm.Id, TranslationProviderCatalog.Claude.Id], withStreaming);
    }
}
