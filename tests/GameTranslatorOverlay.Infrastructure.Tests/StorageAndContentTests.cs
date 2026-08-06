using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gto-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Sprzątanie po testach nie może wywalać testu.
        }
    }
}

public sealed class DpapiSecretsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Zapis_odczyt_i_usuniecie_sekretu()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new DpapiSecretsStore(new AppPaths(_temp.Path));

        store.Save("deepl-api-key", "sekret:fx");
        Assert.Equal("sekret:fx", store.Load("deepl-api-key"));

        store.Delete("deepl-api-key");
        Assert.Null(store.Load("deepl-api-key"));
    }

    [Fact]
    public void Odczyt_nieistniejacego_sekretu_zwraca_null()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new DpapiSecretsStore(new AppPaths(_temp.Path));
        Assert.Null(store.Load("nie-ma"));
    }

    [Fact]
    public void Sekret_jest_zaszyfrowany_na_dysku()
    {
        if (!OperatingSystem.IsWindows()) return;

        var paths = new AppPaths(_temp.Path);
        var store = new DpapiSecretsStore(paths);
        store.Save("klucz", "jawna-wartosc-klucza");

        var files = Directory.GetFiles(paths.SecretsDirectory);
        var raw = File.ReadAllBytes(Assert.Single(files));

        Assert.DoesNotContain("jawna-wartosc-klucza", System.Text.Encoding.UTF8.GetString(raw));
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Zapis_i_odczyt_ustawien()
    {
        var store = new JsonSettingsStore(new AppPaths(_temp.Path));
        store.Save(new AppSettings { Provider = "Mock", CacheOnlyMode = true, OverlayFontSize = 18 });

        var loaded = store.Load();

        Assert.Equal("Mock", loaded.Provider);
        Assert.True(loaded.CacheOnlyMode);
        Assert.Equal(18, loaded.OverlayFontSize);
    }

    [Fact]
    public void Uszkodzony_plik_ustawien_wraca_do_domyslnych_z_kopia_zapasowa()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, "{to nie jest json");

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("DeepL", loaded.Provider);
        Assert.True(File.Exists(paths.SettingsPath + ".corrupt.bak"));
    }

    [Fact]
    public void Brak_pliku_ustawien_zwraca_domyslne()
    {
        var loaded = new JsonSettingsStore(new AppPaths(_temp.Path)).Load();

        Assert.Equal("en", loaded.SourceLanguage);
        Assert.Equal("pl", loaded.TargetLanguage);
    }
}

public sealed class ContentCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ProfileCatalog_laduje_poprawne_profile_i_raportuje_bledne()
    {
        var root = Path.Combine(_temp.Path, "profiles");
        Directory.CreateDirectory(Path.Combine(root, "dobry"));
        Directory.CreateDirectory(Path.Combine(root, "zepsuty"));
        File.WriteAllText(Path.Combine(root, "dobry", "profile.json"),
            """{"id":"dobry","name":"Dobry profil","profileVersion":1,"sourceLanguage":"en"}""");
        File.WriteAllText(Path.Combine(root, "zepsuty", "profile.json"), "{nie-json");

        var (profiles, issues) = new ProfileCatalog([root]).LoadAll();

        Assert.Single(profiles);
        Assert.Equal("dobry", profiles[0].Id);
        Assert.Single(issues);
    }

    [Fact]
    public void GlossaryCatalog_laduje_slownik_wg_pary_jezykow()
    {
        var root = Path.Combine(_temp.Path, "glossaries");
        Directory.CreateDirectory(Path.Combine(root, "global"));
        File.WriteAllText(Path.Combine(root, "global", "en-pl.json"),
            """{"name":"global","sourceLanguage":"en","targetLanguage":"pl","version":1,"terms":[{"source":"Armour","target":"Pancerz"}]}""");

        var (document, issue) = new GlossaryCatalog([root]).TryLoad("global", "en", "pl");

        Assert.Null(issue);
        Assert.NotNull(document);
        Assert.Single(document.Terms);
    }

    [Fact]
    public void GlossaryCatalog_zglasza_brakujacy_slownik()
    {
        var (document, issue) = new GlossaryCatalog([_temp.Path]).TryLoad("nie-ma", "en", "pl");

        Assert.Null(document);
        Assert.NotNull(issue);
    }

    [Fact]
    public void UserGlossaryStore_dodaje_i_nadpisuje_terminy()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new UserGlossaryStore(paths);

        store.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"), "en", "pl");
        store.AddTerm(new GlossaryTerm("Waystone", "Kamień przejścia"), "en", "pl");

        var document = store.Load("en", "pl");

        var term = Assert.Single(document.Terms);
        Assert.Equal("Kamień przejścia", term.Target);
    }
}
