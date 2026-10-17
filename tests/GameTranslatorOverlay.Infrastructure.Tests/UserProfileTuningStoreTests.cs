using System.Text.Json.Nodes;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Infrastructure.Content;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class UserProfileTuningStoreTests : IDisposable
{
    private static readonly string RepoProfiles =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles"));

    private readonly TempDirectory _temp = new();
    private readonly string _bundled;
    private readonly string _user;

    public UserProfileTuningStoreTests()
    {
        _bundled = Path.Combine(_temp.Path, "app", "profiles");
        _user = Path.Combine(_temp.Path, "dane", "profiles");
        Directory.CreateDirectory(_bundled);
    }

    public void Dispose() => _temp.Dispose();

    private UserProfileStore Store() => new(_user, [_bundled]);

    private ProfileCatalog Catalog() => new([_bundled, _user]);

    private static string Write(string root, string folder, string json)
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "profile.json");
        File.WriteAllText(file, json);
        return file;
    }

    private const string OwnWithExtras = """
        {
          "id": "latarnia",
          "name": "Mroczna Latarnia",
          "profileVersion": 1,
          "author": "gracz",
          "processNames": ["latarnia.exe"],
          "sourceLanguage": "en",
          "overlay": {
            "fontFamily": "Georgia",
            "fontRules": [ { "fontFamily": "Cambria", "textColor": "#FBFA86", "outlined": true } ]
          },
          "live": { "ignoreRegions": [ { "x": 0.425, "y": 0.0, "width": 0.15, "height": 0.08 } ] },
          "corpus": {
            "format": "unity-textasset",
            "container": "Latarnia_Data/data.unity3d",
            "file": "resources.assets",
            "sources": [ { "id": "napisy", "parser": "srt", "kind": "subtitle", "include": ["*"] } ]
          },
          "online": false,
          "minAppVersion": "0.1.0",
          "przyszlePole": { "x": 1 },
        }
        """;

    private static string ProfileJson(string file) => File.ReadAllText(file);

    private static JsonObject Node(string file) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(file), new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;

    [Fact]
    public void Zapis_strojenia_wczytuje_sie_z_katalogu_bez_restartu_z_nowymi_wartosciami()
    {
        var created = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);
        var file = Path.Combine(_user, created.Id, "profile.json");
        var before = Store().ReadTuning(file, created.Id);
        var after = before with
        {
            Name = "  Mroczna   Latarnia — strojenie ",
            ProcessNames = ["latarnia.exe", "LatarniaLauncher.exe"],
            WindowTitles = ["Mroczna Latarnia"],
            Glossary = "latarnia",
            OcrUpscale = 3.0,
            OcrMinTextHeight = 12,
            Fps = 10,
            ChangeThreshold = 0.02,
            FontFamily = "Cambria",
            RecommendedMode = ProfileTuningRules.LiveMode,
            Description = "Mój profil",
            IgnoreRegions = [new RelativeRegion { X = 0.1, Y = 0.1, Width = 0.2, Height = 0.05 }],
        };

        Store().Update(file, created.Id, before, after, Catalog().LoadAll().Profiles, privateMode: false);

        var (profiles, issues) = Catalog().LoadAll();
        Assert.Empty(issues);
        var profile = Assert.Single(profiles);
        Assert.Equal("Mroczna Latarnia — strojenie", profile.Name);
        Assert.Equal(["latarnia.exe", "LatarniaLauncher.exe"], profile.ProcessNames);
        Assert.Equal(3.0, profile.Ocr?.Upscale);
        Assert.Equal(12, profile.Ocr?.MinTextHeight);
        Assert.Equal(10, profile.ChangeDetection?.Fps);
        Assert.Equal(0.02, profile.ChangeDetection?.Threshold);
        Assert.Equal("Cambria", profile.Overlay?.FontFamily);
        Assert.Equal("latarnia", profile.Glossary);
        Assert.Equal("live", profile.RecommendedMode);
        Assert.Single(profile.Live!.IgnoreRegions!);
        Assert.Equal(after with { Name = "Mroczna Latarnia — strojenie" }, Store().ReadTuning(file, created.Id));
        Assert.Empty(Directory.GetFiles(_user, "*.tmp", SearchOption.AllDirectories));
        Assert.Same(profile, ProfileProcessMatcher.FindByProcess(profiles, "LatarniaLauncher.EXE"));
    }

    [Fact]
    public void Zapis_zachowuje_pola_ktorych_edytor_nie_pokazuje()
    {
        var file = Write(_user, "latarnia", OwnWithExtras);
        var original = Node(file);
        var before = Store().ReadTuning(file, "latarnia");

        Store().Update(file, "latarnia", before, before with { Fps = 8, FontFamily = "Cambria" }, Catalog().LoadAll().Profiles, privateMode: false);

        var saved = Node(file);
        foreach (var key in new[] { "id", "name", "profileVersion", "author", "processNames", "live", "corpus", "online", "minAppVersion", "przyszlePole" })
        {
            Assert.True(JsonNode.DeepEquals(original[key], saved[key]), key);
        }
        Assert.True(JsonNode.DeepEquals(original["overlay"]!["fontRules"], saved["overlay"]!["fontRules"]));
        var profile = Assert.Single(Catalog().LoadAll().Profiles);
        Assert.Equal("Cambria", profile.Overlay?.FontFamily);
        Assert.Equal(8, profile.ChangeDetection?.Fps);
        Assert.Equal(ChangeDetectionProfileSettings.AppDefaultThreshold, profile.ChangeDetection?.Threshold);
        Assert.NotNull(profile.Corpus);
    }

    [Fact]
    public void Zapis_bez_zmian_nie_dotyka_pliku()
    {
        var file = Write(_user, "latarnia", OwnWithExtras);
        var text = ProfileJson(file);
        var before = Store().ReadTuning(file, "latarnia");

        Store().Update(file, "latarnia", before, before, Catalog().LoadAll().Profiles, privateMode: false);

        Assert.Equal(text, ProfileJson(file));
    }

    [Theory]
    [InlineData(45.0, null)]
    [InlineData(null, 0.75)]
    public void Wartosc_spoza_zakresu_nie_trafia_do_pliku(double? fps, double? threshold)
    {
        var created = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);
        var file = Path.Combine(_user, created.Id, "profile.json");
        var text = ProfileJson(file);
        var before = Store().ReadTuning(file, created.Id);

        Assert.Throws<UserProfileException>(() =>
            Store().Update(file, created.Id, before, before with { Fps = fps, ChangeThreshold = threshold }, [], privateMode: false));

        Assert.Equal(text, ProfileJson(file));
    }

    [Fact]
    public void Zajeta_nazwa_innego_profilu_jest_odrzucona()
    {
        Write(_bundled, "zestaw", """{"id":"zestaw","name":"Gra z zestawu","sourceLanguage":"en","processNames":["zestaw.exe"]}""");
        var created = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);
        var file = Path.Combine(_user, created.Id, "profile.json");
        var before = Store().ReadTuning(file, created.Id);

        var error = Assert.Throws<UserProfileException>(() =>
            Store().Update(file, created.Id, before, before with { Name = "gra z ZESTAWU" }, Catalog().LoadAll().Profiles, privateMode: false));

        Assert.Contains("już istnieje", error.Message);
    }

    [Fact]
    public void Tryb_prywatny_nie_zapisuje_nazwy_procesow_tytulow_ani_opisu()
    {
        var file = Write(_user, "latarnia", OwnWithExtras);
        var text = ProfileJson(file);
        var before = Store().ReadTuning(file, "latarnia");

        foreach (var change in new[]
        {
            before with { Name = "Inna nazwa" },
            before with { ProcessNames = ["latarnia.exe", "inny.exe"] },
            before with { WindowTitles = ["Mroczna Latarnia"] },
            before with { Description = "Gram w Mroczną Latarnię" },
        })
        {
            Assert.Throws<UserProfileException>(() => Store().Update(file, "latarnia", before, change, [], privateMode: true));
            Assert.Equal(text, ProfileJson(file));
        }
    }

    [Fact]
    public void Tryb_prywatny_zapisuje_strojenie_OCR_kroju_slownika_i_wykrywania_zmian()
    {
        var file = Write(_user, "latarnia", OwnWithExtras);
        var before = Store().ReadTuning(file, "latarnia");

        Store().Update(file, "latarnia", before,
            before with { OcrUpscale = 2.0, Fps = 12, ChangeThreshold = 0.01, FontFamily = null, Glossary = "latarnia", IgnoreRegions = [] },
            [], privateMode: true);

        var profile = Assert.Single(Catalog().LoadAll().Profiles);
        Assert.Equal(2.0, profile.Ocr?.Upscale);
        Assert.Equal(12, profile.ChangeDetection?.Fps);
        Assert.Null(profile.Overlay?.FontFamily);
        Assert.Single(profile.Overlay!.FontRules!);
        Assert.Null(profile.Live);
        Assert.Equal("Mroczna Latarnia", profile.Name);
    }

    [Fact]
    public void Zmiana_pliku_na_dysku_od_otwarcia_edytora_przerywa_zapis()
    {
        var file = Write(_user, "latarnia", OwnWithExtras);
        var before = Store().ReadTuning(file, "latarnia");
        var edited = ProfileJson(file).Replace("\"Georgia\"", "\"Book Antiqua\"", StringComparison.Ordinal);
        File.WriteAllText(file, edited);

        Assert.Throws<UserProfileException>(() =>
            Store().Update(file, "latarnia", before, before with { Fps = 10 }, [], privateMode: false));

        Assert.Equal(edited, ProfileJson(file));
    }

    [Fact]
    public void Profilu_z_zestawu_nie_da_sie_dostosowac()
    {
        var bundled = Write(_bundled, "zestaw", """{"id":"zestaw","name":"Gra z zestawu","sourceLanguage":"en","processNames":["zestaw.exe"]}""");
        var text = ProfileJson(bundled);
        var before = ProfileTuningJson.Read(Node(bundled));

        Assert.Throws<UserProfileException>(() => Store().ReadTuning(bundled, "zestaw"));
        Assert.Throws<UserProfileException>(() => Store().Update(bundled, "zestaw", before, before with { Fps = 10 }, [], privateMode: false));

        Assert.Equal(text, ProfileJson(bundled));
    }

    [Fact]
    public void Kopia_profilu_z_zestawu_ma_nowe_id_i_nazwe_bez_korpusu_a_reszta_zostaje()
    {
        var bundled = Write(_bundled, "latarnia", OwnWithExtras);
        var original = Node(bundled);
        var loaded = Catalog().LoadAll().Profiles;

        var copy = Store().CreateCopy(bundled, "latarnia", loaded);

        Assert.Equal("mroczna-latarnia-wlasny", copy.Id);
        Assert.Equal("Mroczna Latarnia (własny)", copy.Name);
        Assert.Null(copy.Corpus);
        Assert.Equal("latarnia", copy.BasedOn);
        Assert.Equal("latarnia", copy.ContentId);
        var copyFile = Path.Combine(_user, copy.Id, "profile.json");
        var saved = Node(copyFile);
        foreach (var key in original.Select(static p => p.Key).Where(static k => k is not ("id" or "name" or "corpus" or "basedOn")))
        {
            Assert.True(JsonNode.DeepEquals(original[key], saved[key]), key);
        }
        Assert.False(saved.ContainsKey("corpus"));
        Assert.Equal(original.ToJsonString(), Node(bundled).ToJsonString());

        var (entries, issues) = Catalog().LoadEntries();
        Assert.Empty(issues);
        Assert.Equal(2, entries.Count);
        var store = Store();
        var match = ProfileProcessMatcher.FindByProcess(entries.Select(static e => e.Profile), "latarnia.exe",
            p => store.Owns(entries.Single(e => ReferenceEquals(e.Profile, p)).FilePath));
        Assert.Equal(copy.Id, match?.Id);
        Assert.Equal("latarnia", ProfileProcessMatcher.FindByProcess(entries.Select(static e => e.Profile), "latarnia.exe")?.Id);
    }

    [Fact]
    public void Kolejna_kopia_dostaje_unikalne_id_i_nazwe()
    {
        var bundled = Write(_bundled, "latarnia", OwnWithExtras);
        var first = Store().CreateCopy(bundled, "latarnia", Catalog().LoadAll().Profiles);
        Directory.CreateDirectory(Path.Combine(_user, "mroczna-latarnia-wlasny-2"));

        var second = Store().CreateCopy(bundled, "latarnia", Catalog().LoadAll().Profiles);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Mroczna Latarnia (własny 2)", second.Name);
        Assert.Equal("mroczna-latarnia-wlasny-2-2", second.Id);
        var (profiles, issues) = Catalog().LoadAll();
        Assert.Empty(issues);
        Assert.Equal(3, profiles.Select(static p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Kopia_dolaczonego_profilu_Escape_Academy_wczytuje_sie_bez_korpusu_z_regulami_kroju()
    {
        var source = Path.Combine(RepoProfiles, "escape-academy", "profile.json");
        Assert.True(File.Exists(source), source);
        var loaded = new ProfileCatalog([RepoProfiles]).LoadAll().Profiles;

        var copy = Store().CreateCopy(source, "escape-academy", loaded);

        var (profiles, issues) = new ProfileCatalog([RepoProfiles, _user]).LoadAll();
        Assert.Empty(issues);
        var reloaded = Assert.Single(profiles, p => p.Id == copy.Id);
        Assert.Null(reloaded.Corpus);
        Assert.Equal("escape-academy", reloaded.ContentId);
        Assert.Equal("Lexend Deca", reloaded.Overlay?.FontFamily);
        Assert.Single(reloaded.Overlay!.FontRules!);
        Assert.Single(reloaded.Live!.IgnoreRegions!);
        Assert.Equal(["Escape Academy.exe"], reloaded.ProcessNames);
        Assert.Equal("Escape Academy (własny)", reloaded.Name);
    }

    [Fact]
    public void Kopia_odmawia_dla_innego_identyfikatora_i_brakujacego_pliku()
    {
        var bundled = Write(_bundled, "latarnia", OwnWithExtras);

        Assert.Throws<UserProfileException>(() => Store().CreateCopy(bundled, "inny", []));
        Assert.Throws<UserProfileException>(() => Store().CreateCopy(Path.Combine(_bundled, "brak", "profile.json"), "brak", []));
        Assert.False(Directory.Exists(_user) && Directory.EnumerateFileSystemEntries(_user).Any());
    }

    [Fact]
    public void Lista_slownikow_ma_slowniki_z_zestawu_i_folderu_danych_dla_pary_jezykow()
    {
        var app = Path.Combine(_temp.Path, "app", "glossaries");
        var data = Path.Combine(_temp.Path, "dane", "glossaries");
        void Glossary(string root, string id, string file = "en-pl.json")
        {
            Directory.CreateDirectory(Path.Combine(root, id));
            File.WriteAllText(Path.Combine(root, id, file), """{"name":"x","sourceLanguage":"en","targetLanguage":"pl","terms":[]}""");
        }
        Glossary(app, "global");
        Glossary(app, "latarnia");
        Glossary(app, "wspolny");
        Glossary(data, "wspolny");
        Glossary(data, "moj-slownik");
        Glossary(data, "tylko-niemiecki", "de-pl.json");
        Glossary(data, "zła nazwa");

        var ids = new GlossaryCatalog([app, data, Path.Combine(_temp.Path, "nie-ma")]).ListIds("en", "pl");

        Assert.Equal(["latarnia", "moj-slownik", "wspolny"], ids);
    }
}
