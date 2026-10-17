using System.Text.Json.Nodes;
using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class ProfileTuningTests
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json, NodeOptions)!;

    private const string Minimal = """
        { "id": "latarnia", "name": "Mroczna Latarnia", "profileVersion": 1, "sourceLanguage": "en", "processNames": ["latarnia.exe"] }
        """;

    private const string Full = """
        {
          "id": "latarnia",
          "name": "Mroczna Latarnia",
          "profileVersion": 1,
          "author": "gracz",
          "description": "Stary opis",
          "processNames": ["latarnia.exe", "LatarniaLauncher.exe"],
          "windowTitles": ["Mroczna Latarnia"],
          "sourceLanguage": "en",
          "recommendedMode": "manual-region",
          "glossary": "latarnia",
          "ocr": { "upscale": 2.0, "minTextHeight": 10 },
          "changeDetection": { "threshold": 0.03, "fps": 6, "przyszlyParametr": 7 },
          "online": false,
          "overlay": {
            "fontFamily": "Georgia",
            "fontRules": [ { "fontFamily": "Cambria", "textColor": "#FBFA86", "colorTolerance": 40, "outlined": true } ]
          },
          "live": { "ignoreRegions": [ { "x": 0.425, "y": 0.0, "width": 0.15, "height": 0.08 } ], "przyszlePoleLive": true },
          "corpus": {
            "format": "unity-textasset",
            "container": "Latarnia_Data/data.unity3d",
            "file": "resources.assets",
            "sources": [ { "id": "napisy", "parser": "srt", "kind": "subtitle", "include": ["*"] } ]
          },
          "minAppVersion": "0.1.0",
          "przyszlePole": { "a": [1, 2, 3] }
        }
        """;

    private static ProfileTuning Changed(ProfileTuning before) => before with
    {
        Name = "Mroczna Latarnia — strojenie",
        ProcessNames = ["latarnia.exe", "Latarnia-Win64-Shipping.exe"],
        WindowTitles = ["Mroczna Latarnia", "Mroczna Latarnia — menu"],
        SourceLanguage = "en",
        Glossary = "latarnia-2",
        OcrUpscale = 3.0,
        OcrMinTextHeight = 14,
        Fps = 12,
        ChangeThreshold = 0.05,
        FontFamily = "Cambria",
        RecommendedMode = ProfileTuningRules.LiveMode,
        Description = "Nowy opis",
        IgnoreRegions = [new RelativeRegion { X = 0.8, Y = 0.9, Width = 0.2, Height = 0.1 }],
    };

    [Fact]
    public void Wszystkie_pola_edytora_przechodza_przez_zapis_i_odczyt()
    {
        var root = Parse(Minimal);
        var before = ProfileTuningJson.Read(root);
        var after = Changed(before);

        ProfileTuningJson.Apply(root, before, after);
        var reread = ProfileTuningJson.Read(Parse(root.ToJsonString()));

        Assert.Equal(after, reread);
        var profile = ProfileSerializer.FromJson(root.ToJsonString());
        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal("Mroczna Latarnia — strojenie", profile.Name);
        Assert.Equal(["latarnia.exe", "Latarnia-Win64-Shipping.exe"], profile.ProcessNames);
        Assert.Equal(["Mroczna Latarnia", "Mroczna Latarnia — menu"], profile.WindowTitles);
        Assert.Equal("latarnia-2", profile.Glossary);
        Assert.Equal(3.0, profile.Ocr?.Upscale);
        Assert.Equal(14, profile.Ocr?.MinTextHeight);
        Assert.Equal(12, profile.ChangeDetection?.Fps);
        Assert.Equal(0.05, profile.ChangeDetection?.Threshold);
        Assert.Equal("Cambria", profile.Overlay?.FontFamily);
        Assert.Equal("live", profile.RecommendedMode);
        Assert.Equal("Nowy opis", profile.Description);
        var region = Assert.Single(profile.Live!.IgnoreRegions!);
        Assert.Equal((0.8, 0.9, 0.2, 0.1), (region.X, region.Y, region.Width, region.Height));
    }

    [Fact]
    public void Odczyt_profilu_bez_ustawien_daje_puste_pola_czyli_domyslne_aplikacji()
    {
        var tuning = ProfileTuningJson.Read(Parse(Minimal));

        Assert.Equal("Mroczna Latarnia", tuning.Name);
        Assert.Equal(["latarnia.exe"], tuning.ProcessNames);
        Assert.Empty(tuning.WindowTitles);
        Assert.Null(tuning.Glossary);
        Assert.Null(tuning.OcrUpscale);
        Assert.Null(tuning.OcrMinTextHeight);
        Assert.Null(tuning.Fps);
        Assert.Null(tuning.ChangeThreshold);
        Assert.Null(tuning.FontFamily);
        Assert.Null(tuning.RecommendedMode);
        Assert.Null(tuning.Description);
        Assert.Empty(tuning.IgnoreRegions);
    }

    [Fact]
    public void Wyczyszczenie_pol_usuwa_je_z_pliku_i_wraca_do_domyslnych_aplikacji()
    {
        var root = Parse(Minimal);
        var start = ProfileTuningJson.Read(root);
        var tuned = Changed(start);
        ProfileTuningJson.Apply(root, start, tuned);

        var cleared = tuned with
        {
            WindowTitles = [],
            Glossary = null,
            OcrUpscale = null,
            OcrMinTextHeight = null,
            Fps = null,
            ChangeThreshold = null,
            FontFamily = null,
            RecommendedMode = null,
            Description = null,
            IgnoreRegions = [],
        };
        ProfileTuningJson.Apply(root, tuned, cleared);

        var names = root.Select(static p => p.Key).ToList();
        Assert.Equal(["id", "name", "profileVersion", "sourceLanguage", "processNames", "windowTitles"], names);
        var profile = ProfileSerializer.FromJson(root.ToJsonString());
        Assert.Null(profile.Ocr);
        Assert.Null(profile.ChangeDetection);
        Assert.Null(profile.Overlay);
        Assert.Null(profile.Live);
        Assert.Null(profile.Glossary);
        Assert.Empty(profile.WindowTitles);
    }

    [Fact]
    public void Niezmienione_pola_nie_sa_dopisywane_do_pliku()
    {
        var root = Parse(Minimal);
        var before = ProfileTuningJson.Read(root);

        ProfileTuningJson.Apply(root, before, before with { FontFamily = "Georgia" });

        Assert.Equal(["id", "name", "profileVersion", "sourceLanguage", "processNames", "overlay"], root.Select(static p => p.Key));
        Assert.Equal("""{"fontFamily":"Georgia"}""", root["overlay"]!.ToJsonString());
    }

    [Fact]
    public void Pola_spoza_edytora_przetrwaja_zmiane_wszystkich_pol_edytora()
    {
        var original = Parse(Full);
        var root = Parse(Full);
        var before = ProfileTuningJson.Read(root);

        ProfileTuningJson.Apply(root, before, Changed(before));

        foreach (var key in new[] { "id", "profileVersion", "author", "online", "corpus", "minAppVersion", "przyszlePole" })
        {
            Assert.True(JsonNode.DeepEquals(original[key], root[key]), key);
        }
        Assert.True(JsonNode.DeepEquals(original["overlay"]!["fontRules"], root["overlay"]!["fontRules"]));
        Assert.True(root["live"]!["przyszlePoleLive"]!.GetValue<bool>());
        Assert.Equal(7, root["changeDetection"]!["przyszlyParametr"]!.GetValue<int>());
        var profile = ProfileSerializer.FromJson(root.ToJsonString());
        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.NotNull(profile.Corpus);
        Assert.Single(profile.Overlay!.FontRules!);
    }

    [Fact]
    public void Wielkosc_liter_kluczy_w_pliku_zostaje_zachowana()
    {
        var root = Parse("""{ "id": "a", "Name": "Gra", "sourceLanguage": "en", "OCR": { "Upscale": 2.0 } }""");
        var before = ProfileTuningJson.Read(root);

        ProfileTuningJson.Apply(root, before, before with { Name = "Gra 2", OcrUpscale = 3.0 });

        var keys = root.Select(static p => p.Key).ToList();
        Assert.Equal(["id", "Name", "sourceLanguage", "OCR"], keys);
        Assert.Equal(3.0, root["OCR"]!["Upscale"]!.GetValue<double>());
        Assert.Equal("Gra 2", ProfileSerializer.FromJson(root.ToJsonString()).Name);
    }

    [Fact]
    public void Jedno_pole_wykrywania_zmian_dostaje_drugie_jako_domyslne_aplikacji()
    {
        var root = Parse(Minimal);
        var before = ProfileTuningJson.Read(root);

        ProfileTuningJson.Apply(root, before, before with { ChangeThreshold = 0.04 });

        var profile = ProfileSerializer.FromJson(root.ToJsonString());
        Assert.Equal(ChangeDetectionProfileSettings.AppDefaultFps, profile.ChangeDetection?.Fps);
        Assert.Equal(0.04, profile.ChangeDetection?.Threshold);
    }

    [Fact]
    public void Wyczyszczenie_wykrywania_zmian_z_obcym_polem_zostawia_je_i_domyslne_aplikacji()
    {
        var root = Parse(Full);
        var before = ProfileTuningJson.Read(root);
        Assert.Equal(6, before.Fps);
        Assert.Equal(0.03, before.ChangeThreshold);

        ProfileTuningJson.Apply(root, before, before with { Fps = null, ChangeThreshold = null });

        var profile = ProfileSerializer.FromJson(root.ToJsonString());
        Assert.Equal(ChangeDetectionProfileSettings.AppDefaultFps, profile.ChangeDetection?.Fps);
        Assert.Equal(ChangeDetectionProfileSettings.AppDefaultThreshold, profile.ChangeDetection?.Threshold);
        Assert.Equal(7, root["changeDetection"]!["przyszlyParametr"]!.GetValue<int>());
    }

    [Fact]
    public void Sekcja_wykrywania_zmian_z_jednym_polem_pokazuje_w_edytorze_wartosci_ktore_dzialaja()
    {
        var tuning = ProfileTuningJson.Read(Parse("""{ "id": "a", "name": "A", "sourceLanguage": "en", "changeDetection": { "fps": 10 } }"""));

        Assert.Equal(10, tuning.Fps);
        Assert.Equal(new ChangeDetectionProfileSettings().Threshold, tuning.ChangeThreshold);
    }

    [Fact]
    public void Kopia_dostaje_nowe_id_i_nazwe_bez_recepty_korpusu()
    {
        var original = Parse(Full);
        var root = Parse(Full);

        ProfileTuningJson.PrepareCopy(root, "latarnia-wlasny", "Mroczna Latarnia (własny)");

        Assert.Equal("latarnia-wlasny", root["id"]!.GetValue<string>());
        Assert.Equal("Mroczna Latarnia (własny)", root["name"]!.GetValue<string>());
        Assert.Equal(original["id"]!.GetValue<string>(), root["basedOn"]!.GetValue<string>());
        Assert.False(root.ContainsKey("corpus"));
        foreach (var key in original.Select(static p => p.Key).Where(static k => k is not ("id" or "name" or "corpus" or "basedOn")))
        {
            Assert.True(JsonNode.DeepEquals(original[key], root[key]), key);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(31)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Liczba_analiz_spoza_zakresu_jest_odrzucona(double fps)
    {
        var before = ProfileTuningJson.Read(Parse(Minimal));

        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Fps = fps }, []));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.51)]
    [InlineData(double.NaN)]
    public void Prog_zmiany_spoza_zakresu_jest_odrzucony(double threshold)
    {
        var before = ProfileTuningJson.Read(Parse(Minimal));

        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { ChangeThreshold = threshold }, []));
    }

    [Fact]
    public void Wartosci_na_granicach_zakresow_sa_dozwolone()
    {
        var before = ProfileTuningJson.Read(Parse(Minimal));

        Assert.Empty(ProfileTuningRules.Validate(before, before with { Fps = 1, ChangeThreshold = 0.5, OcrUpscale = 4.0, OcrMinTextHeight = 4 }, []));
        Assert.Empty(ProfileTuningRules.Validate(before, before with { Fps = 30, ChangeThreshold = 0, OcrUpscale = 1.0, OcrMinTextHeight = 64 }, []));
    }

    [Fact]
    public void Pozostale_zakresy_i_formaty_sa_sprawdzane()
    {
        var before = ProfileTuningJson.Read(Parse(Minimal));

        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { OcrUpscale = 0.9 }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { OcrUpscale = 4.5 }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { OcrMinTextHeight = 3 }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { OcrMinTextHeight = 65 }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Glossary = "..\\obcy" }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Glossary = ".." }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { FontFamily = "C:\\Fonts\\krój.ttf" }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { RecommendedMode = "auto" }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { SourceLanguage = " " }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Description = "linia\nlinia" }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Description = new string('a', 2001) }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { ProcessNames = ["latarnia.exe", "LATARNIA"] }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { ProcessNames = ["C:\\Gry\\latarnia.exe"] }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { ProcessNames = [".exe"] }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { WindowTitles = ["Latarnia", " latarnia "] }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { WindowTitles = ["   "] }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Name = "Inna gra" }, ["inna GRA"]));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Name = " " }, []));
        Assert.Empty(ProfileTuningRules.Validate(before, before with { Glossary = "path-of-exile-2", RecommendedMode = "manual-region", ProcessNames = [] }, []));
    }

    [Theory]
    [InlineData(-0.1, 0, 0.2, 0.1)]
    [InlineData(0, 0, 0, 0.1)]
    [InlineData(0.9, 0, 0.2, 0.1)]
    [InlineData(0, 0.95, 0.2, 0.1)]
    [InlineData(double.NaN, 0, 0.2, 0.1)]
    public void Pomijany_obszar_musi_byc_ulamkiem_okna_w_jego_granicach(double x, double y, double width, double height)
    {
        var before = ProfileTuningJson.Read(Parse(Minimal));
        var region = new RelativeRegion { X = x, Y = y, Width = width, Height = height };

        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { IgnoreRegions = [region] }, []));
    }

    [Fact]
    public void Niezmieniona_wartosc_spoza_zakresu_edytora_nie_blokuje_zapisu_innych_pol()
    {
        var root = Parse("""{ "id": "a", "name": "A", "sourceLanguage": "en", "changeDetection": { "fps": 0.5, "threshold": 0.9 } }""");
        var before = ProfileTuningJson.Read(root);
        Assert.Empty(ProfileValidator.Validate(ProfileSerializer.FromJson(root.ToJsonString())));

        Assert.Empty(ProfileTuningRules.Validate(before, before with { FontFamily = "Georgia" }, []));
        Assert.NotEmpty(ProfileTuningRules.Validate(before, before with { Fps = 0.6 }, []));
    }

    [Fact]
    public void Tryb_prywatny_blokuje_nazwe_procesy_tytuly_i_opis()
    {
        var before = ProfileTuningJson.Read(Parse(Full));

        Assert.NotNull(ProfileTuningRules.PrivateModeViolation(before, before with { Name = "Inna" }));
        Assert.NotNull(ProfileTuningRules.PrivateModeViolation(before, before with { ProcessNames = ["latarnia.exe"] }));
        Assert.NotNull(ProfileTuningRules.PrivateModeViolation(before, before with { WindowTitles = [] }));
        Assert.NotNull(ProfileTuningRules.PrivateModeViolation(before, before with { Description = "Gram w Mroczną Latarnię" }));
    }

    [Fact]
    public void Tryb_prywatny_pozwala_stroic_OCR_kroj_slownik_i_wykrywanie_zmian()
    {
        var before = ProfileTuningJson.Read(Parse(Full));
        var tuned = before with
        {
            OcrUpscale = 3.0,
            OcrMinTextHeight = 12,
            Fps = 10,
            ChangeThreshold = 0.01,
            FontFamily = "Cambria",
            Glossary = null,
            RecommendedMode = ProfileTuningRules.LiveMode,
            IgnoreRegions = [],
        };

        Assert.Null(ProfileTuningRules.PrivateModeViolation(before, tuned));
        Assert.Null(ProfileTuningRules.PrivateModeViolation(before, before));
    }

    [Fact]
    public void Porownanie_strojenia_patrzy_na_zawartosc_list()
    {
        var before = ProfileTuningJson.Read(Parse(Full));
        var same = ProfileTuningJson.Read(Parse(Full));

        Assert.Equal(before, same);
        Assert.NotEqual(before, same with { IgnoreRegions = [new RelativeRegion { X = 0.425, Y = 0, Width = 0.15, Height = 0.09 }] });
        Assert.NotEqual(before, same with { ProcessNames = ["latarnia.exe"] });
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("6", 6.0)]
    [InlineData("0,02", 0.02)]
    [InlineData("0.02", 0.02)]
    [InlineData(" 2,5 ", 2.5)]
    public void Liczby_z_pola_przyjmuja_przecinek_i_kropke(string text, double? expected)
    {
        Assert.True(ProfileNumbers.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,000,5")]
    [InlineData("1e3")]
    public void Niepoprawna_liczba_nie_przechodzi(string text)
    {
        Assert.False(ProfileNumbers.TryParse(text, out _));
    }

    [Fact]
    public void Liczby_sa_pokazywane_po_polsku_i_wracaja_bez_straty()
    {
        Assert.Equal("0,025", ProfileNumbers.Format(0.025));
        Assert.Equal("6", ProfileNumbers.Format(6));
        Assert.Equal(string.Empty, ProfileNumbers.Format(null));
        Assert.True(ProfileNumbers.TryParse(ProfileNumbers.Format(0.12345), out var back));
        Assert.Equal(0.12345, back);
        Assert.True(ProfileNumbers.TryParseInteger("10", out var whole));
        Assert.Equal(10, whole);
        Assert.False(ProfileNumbers.TryParseInteger("10,5", out _));
    }

    [Fact]
    public void Nazwa_kopii_jest_unikalna_i_miesci_sie_w_limicie()
    {
        Assert.Equal("Mroczna Latarnia (własny)", ProfileNames.SuggestCopy("Mroczna Latarnia", ["Mroczna Latarnia"]));
        Assert.Equal("Mroczna Latarnia (własny 3)",
            ProfileNames.SuggestCopy("Mroczna Latarnia", ["mroczna latarnia (WŁASNY)", "Mroczna Latarnia (własny 2)"]));
        var longName = ProfileNames.SuggestCopy(new string('x', ProfileNames.MaxLength), []);
        Assert.True(longName.Length <= ProfileNames.MaxLength, longName);
        Assert.EndsWith(" (własny)", longName);
        Assert.Null(ProfileNames.Validate(longName, []));
    }

    [Fact]
    public void Podsumowanie_profilu_pokazuje_ustawienia_tylko_gdy_sa()
    {
        var full = ProfileSerializer.FromJson(Full);
        var minimal = ProfileSerializer.FromJson(Minimal);

        var rows = ProfileSummary.Describe(full).ToDictionary(static r => r.Label, static r => r.Value);
        Assert.Equal("latarnia.exe, LatarniaLauncher.exe", rows["Procesy"]);
        Assert.Equal("2×", rows["Powiększenie OCR"]);
        Assert.Equal("10 px", rows["Min. wysokość tekstu"]);
        Assert.Equal("6 analiz/s, próg 0,03", rows["Wykrywanie zmian"]);
        Assert.Equal("Georgia", rows["Krój nakładki"]);
        Assert.Equal("ręczne zaznaczenie regionu", rows["Zalecany tryb"]);
        Assert.Contains("Reguły kroju", rows.Keys);
        Assert.Contains("Pomijane obszary", rows.Keys);
        Assert.Contains("Recepta korpusu", rows.Keys);

        Assert.Equal(["Procesy", "Język gry"], ProfileSummary.Describe(minimal).Select(static r => r.Label));
    }

    [Fact]
    public void Samo_powiekszenie_OCR_nie_pokazuje_domyslnej_minimalnej_wysokosci()
    {
        var profile = ProfileSerializer.FromJson("""{ "id": "a", "name": "A", "sourceLanguage": "en", "ocr": { "upscale": 2 } }""");

        var labels = ProfileSummary.Describe(profile).Select(static r => r.Label).ToList();

        Assert.Contains("Powiększenie OCR", labels);
        Assert.DoesNotContain("Min. wysokość tekstu", labels);
    }

    [Fact]
    public void Puste_listy_procesow_i_tytulow_w_pliku_nie_psuja_podsumowania_ani_edytora()
    {
        var json = """{ "id": "a", "name": "A", "sourceLanguage": "en", "processNames": null, "windowTitles": null }""";
        var profile = ProfileSerializer.FromJson(json);

        Assert.Empty(profile.ProcessNames);
        Assert.Empty(profile.WindowTitles);
        Assert.NotEmpty(ProfileSummary.Describe(profile));
        Assert.Empty(ProfileTuningJson.Read(Parse(json)).WindowTitles);
    }

    [Fact]
    public void Bardzo_mala_liczba_z_pliku_wraca_z_pola_bez_zapisu_wykladniczego()
    {
        var text = ProfileNumbers.Format(0.00001);

        Assert.Equal("0,00001", text);
        Assert.True(ProfileNumbers.TryParse(text, out var back));
        Assert.Equal(0.00001, back);
    }
}
