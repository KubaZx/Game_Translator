using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class OverlayFontSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gto-font-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Nowe_ustawienia_biora_kroj_z_profilu_gry()
    {
        Assert.Equal(AppSettings.AutoFontFamily, new AppSettings().OverlayFontFamily);
    }

    [Fact]
    public void Dawny_domyslny_Segoe_UI_przechodzi_na_kroj_z_profilu()
    {
        var paths = Write("""
            { "provider": "Mock", "overlayFontFamily": "Segoe UI", "overlayPlacement": "cover" }
            """);

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal(AppSettings.AutoFontFamily, loaded.OverlayFontFamily);
        Assert.Equal(AppSettings.CurrentOverlayFontRevision, loaded.OverlayFontRevision);
        Assert.Equal("cover", loaded.OverlayPlacement);
    }

    [Fact]
    public void Swiadomie_wybrany_kroj_zostaje()
    {
        var paths = Write("""
            { "provider": "Mock", "overlayFontFamily": "Georgia" }
            """);

        Assert.Equal("Georgia", new JsonSettingsStore(paths).Load().OverlayFontFamily);
    }

    [Fact]
    public void Segoe_UI_wybrany_po_migracji_nie_jest_juz_zmieniany()
    {
        var store = new JsonSettingsStore(new AppPaths(_root));
        store.Save(new AppSettings { OverlayFontFamily = "Segoe UI", Provider = "Mock" });

        Assert.Equal("Segoe UI", store.Load().OverlayFontFamily);
    }

    private AppPaths Write(string json)
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, json);
        return paths;
    }
}
