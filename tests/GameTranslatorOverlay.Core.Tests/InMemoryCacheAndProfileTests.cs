using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class InMemoryCacheTests
{
    [Fact]
    public async Task Zapis_i_odczyt_dziala()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Mock"));

        var hit = await cache.LookupAsync("Hello", "en", "pl", "");

        Assert.NotNull(hit);
        Assert.Equal("Cześć", hit.TranslatedText);
    }

    [Fact]
    public async Task Automatyczny_zapis_nie_nadpisuje_recznej_korekty()
    {
        var cache = new InMemoryTranslationCache();
        await cache.SaveManualCorrectionAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Poprawione", "manual"));
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Automatyczne", "Mock"));

        var hit = await cache.LookupAsync("Hello", "en", "pl", "");

        Assert.NotNull(hit);
        Assert.Equal("Poprawione", hit.TranslatedText);
        Assert.True(hit.IsManual);
    }

    [Fact]
    public async Task Reczna_korekta_globalna_wygrywa_z_automatycznym_wpisem_profilu()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Z profilu", "Mock", GameProfile: "poe2"));
        await cache.SaveManualCorrectionAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Ręczne", "manual"));

        var hit = await cache.LookupAsync("Hello", "en", "pl", "poe2");

        Assert.NotNull(hit);
        Assert.Equal("Ręczne", hit.TranslatedText);
    }

    [Fact]
    public async Task Czyszczenie_zachowuje_reczne_korekty()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("A", "A", "en", "pl", "1", "Mock"));
        await cache.SaveManualCorrectionAsync(new NewCacheEntry("B", "B", "en", "pl", "2", "manual"));

        var removed = await cache.ClearAsync(keepManualCorrections: true);

        Assert.Equal(1, removed);
        Assert.Null(await cache.LookupAsync("A", "en", "pl", ""));
        Assert.NotNull(await cache.LookupAsync("B", "en", "pl", ""));
    }

    [Fact]
    public async Task Eksport_i_import_wykonuja_roundtrip()
    {
        var source = new InMemoryTranslationCache();
        await source.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Mock"));
        var json = await source.ExportJsonAsync();

        var target = new InMemoryTranslationCache();
        var imported = await target.ImportJsonAsync(json);

        Assert.Equal(1, imported);
        Assert.NotNull(await target.LookupAsync("Hello", "en", "pl", ""));
    }
}

public class ProfileValidatorTests
{
    private static GameProfile ValidProfile() => new()
    {
        Id = "path-of-exile-2",
        Name = "Path of Exile 2",
        ProfileVersion = 1,
        SourceLanguage = "en",
        Ocr = new OcrProfileSettings { Upscale = 2.0, MinTextHeight = 10 },
        ChangeDetection = new ChangeDetectionProfileSettings { Threshold = 0.02, Fps = 4 },
    };

    [Fact]
    public void Poprawny_profil_przechodzi_walidacje()
    {
        Assert.Empty(ProfileValidator.Validate(ValidProfile()));
    }

    [Fact]
    public void Brak_id_i_nazwy_zglasza_bledy()
    {
        var profile = ValidProfile();
        profile.Id = "";
        profile.Name = "";

        var errors = ProfileValidator.Validate(profile);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Niedozwolone_znaki_w_id_zglaszaja_blad()
    {
        var profile = ValidProfile();
        profile.Id = "poe/2";

        Assert.NotEmpty(ProfileValidator.Validate(profile));
    }

    [Fact]
    public void Upscale_poza_zakresem_zglasza_blad()
    {
        var profile = ValidProfile();
        profile.Ocr = new OcrProfileSettings { Upscale = 9.0 };

        Assert.NotEmpty(ProfileValidator.Validate(profile));
    }

    [Fact]
    public void Serializer_czyta_schemat_json_profilu()
    {
        const string json = """
            {
              "id": "generic",
              "name": "Profil uniwersalny",
              "profileVersion": 1,
              "processNames": [],
              "windowTitles": [],
              "sourceLanguage": "en",
              "ocr": { "upscale": 2.0, "minTextHeight": 10 },
              "changeDetection": { "threshold": 0.02, "fps": 4 }
            }
            """;

        var profile = ProfileSerializer.FromJson(json);

        Assert.Equal("generic", profile.Id);
        Assert.NotNull(profile.Ocr);
        Assert.Equal(2.0, profile.Ocr.Upscale);
        Assert.Empty(ProfileValidator.Validate(profile));
    }
}
