using System.Text.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Settings;

public sealed class AppSettings
{
    public const int CurrentSettingsVersion = 1;

    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    [JsonIgnore]
    public string? PersistedActiveProfileId { get; set; }

    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "pl";
    public string Provider { get; set; } = "DeepL";

    /// <summary>Region zasobu Azure Translator (np. westeurope); puste = zasób globalny.</summary>
    public string? AzureRegion { get; set; }

    /// <summary>Adres bazowy serwera zgodnego z API OpenAI (np. http://localhost:11434/v1).</summary>
    public string LlmEndpoint { get; set; } = Providers.LlmEndpoint.OpenAiDefault;

    /// <summary>Nazwa modelu na serwerze LLM (np. nazwa modelu z Ollamy).</summary>
    public string? LlmModel { get; set; }

    /// <summary>
    /// Serwer (host[:port]), dla którego zapisano klucz LLM. Klucz nie jest wysyłany
    /// pod inny adres — zmiana serwera wymaga ponownego zapisania klucza.
    /// </summary>
    public string? LlmKeyHost { get; set; }

    public Providers.LlmServerOptions? LlmServerOptions { get; set; }

    /// <summary>Model Claude (Anthropic).</summary>
    public string ClaudeModel { get; set; } = Providers.ClaudeTranslationProvider.DefaultModel;
    public string TranslateHotkey { get; set; } = Core.Windows.HotkeyDefaults.Translate;
    public string ToggleOverlayHotkey { get; set; } = Core.Windows.HotkeyDefaults.ToggleOverlay;

    /// <summary>
    /// Globalny skrót start/stop trybu live na aktywnej grze — bez Alt+Tab do okna tłumacza.
    /// Brak pola w starym pliku = Ctrl+Shift+L.
    /// </summary>
    public string LiveToggleHotkey { get; set; } = Core.Windows.HotkeyDefaults.LiveToggle;

    /// <summary>
    /// Proces gry (np. „latarnia.exe”) z ostatniego startu live — skrót wybiera go, gdy na
    /// pierwszym planie jest okno tłumacza, a lista okien zaznacza go po odświeżeniu.
    /// Nie jest zapisywany w trybie prywatnym.
    /// </summary>
    public string? LastGameProcess { get; set; }

    /// <summary>Tytuł okna z ostatniego startu live — rozstrzyga, gdy gra ma kilka okien.</summary>
    public string? LastGameTitle { get; set; }
    public bool CacheOnlyMode { get; set; }
    public bool PrivateMode { get; set; }

    /// <summary>Czy użytkownik zobaczył już obowiązkowe zastrzeżenie (SECURITY.md) przy pierwszym starcie.</summary>
    public bool DisclaimerAcknowledged { get; set; }

    public long? SessionCharacterLimit { get; set; }

    /// <summary>panel | overlay</summary>
    public string ResultDisplayMode { get; set; } = "panel";

    /// <summary>0 = auto: rozmiar dopasowany do wysokości oryginalnego tekstu z OCR.</summary>
    public double OverlayFontSize { get; set; }

    /// <summary>Krój czcionki nakładki (nazwa czcionki systemowej).</summary>
    public string OverlayFontFamily { get; set; } = AutoFontFamily;

    public const string AutoFontFamily = "auto";

    public int OverlayFontRevision { get; set; } = CurrentOverlayFontRevision;

    public const int CurrentOverlayFontRevision = 1;

    public void MigrateOverlayFont()
    {
        if (OverlayFontRevision >= CurrentOverlayFontRevision) return;
        if (string.Equals(OverlayFontFamily, "Segoe UI", StringComparison.OrdinalIgnoreCase)) OverlayFontFamily = AutoFontFamily;
        OverlayFontRevision = CurrentOverlayFontRevision;
    }

    public double OverlayBackgroundOpacity { get; set; } = 0.85;
    public int ResultAutoHideSeconds { get; set; } = 30;

    /// <summary>at-source (przy oryginale) | subtitle (napisy na dole).</summary>
    public string LiveDisplayMode { get; set; } = "at-source";
    public int SubtitleSeconds { get; set; } = 8;

    /// <summary>below (dymek pod oryginałem) | cover (dymek zakrywa oryginalny tekst).</summary>
    public string OverlayPlacement { get; set; } = "below";
    public string? ActiveProfileId { get; set; }

    /// <summary>0 = automatyczny dobór powiększenia obrazu przed OCR.</summary>
    public double OcrUpscale { get; set; }

    /// <summary>
    /// Krótkie komunikaty w nakładce nad grą (brak klucza, limit, brak sieci, Cache-only,
    /// start/stop live). Domyślnie włączone — w trakcie gry okno aplikacji jest schowane,
    /// więc bez nich błędy są niewidoczne. Brak pola w starym pliku = włączone.
    /// </summary>
    public bool ShowOverlayNotices { get; set; } = true;

    public bool ShowQualityMarks { get; set; } = true;

    public bool ShowsQualityMark(Core.Translation.TranslationQualityFlags issues, bool subtitleBar = false) =>
        ShowsQualityMark(issues != Core.Translation.TranslationQualityFlags.None, subtitleBar);

    public bool ShowsQualityMark(bool warning, bool subtitleBar = false) =>
        ShowQualityMarks && warning
        && (subtitleBar || !OverlayPlacement.Equals("cover", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Płeć postaci gracza: unknown | male | female. Tylko dostawcy LLM (Claude, zgodny z OpenAI)
    /// odmieniają według niej zwroty do gracza; brak pola w starym pliku = unknown.
    /// </summary>
    public string PlayerGender { get; set; } = Core.Translation.PlayerGenders.UnknownSetting;

    public bool ParagraphCacheKeys { get; set; }

    public bool LiveGraphicsCapture { get; set; } = true;

    public bool StreamLlmResponses { get; set; } = true;

    /// <summary>
    /// Pola, które nie wpływają na pipeline tłumaczenia: wygląd nakładki i panelu, skróty
    /// (rejestrowane przy starcie), zapamiętana gra, komunikaty, zastrzeżenie. Nakładka czyta
    /// je na bieżąco, więc ich zmiana nie może przebudowywać pipeline'u — przebudowa czyści
    /// pamięć dialogu i anuluje płatne tłumaczenia będące w locie.
    /// </summary>
    private static readonly string[] PresentationOnly =
    [
        nameof(ResultDisplayMode), nameof(LiveDisplayMode), nameof(OverlayPlacement),
        nameof(OverlayBackgroundOpacity), nameof(OverlayFontSize), nameof(OverlayFontFamily), nameof(OverlayFontRevision),
        nameof(ResultAutoHideSeconds), nameof(SubtitleSeconds), nameof(ShowOverlayNotices), nameof(ShowQualityMarks),
        nameof(TranslateHotkey), nameof(ToggleOverlayHotkey), nameof(LiveToggleHotkey),
        nameof(LastGameProcess), nameof(LastGameTitle), nameof(DisclaimerAcknowledged), nameof(StreamLlmResponses),
        nameof(SettingsVersion),
    ];

    /// <summary>
    /// Migawka tylko tych ustawień, które wymagają przebudowy pipeline'u (dostawca, języki,
    /// profil, tryby, limity, płeć gracza, ustawienia dostawców…). Nowe pole trafia tu
    /// domyślnie — bezpieczniej przebudować o raz za dużo niż nie przebudować wcale.
    /// </summary>
    public string PipelineSnapshot()
    {
        var node = JsonSerializer.SerializeToNode(this)!.AsObject();
        foreach (var name in PresentationOnly) node.Remove(name);
        return node.ToJsonString();
    }
}
