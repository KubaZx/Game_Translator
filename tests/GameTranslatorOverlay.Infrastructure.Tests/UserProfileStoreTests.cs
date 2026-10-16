using System.Text.Json;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class UserProfileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _bundled;
    private readonly string _user;

    public UserProfileStoreTests()
    {
        _bundled = Path.Combine(_temp.Path, "app", "profiles");
        _user = Path.Combine(_temp.Path, "dane", "profiles");
        Directory.CreateDirectory(_bundled);
    }

    public void Dispose() => _temp.Dispose();

    private UserProfileStore Store() => new(_user, [_bundled]);

    private ProfileCatalog Catalog() => new([_bundled, _user]);

    private string WriteProfile(string root, string folder, string json)
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "profile.json");
        File.WriteAllText(file, json);
        return file;
    }

    private string WriteBundled(string id, string name, string process) =>
        WriteProfile(_bundled, id, $$$"""{"id":"{{{id}}}","name":"{{{name}}}","profileVersion":1,"sourceLanguage":"en","processNames":["{{{process}}}"],"ocr":{"upscale":2.0}}""");

    [Fact]
    public void Utworzony_profil_wczytuje_sie_z_katalogu_i_dopasowuje_po_procesie()
    {
        var created = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);

        var (entries, issues) = Catalog().LoadEntries();

        Assert.Empty(issues);
        var entry = Assert.Single(entries);
        Assert.Equal("mroczna-latarnia", created.Id);
        Assert.Equal(created.Id, entry.Profile.Id);
        Assert.Equal("Mroczna Latarnia", entry.Profile.Name);
        Assert.Equal(Path.Combine(_user, "mroczna-latarnia", "profile.json"), entry.FilePath);
        Assert.Equal(created.Id, Path.GetFileName(Path.GetDirectoryName(entry.FilePath)));
        Assert.Same(entry.Profile, ProfileProcessMatcher.FindByProcess(entries.Select(static e => e.Profile), "Latarnia.EXE"));
        Assert.True(Store().Owns(entry.FilePath));
    }

    [Fact]
    public void Plik_nowego_profilu_ma_tylko_podstawowe_pola_bez_ustawien_OCR()
    {
        var created = Store().Create("Mroczna Latarnia: Opowieść", "latarnia.exe", "en", []);

        var file = Path.Combine(_user, created.Id, "profile.json");
        var text = File.ReadAllText(file);
        using var document = JsonDocument.Parse(text);
        var names = document.RootElement.EnumerateObject().Select(static p => p.Name).ToList();

        Assert.Equal(["id", "name", "profileVersion", "sourceLanguage", "processNames"], names);
        Assert.Equal(1, document.RootElement.GetProperty("profileVersion").GetInt32());
        Assert.Equal("en", document.RootElement.GetProperty("sourceLanguage").GetString());
        Assert.Equal(["latarnia.exe"], document.RootElement.GetProperty("processNames").EnumerateArray().Select(static e => e.GetString()));
        Assert.Contains("Opowieść", text);
        var loaded = ProfileSerializer.FromJson(text);
        Assert.Null(loaded.Ocr);
        Assert.Null(loaded.ChangeDetection);
        Assert.Empty(loaded.WindowTitles);
        Assert.Empty(Directory.GetFiles(_user, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Identyfikator_omija_profile_z_zestawu_i_zajete_foldery()
    {
        WriteBundled("mroczna-latarnia", "Latarnia z zestawu", "inna.exe");
        Directory.CreateDirectory(Path.Combine(_user, "mroczna-latarnia-2"));
        var loaded = Catalog().LoadAll().Profiles;

        var created = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", loaded);

        Assert.Equal("mroczna-latarnia-3", created.Id);
        var (profiles, issues) = Catalog().LoadAll();
        Assert.Empty(issues);
        Assert.Equal(2, profiles.Count);
    }

    [Fact]
    public void Zajeta_nazwa_albo_brak_procesu_nie_tworzy_profilu()
    {
        WriteBundled("latarnia", "Mroczna Latarnia", "inna.exe");
        var loaded = Catalog().LoadAll().Profiles;

        Assert.Throws<UserProfileException>(() => Store().Create(" mroczna latarnia ", "latarnia.exe", "en", loaded));
        Assert.Throws<UserProfileException>(() => Store().Create("Nowa gra", "  ", "en", loaded));
        Assert.Throws<UserProfileException>(() => Store().Create("Nowa gra", ".exe", "en", loaded));
        Assert.Throws<UserProfileException>(() => Store().Create("Nowa gra", "latarnia.exe", " ", loaded));
        Assert.False(Directory.Exists(_user) && Directory.EnumerateFileSystemEntries(_user).Any());
    }

    [Fact]
    public void Zmiana_nazwy_zmienia_tylko_nazwe_i_zostawia_reszte_pliku()
    {
        var file = WriteProfile(_user, "latarnia", """
            {
              "id": "latarnia",
              "Name": "Latarnia",
              "author": "gracz",
              "processNames": ["latarnia.exe"],
              "sourceLanguage": "en",
              "ocr": { "upscale": 2.5, "minTextHeight": 12 },
              "przyszlePole": { "x": 1 },
            }
            """);
        var loaded = Catalog().LoadAll().Profiles;

        Store().Rename(file, "latarnia", "  Mroczna   Latarnia ", loaded);

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        Assert.Equal("Mroczna Latarnia", root.GetProperty("Name").GetString());
        Assert.False(root.TryGetProperty("name", out _));
        Assert.Equal("gracz", root.GetProperty("author").GetString());
        Assert.Equal(2.5, root.GetProperty("ocr").GetProperty("upscale").GetDouble());
        Assert.Equal(1, root.GetProperty("przyszlePole").GetProperty("x").GetInt32());
        var reloaded = Assert.Single(Catalog().LoadAll().Profiles);
        Assert.Equal("Mroczna Latarnia", reloaded.Name);
        Assert.Equal(2.5, reloaded.Ocr?.Upscale);
    }

    [Fact]
    public void Zmiana_nazwy_na_zajeta_albo_dla_innego_identyfikatora_jest_odrzucona()
    {
        WriteBundled("zestaw", "Gra z zestawu", "zestaw.exe");
        var own = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);
        var loaded = Catalog().LoadAll().Profiles;
        var file = Path.Combine(_user, own.Id, "profile.json");
        var before = File.ReadAllText(file);

        Assert.Throws<UserProfileException>(() => Store().Rename(file, own.Id, "gra z ZESTAWU", loaded));
        Assert.Throws<UserProfileException>(() => Store().Rename(file, "inny-id", "Nowa nazwa", loaded));
        Assert.Equal(before, File.ReadAllText(file));

        Store().Rename(file, own.Id, "MROCZNA latarnia", loaded);
        Assert.Equal("MROCZNA latarnia", Assert.Single(Catalog().LoadAll().Profiles, p => p.Id == own.Id).Name);
    }

    [Fact]
    public void Profilu_z_zestawu_nie_da_sie_zmienic_ani_usunac()
    {
        var bundled = WriteBundled("zestaw", "Gra z zestawu", "zestaw.exe");
        var before = File.ReadAllText(bundled);
        var loaded = Catalog().LoadAll().Profiles;

        Assert.False(Store().Owns(bundled));
        Assert.Throws<UserProfileException>(() => Store().Rename(bundled, "zestaw", "Nowa nazwa", loaded));
        Assert.Throws<UserProfileException>(() => Store().Delete(bundled, "zestaw"));

        Assert.Equal(before, File.ReadAllText(bundled));
    }

    [Fact]
    public void Usuniecie_kasuje_tylko_folder_wlasnego_profilu()
    {
        var bundled = WriteBundled("zestaw", "Gra z zestawu", "zestaw.exe");
        var first = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);
        var second = Store().Create("Druga Latarnia", "druga.exe", "en", [first]);
        var file = Path.Combine(_user, first.Id, "profile.json");
        File.WriteAllText(Path.Combine(_user, first.Id, "notatki.txt"), "x");

        Store().Delete(file, first.Id);

        Assert.False(Directory.Exists(Path.Combine(_user, first.Id)));
        Assert.True(File.Exists(Path.Combine(_user, second.Id, "profile.json")));
        Assert.True(File.Exists(bundled));
        Assert.True(Directory.Exists(_user));
        Assert.Equal(["druga-latarnia", "zestaw"], Catalog().LoadAll().Profiles.Select(static p => p.Id).Order());
    }

    [Fact]
    public void Usuniecie_odmawia_dla_innego_identyfikatora_podfolderu_i_folderu_z_innymi_profilami()
    {
        var own = Store().Create("Mroczna Latarnia", "latarnia.exe", "en", []);
        var ownFile = Path.Combine(_user, own.Id, "profile.json");
        var nested = WriteProfile(_user, Path.Combine("paczka", "zagniezdzony"),
            """{"id":"zagniezdzony","name":"Zagnieżdżony","sourceLanguage":"en","processNames":["z.exe"]}""");
        var rootFile = Path.Combine(_user, "profile.json");
        File.WriteAllText(rootFile, """{"id":"w-korzeniu","name":"W korzeniu","sourceLanguage":"en"}""");
        var outer = WriteProfile(_user, "zbiorczy", """{"id":"zbiorczy","name":"Zbiorczy","sourceLanguage":"en"}""");
        WriteProfile(Path.Combine(_user, "zbiorczy"), "wewnetrzny", """{"id":"wewnetrzny","name":"Wewnętrzny","sourceLanguage":"en"}""");

        Assert.Throws<UserProfileException>(() => Store().Delete(ownFile, "inny-id"));
        Assert.Throws<UserProfileException>(() => Store().Delete(nested, "zagniezdzony"));
        Assert.Throws<UserProfileException>(() => Store().Delete(rootFile, "w-korzeniu"));
        Assert.Throws<UserProfileException>(() => Store().Delete(outer, "zbiorczy"));

        Assert.True(File.Exists(ownFile));
        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(rootFile));
        Assert.True(File.Exists(outer));
    }

    [Fact]
    public void Plik_spoza_folderu_danych_nie_jest_wlasnym_profilem()
    {
        var outside = WriteProfile(Path.Combine(_temp.Path, "gdzies"), "obcy", """{"id":"obcy","name":"Obcy","sourceLanguage":"en"}""");

        Assert.False(Store().Owns(outside));
        Assert.False(Store().Owns(null));
        Assert.False(Store().Owns(Path.Combine(_user, "x", "inny.json")));
        Assert.Throws<UserProfileException>(() => Store().Delete(outside, "obcy"));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Folder_danych_wewnatrz_folderu_zestawu_nie_daje_wlasnych_profili()
    {
        var store = new UserProfileStore(Path.Combine(_bundled, "dane"), [_bundled]);
        var file = WriteProfile(Path.Combine(_bundled, "dane"), "gra", """{"id":"gra","name":"Gra","sourceLanguage":"en"}""");

        Assert.False(store.Owns(file));
        Assert.Throws<UserProfileException>(() => store.Delete(file, "gra"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Domyslny_magazyn_pisze_tam_skad_czyta_domyslny_katalog()
    {
        var paths = new AppPaths(Path.Combine(_temp.Path, "folder-danych"));
        var created = UserProfileStore.CreateDefault(paths).Create("Mroczna Latarnia", "latarnia.exe", "en", []);

        var (entries, _) = ProfileCatalog.CreateDefault(paths).LoadEntries();

        var entry = Assert.Single(entries, e => e.Profile.Id == created.Id);
        Assert.StartsWith(paths.ProfilesDirectory, entry.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(UserProfileStore.CreateDefault(paths).Owns(entry.FilePath));
        Assert.Equal("mroczna-latarnia", UserProfileStore.CreateDefault(paths).FolderName(entry.FilePath));
    }
}
