using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslatorOverlay.Core.Profiles;

public sealed class GameProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ProfileVersion { get; set; } = 1;
    public string? Author { get; set; }
    public string? Description { get; set; }
    public List<string> ProcessNames { get; set; } = [];
    public List<string> WindowTitles { get; set; } = [];
    public string SourceLanguage { get; set; } = "en";
    public string? RecommendedMode { get; set; }
    public string? Glossary { get; set; }
    public OcrProfileSettings? Ocr { get; set; }
    public ChangeDetectionProfileSettings? ChangeDetection { get; set; }
    public string? MinAppVersion { get; set; }
}

public sealed class OcrProfileSettings
{
    public double Upscale { get; set; } = 1.0;
    public int MinTextHeight { get; set; } = 8;
}

public sealed class ChangeDetectionProfileSettings
{
    public double Threshold { get; set; } = 0.02;
    public double Fps { get; set; } = 4;
}

public static class ProfileSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static GameProfile FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<GameProfile>(json, JsonOptions);
        return profile ?? throw new FormatException("Plik profilu jest pusty albo ma niepoprawny format JSON.");
    }

    public static string ToJson(GameProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);
}

public static class ProfileValidator
{
    public static IReadOnlyList<string> Validate(GameProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            errors.Add("Profil musi mieć identyfikator (pole „id”).");
        }
        else if (profile.Id.Any(static ch => char.IsWhiteSpace(ch) || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            errors.Add($"Identyfikator profilu „{profile.Id}” zawiera niedozwolone znaki (dozwolone: litery, cyfry, myślniki).");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Profil musi mieć nazwę (pole „name”).");
        }
        if (profile.ProfileVersion < 1)
        {
            errors.Add("Wersja profilu musi być liczbą całkowitą ≥ 1.");
        }
        if (string.IsNullOrWhiteSpace(profile.SourceLanguage))
        {
            errors.Add("Profil musi wskazywać język źródłowy (pole „sourceLanguage”).");
        }
        if (profile.Ocr is { } ocr && (ocr.Upscale < 1.0 || ocr.Upscale > 4.0))
        {
            errors.Add("Wartość ocr.upscale musi mieścić się w zakresie 1.0–4.0.");
        }
        if (profile.ChangeDetection is { } cd && (cd.Fps <= 0 || cd.Fps > 30))
        {
            errors.Add("Wartość changeDetection.fps musi mieścić się w zakresie 0–30.");
        }

        return errors;
    }
}
