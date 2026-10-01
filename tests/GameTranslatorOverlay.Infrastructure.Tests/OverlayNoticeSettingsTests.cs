using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class OverlayNoticeSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gto-notice-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Komunikaty_w_nakladce_sa_domyslnie_wlaczone()
    {
        Assert.True(new AppSettings().ShowOverlayNotices);
    }

    [Fact]
    public void Wylaczenie_komunikatow_przetrwa_zapis_i_odczyt()
    {
        var store = new JsonSettingsStore(new AppPaths(_root));
        store.Save(new AppSettings { ShowOverlayNotices = false, Provider = "Mock" });

        var loaded = store.Load();

        Assert.False(loaded.ShowOverlayNotices);
        Assert.Equal("Mock", loaded.Provider);
    }

    [Fact]
    public void Stary_plik_ustawien_bez_pola_wlacza_komunikaty()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        // Plik z wersji sprzed komunikatów w nakładce — bez pola showOverlayNotices.
        File.WriteAllText(paths.SettingsPath, """
            {
              "sourceLanguage": "en",
              "targetLanguage": "pl",
              "provider": "Mock",
              "cacheOnlyMode": true,
              "resultDisplayMode": "overlay",
              "overlayFontSize": 0
            }
            """);

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.True(loaded.ShowOverlayNotices);
        Assert.True(loaded.CacheOnlyMode);
        Assert.Equal("overlay", loaded.ResultDisplayMode);
        Assert.False(File.Exists(paths.SettingsPath + ".corrupt.bak"));
    }
}
