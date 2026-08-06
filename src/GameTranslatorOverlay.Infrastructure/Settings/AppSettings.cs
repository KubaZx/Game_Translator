using System.Text.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Settings;

public sealed class AppSettings
{
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "pl";
    public string Provider { get; set; } = "DeepL";
    public string TranslateHotkey { get; set; } = "Ctrl+Shift+T";
    public string ToggleOverlayHotkey { get; set; } = "Ctrl+Shift+H";
    public bool CacheOnlyMode { get; set; }
    public bool PrivateMode { get; set; }
    public long? SessionCharacterLimit { get; set; }

    /// <summary>panel | overlay</summary>
    public string ResultDisplayMode { get; set; } = "panel";
    public double OverlayFontSize { get; set; } = 15;
    public double OverlayBackgroundOpacity { get; set; } = 0.85;
    public int ResultAutoHideSeconds { get; set; } = 30;

    /// <summary>at-source (przy oryginale) | subtitle (napisy na dole).</summary>
    public string LiveDisplayMode { get; set; } = "at-source";
    public int SubtitleSeconds { get; set; } = 8;
    public string? ActiveProfileId { get; set; }

    /// <summary>0 = automatyczny dobór powiększenia obrazu przed OCR.</summary>
    public double OcrUpscale { get; set; }
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
        File.WriteAllText(paths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
