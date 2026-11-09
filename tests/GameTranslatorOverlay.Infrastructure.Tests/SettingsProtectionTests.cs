using System.Text.Json.Nodes;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class SettingsProtectionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AppPaths Paths()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        return paths;
    }

    private static JsonSettingsStore Store(AppPaths paths) => new(paths, null, readAttempts: 2, retryDelayMs: 0);

    private string[] Backups() => Directory.GetFiles(_temp.Path, "settings.json.corrupt-*.bak");

    private static AppSettings NonDefaultSettings() => new()
    {
        SourceLanguage = "en",
        TargetLanguage = "pl",
        Provider = "Mock",
        AzureRegion = "westeurope",
        LlmEndpoint = "http://localhost:11434/v1",
        LlmModel = "model-testowy",
        ClaudeModel = "model-claude",
        TranslateHotkey = "Ctrl+Alt+K",
        ToggleOverlayHotkey = "F11",
        LiveToggleHotkey = "Ctrl+Alt+L",
        LastGameProcess = "gra.exe",
        LastGameTitle = "Gra testowa",
        CacheOnlyMode = true,
        PrivateMode = false,
        DisclaimerAcknowledged = true,
        SessionCharacterLimit = 5000,
        ResultDisplayMode = "overlay",
        OverlayFontSize = 20,
        OverlayFontFamily = "Georgia",
        OverlayBackgroundOpacity = 0.4,
        ResultAutoHideSeconds = 12,
        LiveDisplayMode = "subtitle",
        SubtitleSeconds = 5,
        OverlayPlacement = "cover",
        ActiveProfileId = "profil-testowy",
        OcrUpscale = 2,
        ShowOverlayNotices = false,
        ShowQualityMarks = false,
        PlayerGender = "female",
        ParagraphCacheKeys = true,
        LiveGraphicsCapture = false,
        StreamLlmResponses = false,
    };

    private void WriteSettingsJson(Action<JsonObject> change)
    {
        var paths = Paths();
        Store(paths).Save(NonDefaultSettings());
        var node = JsonNode.Parse(File.ReadAllText(paths.SettingsPath))!.AsObject();
        change(node);
        File.WriteAllText(paths.SettingsPath, node.ToJsonString());
    }

    [Fact]
    public void Plik_z_wersji_0_9_0_bez_pola_wersji_wczytuje_sie_bez_zmian()
    {
        WriteSettingsJson(node => node.Remove("settingsVersion"));

        var store = Store(new AppPaths(_temp.Path));
        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.Ok, result.Status);
        Assert.Empty(result.RepairedFields);
        Assert.False(store.SavingBlocked);
        Assert.Equal(NonDefaultSettings().PipelineSnapshot(), result.Settings.PipelineSnapshot());
        Assert.Equal("Ctrl+Alt+L", result.Settings.LiveToggleHotkey);
        Assert.Equal("Gra testowa", result.Settings.LastGameTitle);
        Assert.Equal(5000, result.Settings.SessionCharacterLimit);
        Assert.Equal(AppSettings.CurrentSettingsVersion, result.Settings.SettingsVersion);
    }

    [Fact]
    public void Brak_pliku_daje_status_brak_pliku_i_domyslne_ustawienia()
    {
        var result = Store(new AppPaths(_temp.Path)).LoadWithReport();

        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
        Assert.False(result.Settings.PrivateMode);
        Assert.Equal("DeepL", result.Settings.Provider);
    }

    [Fact]
    public void Pole_null_dostaje_wartosc_domyslna_a_reszta_zostaje()
    {
        WriteSettingsJson(node => node["provider"] = null);

        var store = Store(new AppPaths(_temp.Path));
        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.FieldsRepaired, result.Status);
        Assert.Equal(["provider"], result.RepairedFields);
        Assert.Equal("DeepL", result.Settings.Provider);
        Assert.True(result.Settings.CacheOnlyMode);
        Assert.Equal("Ctrl+Alt+L", result.Settings.LiveToggleHotkey);
        Assert.False(store.SavingBlocked);
        Assert.Empty(Backups());
    }

    [Fact]
    public void Pole_ze_zlym_typem_albo_wartoscia_wraca_do_domyslnego_a_reszta_zostaje()
    {
        WriteSettingsJson(node =>
        {
            node["privateMode"] = "tak";
            node["sessionCharacterLimit"] = "dużo";
            node["overlayBackgroundOpacity"] = 7;
            node["resultDisplayMode"] = "okienko";
        });

        var result = Store(new AppPaths(_temp.Path)).LoadWithReport();

        Assert.Equal(SettingsLoadStatus.FieldsRepaired, result.Status);
        Assert.Equal(
            ["overlayBackgroundOpacity", "privateMode", "resultDisplayMode", "sessionCharacterLimit"],
            result.RepairedFields.Order(StringComparer.Ordinal).ToArray());
        Assert.False(result.Settings.PrivateMode);
        Assert.Null(result.Settings.SessionCharacterLimit);
        Assert.Equal(0.85, result.Settings.OverlayBackgroundOpacity);
        Assert.Equal("panel", result.Settings.ResultDisplayMode);
        Assert.Equal("Mock", result.Settings.Provider);
        Assert.True(result.Settings.CacheOnlyMode);
    }

    [Fact]
    public void Plik_ucieety_startuje_prywatnie_z_kopia_i_blokuje_zapis()
    {
        var paths = Paths();
        const string truncated = """{"provider":"Mock","privateMode":tr""";
        File.WriteAllText(paths.SettingsPath, truncated);
        var store = Store(paths);

        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.Unreadable, result.Status);
        Assert.True(result.Settings.PrivateMode);
        Assert.True(result.Settings.CacheOnlyMode);
        Assert.True(store.SavingBlocked);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(truncated, File.ReadAllText(result.BackupPath!));

        Assert.False(store.Save(result.Settings));
        Assert.Equal(truncated, File.ReadAllText(paths.SettingsPath));
    }

    [Fact]
    public void Kolejne_kopie_uszkodzonego_pliku_nie_nadpisuja_poprzednich()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsPath, "{pierwszy");
        var first = Store(paths).LoadWithReport().BackupPath;
        File.WriteAllText(paths.SettingsPath, "{drugi");
        var second = Store(paths).LoadWithReport().BackupPath;

        Assert.NotEqual(first, second);
        Assert.Equal(2, Backups().Length);
        Assert.Equal("{pierwszy", File.ReadAllText(first!));
        Assert.Equal("{drugi", File.ReadAllText(second!));
    }

    [Fact]
    public void Plik_bedacy_tablica_jest_nieczytelny()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsPath, "[1, 2, 3]");

        var result = Store(paths).LoadWithReport();

        Assert.Equal(SettingsLoadStatus.Unreadable, result.Status);
        Assert.True(result.Settings.PrivateMode);
    }

    [Fact]
    public void Wybor_ustawien_domyslnych_pozwala_znow_zapisywac()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsPath, "{zepsute");
        var store = Store(paths);
        var result = store.LoadWithReport();

        store.AllowSaving();

        Assert.True(store.Save(result.Settings));
        Assert.Equal(SettingsLoadStatus.Ok, Store(paths).LoadWithReport().Status);
    }

    [Fact]
    public void Zablokowany_plik_daje_status_niedostepny_bez_kopii_i_bez_zapisu()
    {
        var paths = Paths();
        Store(paths).Save(NonDefaultSettings());
        var original = File.ReadAllText(paths.SettingsPath);
        var store = Store(paths);

        SettingsLoadResult result;
        using (new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = store.LoadWithReport();
        }

        Assert.Equal(SettingsLoadStatus.Unavailable, result.Status);
        Assert.True(result.Settings.PrivateMode);
        Assert.True(result.Settings.CacheOnlyMode);
        Assert.True(store.SavingBlocked);
        Assert.Null(result.BackupPath);
        Assert.Empty(Backups());
        Assert.False(store.Save(result.Settings));
        Assert.Equal(original, File.ReadAllText(paths.SettingsPath));
    }

    [Fact]
    public void Nieznane_pola_z_nowszego_wydania_wracaja_do_pliku_przy_zapisie()
    {
        WriteSettingsJson(node =>
        {
            node["polePrzyszlosci"] = new JsonObject { ["a"] = 1, ["b"] = "x" };
            node["innePole"] = true;
        });
        var paths = new AppPaths(_temp.Path);
        var store = Store(paths);
        var settings = store.Load();

        settings.LiveToggleHotkey = "Ctrl+Alt+M";
        Assert.True(store.Save(settings));

        var saved = JsonNode.Parse(File.ReadAllText(paths.SettingsPath))!.AsObject();
        Assert.Equal(1, (int)saved["polePrzyszlosci"]!["a"]!);
        Assert.Equal("x", (string)saved["polePrzyszlosci"]!["b"]!);
        Assert.True((bool)saved["innePole"]!);
        Assert.Equal("Ctrl+Alt+M", (string)saved["liveToggleHotkey"]!);
        Assert.Equal(AppSettings.CurrentSettingsVersion, (int)saved["settingsVersion"]!);
    }

    [Fact]
    public void Plik_z_nowsza_wersja_nie_jest_zapisywany()
    {
        WriteSettingsJson(node => node["settingsVersion"] = AppSettings.CurrentSettingsVersion + 1);
        var paths = new AppPaths(_temp.Path);
        var original = File.ReadAllText(paths.SettingsPath);
        var store = Store(paths);

        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.NewerVersion, result.Status);
        Assert.True(store.SavingBlocked);
        Assert.Equal("Mock", result.Settings.Provider);
        Assert.Empty(Backups());
        Assert.False(store.Save(result.Settings));
        Assert.Equal(original, File.ReadAllText(paths.SettingsPath));
    }

    [Fact]
    public void Tryb_prywatny_nie_zapisuje_wyboru_profilu_a_po_wyjsciu_wraca_zapisany()
    {
        var paths = Paths();
        var store = Store(paths);
        store.Save(new AppSettings { ActiveProfileId = "stary-profil" });

        var settings = store.Load();
        Assert.Equal("stary-profil", settings.PersistedActiveProfileId);
        settings.PrivateMode = true;
        settings.ActiveProfileId = "nowy-profil";
        store.Save(settings);

        var reloaded = Store(paths).Load();
        Assert.True(reloaded.PrivateMode);
        Assert.Equal("stary-profil", reloaded.ActiveProfileId);
        Assert.DoesNotContain("nowy-profil", File.ReadAllText(paths.SettingsPath));

        settings.PrivateMode = false;
        store.Save(settings);
        Assert.Equal("nowy-profil", Store(paths).Load().ActiveProfileId);
    }

    [Fact]
    public void Tryb_prywatny_bez_zapisanego_profilu_nie_dopisuje_profilu_do_pliku()
    {
        var paths = Paths();
        var store = Store(paths);
        var settings = new AppSettings { PrivateMode = true, ActiveProfileId = "prywatny-profil" };

        store.Save(settings);

        Assert.DoesNotContain("prywatny-profil", File.ReadAllText(paths.SettingsPath));
        Assert.Null(Store(paths).Load().ActiveProfileId);
    }

    [Fact]
    public void Komunikaty_dla_gracza_zawieraja_kopie_i_oba_wybory()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsPath, "{zepsute");
        var result = Store(paths).LoadWithReport();

        var notice = SettingsLoadMessages.Describe(result);

        Assert.NotNull(notice);
        Assert.Contains(result.BackupPath!, notice!.Message);
        Assert.Contains("tryb", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Zacznij od ustawień domyślnych", notice.DefaultsButton);
        Assert.Equal("Zostaw plik — poprawię go sam", notice.KeepButton);
        Assert.Null(SettingsLoadMessages.Describe(new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Ok, [], null)));
    }
}
