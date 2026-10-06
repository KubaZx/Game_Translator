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
    public bool? Online { get; set; }
    public CorpusRecipe? Corpus { get; set; }
}

public sealed class OcrProfileSettings
{
    /// <summary>
    /// Powiększenie przed OCR: brak = ustawienia aplikacji (automatyka), 1.0 = bez
    /// powiększania, powyżej 1.0 = stały współczynnik (do 4.0).
    /// </summary>
    public double? Upscale { get; set; }
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
    /// <summary>
    /// Sprawdza profil. Gdy podano <paramref name="appVersion"/>, profil wymagający nowszej
    /// aplikacji (pole „minAppVersion”) jest błędny — może używać ustawień, których ta wersja nie zna.
    /// </summary>
    public static IReadOnlyList<string> Validate(GameProfile profile, Version? appVersion = null)
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
        if (profile.Ocr is { Upscale: { } upscale } && (upscale < 1.0 || upscale > 4.0))
        {
            errors.Add("Wartość ocr.upscale musi mieścić się w zakresie 1.0–4.0.");
        }
        if (profile.ChangeDetection is { } cd && (cd.Fps <= 0 || cd.Fps > 30))
        {
            errors.Add("Wartość changeDetection.fps musi mieścić się w zakresie 0–30.");
        }
        if (profile.Corpus is { } corpus)
        {
            errors.AddRange(CorpusRecipeValidator.Validate(corpus));
        }

        if (!string.IsNullOrWhiteSpace(profile.MinAppVersion))
        {
            if (!TryParseVersion(profile.MinAppVersion, out var required))
            {
                errors.Add($"Pole „minAppVersion” („{profile.MinAppVersion}”) nie jest numerem wersji (np. 0.2.2).");
            }
            else if (appVersion is not null && Normalize(appVersion) < required)
            {
                errors.Add($"Profil wymaga nowszej wersji aplikacji ({required.ToString(3)} lub nowszej); " +
                           $"obecna wersja: {Normalize(appVersion).ToString(3)}.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Czyta wersję SemVer („0.2.2”, „v0.3”, „1.0.0-beta.1”) jako major.minor.patch;
    /// przyrostki przedpremierowe i metadane są pomijane.
    /// </summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0) core = core[..cut];
        if (!core.Contains('.')) core += ".0";

        if (Version.TryParse(core, out var parsed) && parsed.Revision <= 0)
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));
}
