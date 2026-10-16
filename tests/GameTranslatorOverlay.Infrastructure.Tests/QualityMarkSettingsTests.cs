using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class QualityMarkSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gto-quality-mark-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Znacznik_ostrzezen_jest_domyslnie_wlaczony()
    {
        Assert.True(new AppSettings().ShowQualityMarks);
    }

    [Fact]
    public void Wylaczenie_znacznika_przetrwa_zapis_i_odczyt()
    {
        var store = new JsonSettingsStore(new AppPaths(_root));
        store.Save(new AppSettings { ShowQualityMarks = false, OverlayPlacement = "below" });

        var loaded = store.Load();

        Assert.False(loaded.ShowQualityMarks);
        Assert.Equal("below", loaded.OverlayPlacement);
        Assert.Contains("\"showQualityMarks\": false", File.ReadAllText(new AppPaths(_root).SettingsPath));
    }

    [Fact]
    public void Stary_plik_ustawien_bez_pola_wlacza_znacznik()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """
            {
              "sourceLanguage": "en",
              "targetLanguage": "pl",
              "provider": "Mock",
              "overlayPlacement": "cover",
              "showOverlayNotices": false
            }
            """);

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.True(loaded.ShowQualityMarks);
        Assert.False(loaded.ShowOverlayNotices);
        Assert.Equal("cover", loaded.OverlayPlacement);
        Assert.False(File.Exists(paths.SettingsPath + ".corrupt.bak"));
    }

    [Theory]
    [InlineData("below", true, false, true)]
    [InlineData("below", true, true, true)]
    [InlineData("cover", true, false, false)]
    [InlineData("COVER", true, false, false)]
    [InlineData("cover", true, true, true)]
    [InlineData("below", false, false, false)]
    [InlineData("cover", false, true, false)]
    public void Znacznik_nigdy_przy_zakrywaniu_oryginalu_a_pasek_napisow_go_dostaje(
        string placement, bool enabled, bool subtitleBar, bool expected)
    {
        var settings = new AppSettings { OverlayPlacement = placement, ShowQualityMarks = enabled };

        Assert.Equal(expected, settings.ShowsQualityMark(TranslationQualityFlags.NumbersChanged, subtitleBar));
        Assert.Equal(expected, settings.ShowsQualityMark(warning: true, subtitleBar));
        Assert.False(settings.ShowsQualityMark(TranslationQualityFlags.None, subtitleBar));
        Assert.False(settings.ShowsQualityMark(warning: false, subtitleBar));
    }
}
