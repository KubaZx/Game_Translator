using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class PlayerGenderSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("female", PlayerGender.Female)]
    [InlineData("male", PlayerGender.Male)]
    [InlineData("unknown", PlayerGender.Unknown)]
    public void Plec_gracza_przechodzi_zapis_i_odczyt_ustawien(string setting, PlayerGender expected)
    {
        var store = new JsonSettingsStore(new AppPaths(_temp.Path));
        store.Save(new AppSettings { PlayerGender = setting });

        var loaded = store.Load();

        Assert.Equal(setting, loaded.PlayerGender);
        Assert.Equal(expected, PlayerGenders.Parse(loaded.PlayerGender));
        Assert.Contains("\"playerGender\"", File.ReadAllText(new AppPaths(_temp.Path).SettingsPath));
    }

    [Fact]
    public void Stary_plik_ustawien_bez_plci_gracza_laduje_sie_jako_nieznana()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """
            {
              "sourceLanguage": "en",
              "targetLanguage": "pl",
              "provider": "Claude",
              "cacheOnlyMode": false,
              "privateMode": true
            }
            """);

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("Claude", loaded.Provider);
        Assert.True(loaded.PrivateMode);
        Assert.Equal("unknown", loaded.PlayerGender);
        Assert.Equal(PlayerGender.Unknown, PlayerGenders.Parse(loaded.PlayerGender));
    }

    [Fact]
    public void Nieznana_wartosc_plci_w_pliku_nie_psuje_ustawien()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """{"provider": "Mock", "playerGender": "Kobieta?"}""");

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("Mock", loaded.Provider);
        Assert.Equal(PlayerGender.Unknown, PlayerGenders.Parse(loaded.PlayerGender));
    }

    [Fact]
    public void Domyslne_ustawienia_maja_nieznana_plec_gracza()
    {
        Assert.Equal(PlayerGenders.UnknownSetting, new AppSettings().PlayerGender);
    }
}
