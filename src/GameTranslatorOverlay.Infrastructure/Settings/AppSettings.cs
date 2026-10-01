using System.Text.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Settings;

public sealed class AppSettings
{
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

    /// <summary>Model Claude (Anthropic).</summary>
    public string ClaudeModel { get; set; } = Providers.ClaudeTranslationProvider.DefaultModel;
    public string TranslateHotkey { get; set; } = "Ctrl+Shift+T";
    public string ToggleOverlayHotkey { get; set; } = "Ctrl+Shift+H";

    /// <summary>
    /// Globalny skrót start/stop trybu live na aktywnej grze — bez Alt+Tab do okna tłumacza.
    /// Brak pola w starym pliku = Ctrl+Shift+L.
    /// </summary>
    public string LiveToggleHotkey { get; set; } = "Ctrl+Shift+L";

    /// <summary>
    /// Proces gry (np. „witcher3.exe”) z ostatniego startu live — skrót wybiera go, gdy na
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
    public string OverlayFontFamily { get; set; } = "Segoe UI";

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

    /// <summary>
    /// Płeć postaci gracza: unknown | male | female. Tylko dostawcy LLM (Claude, zgodny z OpenAI)
    /// odmieniają według niej zwroty do gracza; brak pola w starym pliku = unknown.
    /// </summary>
    public string PlayerGender { get; set; } = Core.Translation.PlayerGenders.UnknownSetting;
}

public sealed class JsonSettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public AppSettings Load()
    {
        if (!File.Exists(paths.SettingsPath)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(paths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Uszkodzony plik ustawień nie może blokować startu — odkładamy kopię i wracamy do domyślnych.
            try
            {
                File.Copy(paths.SettingsPath, paths.SettingsPath + ".corrupt.bak", overwrite: true);
            }
            catch (IOException)
            {
                // Kopia zapasowa jest tylko ułatwieniem diagnostyki.
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        paths.EnsureCreated();
        // Zapis przez plik tymczasowy + atomowa podmiana: przerwanie w trakcie (crash,
        // zanik zasilania) nie może zostawić uciętego JSON-a, który przy starcie po cichu
        // resetuje wszystkie ustawienia do domyślnych.
        var temp = paths.SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, paths.SettingsPath, overwrite: true);
    }
}
