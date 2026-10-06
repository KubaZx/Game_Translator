using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public sealed record LocalAppSettings(
    string SettingsPath,
    bool Exists,
    bool PrivateMode,
    string SourceLanguage,
    string TargetLanguage,
    PlayerGender PlayerGender)
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LocalAppSettings Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            return new LocalAppSettings(path, false, defaults.PrivateMode, defaults.SourceLanguage, defaults.TargetLanguage,
                PlayerGenders.Parse(defaults.PlayerGender));
        }

        AppSettings settings;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            settings = JsonSerializer.Deserialize<AppSettings>(reader.ReadToEnd(), JsonOptions)
                ?? throw new JsonException("Pusty plik ustawień.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new RefusedException(
                $"Nie da się odczytać ustawień aplikacji ({path}): {ex.Message} Bez nich nie wiadomo, czy tryb prywatny jest wyłączony.");
        }

        return new LocalAppSettings(
            path,
            true,
            settings.PrivateMode,
            string.IsNullOrWhiteSpace(settings.SourceLanguage) ? "en" : settings.SourceLanguage.Trim(),
            string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "pl" : settings.TargetLanguage.Trim(),
            PlayerGenders.Parse(settings.PlayerGender));
    }
}
