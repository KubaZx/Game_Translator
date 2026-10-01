using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class LiveHotkeySettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Domyslne_ustawienia_maja_skrot_live_i_brak_zapamietanej_gry()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+Shift+L", settings.LiveToggleHotkey);
        Assert.Null(settings.LastGameProcess);
        Assert.Null(settings.LastGameTitle);
    }

    [Fact]
    public void Skrot_live_i_zapamietana_gra_przechodza_zapis_i_odczyt()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);
        store.Save(new AppSettings
        {
            LiveToggleHotkey = "Ctrl+Alt+F9",
            LastGameProcess = "witcher3.exe",
            LastGameTitle = "The Witcher 3",
        });

        var loaded = store.Load();

        Assert.Equal("Ctrl+Alt+F9", loaded.LiveToggleHotkey);
        Assert.Equal("witcher3.exe", loaded.LastGameProcess);
        Assert.Equal("The Witcher 3", loaded.LastGameTitle);
        var json = File.ReadAllText(paths.SettingsPath);
        Assert.Contains("\"liveToggleHotkey\"", json);
        Assert.Contains("\"lastGameProcess\"", json);
        Assert.Contains("\"lastGameTitle\"", json);
    }

    [Fact]
    public void Bez_zapamietanej_gry_plik_nie_zawiera_pustych_pol()
    {
        var paths = new AppPaths(_temp.Path);
        new JsonSettingsStore(paths).Save(new AppSettings());

        var json = File.ReadAllText(paths.SettingsPath);

        Assert.DoesNotContain("lastGameProcess", json);
        Assert.DoesNotContain("lastGameTitle", json);
    }

    [Fact]
    public void Stary_plik_ustawien_bez_skrotu_live_laduje_sie_z_domyslnymi()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """
            {
              "sourceLanguage": "en",
              "targetLanguage": "pl",
              "provider": "DeepL",
              "translateHotkey": "Ctrl+Shift+T",
              "toggleOverlayHotkey": "Ctrl+Shift+H",
              "privateMode": true
            }
            """);

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("DeepL", loaded.Provider);
        Assert.True(loaded.PrivateMode);
        Assert.Equal("Ctrl+Shift+T", loaded.TranslateHotkey);
        Assert.Equal("Ctrl+Shift+L", loaded.LiveToggleHotkey);
        Assert.Null(loaded.LastGameProcess);
        Assert.Null(loaded.LastGameTitle);
    }
}
