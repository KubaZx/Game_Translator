using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class CacheExportContextTests
{
    [Fact]
    public async Task Eksport_i_import_w_pamieci_zachowuje_znacznik_kontekstu()
    {
        var source = new InMemoryTranslationCache();
        await source.StoreAsync(new NewCacheEntry("Talk to the\nsmith", "Talk to the\nsmith", "en", "pl",
            "Porozmawiaj\nz kowalem", "DeepL", Context: TextReflow.FormatVersion));
        await source.StoreAsync(new NewCacheEntry("Old", "Old", "en", "pl", "Stary", "DeepL"));

        var json = await source.ExportJsonAsync();
        var target = new InMemoryTranslationCache();
        Assert.Equal(2, await target.ImportJsonAsync(json));

        Assert.Equal(TextReflow.FormatVersion, (await target.LookupAsync("Talk to the\nsmith", "en", "pl", ""))!.Context);
        Assert.Null((await target.LookupAsync("Old", "en", "pl", ""))!.Context);
        // Wpis bez znacznika nie zapisuje pola wcale — plik zostaje czytelny dla starszych wersji.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"context\""));
    }

    [Fact]
    public async Task Stary_plik_eksportu_bez_kontekstu_nadal_sie_importuje()
    {
        const string oldJson = """
            [
              {
                "sourceText": "Hello",
                "normalizedText": "Hello",
                "sourceLanguage": "en",
                "targetLanguage": "pl",
                "translatedText": "Cześć",
                "provider": "DeepL",
                "gameProfile": "witcher",
                "isManual": false,
                "isApproved": false
              }
            ]
            """;
        var cache = new InMemoryTranslationCache();

        Assert.Equal(1, await cache.ImportJsonAsync(oldJson));
        var hit = await cache.LookupAsync("Hello", "en", "pl", "witcher");
        Assert.Equal("Cześć", hit!.TranslatedText);
        Assert.Null(hit.Context);
    }
}
