using GameTranslatorOverlay.Core.Windows;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class HotkeySettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Domyslne_skroty_ustawien_to_skroty_przywracane_w_edytorze()
    {
        var settings = new AppSettings();

        Assert.Equal(HotkeyDefaults.Translate, settings.TranslateHotkey);
        Assert.Equal(HotkeyDefaults.ToggleOverlay, settings.ToggleOverlayHotkey);
        Assert.Equal(HotkeyDefaults.LiveToggle, settings.LiveToggleHotkey);
    }

    [Fact]
    public void Trzy_zmienione_skroty_przechodza_zapis_i_odczyt_w_zapisie_kanonicznym()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);
        var settings = store.Load();
        settings.TranslateHotkey = HotkeyGesture.Normalize("shift+ctrl+f9")!;
        settings.ToggleOverlayHotkey = HotkeyGesture.Normalize("alt+win+o")!;
        settings.LiveToggleHotkey = HotkeyGesture.Normalize("F10")!;
        store.Save(settings);

        var loaded = store.Load();

        Assert.Equal("Ctrl+Shift+F9", loaded.TranslateHotkey);
        Assert.Equal("Alt+Win+O", loaded.ToggleOverlayHotkey);
        Assert.Equal("F10", loaded.LiveToggleHotkey);
        Assert.False(File.Exists(paths.SettingsPath + ".tmp"));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.SettingsPath));
        Assert.Equal("Ctrl+Shift+F9", json.RootElement.GetProperty("translateHotkey").GetString());
        Assert.Equal("Alt+Win+O", json.RootElement.GetProperty("toggleOverlayHotkey").GetString());
        Assert.Equal("F10", json.RootElement.GetProperty("liveToggleHotkey").GetString());
    }

    [Fact]
    public void Zmiana_skrotow_nie_wymaga_przebudowy_pipeline()
    {
        var settings = new AppSettings();
        var before = settings.PipelineSnapshot();

        settings.TranslateHotkey = "Ctrl+Alt+K";
        settings.ToggleOverlayHotkey = "F11";
        settings.LiveToggleHotkey = "Ctrl+Alt+L";

        Assert.Equal(before, settings.PipelineSnapshot());
    }

    [Fact]
    public void Zapis_nie_zmienia_innych_ustawien_przy_zmianie_skrotu()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);
        store.Save(new AppSettings { Provider = "Mock", PrivateMode = true, ActiveProfileId = "mroczna-latarnia" });

        var settings = store.Load();
        settings.LiveToggleHotkey = "Ctrl+Alt+L";
        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal("Mock", loaded.Provider);
        Assert.True(loaded.PrivateMode);
        Assert.Equal("mroczna-latarnia", loaded.ActiveProfileId);
        Assert.Equal("Ctrl+Alt+L", loaded.LiveToggleHotkey);
        Assert.Equal(HotkeyDefaults.Translate, loaded.TranslateHotkey);
    }
}
