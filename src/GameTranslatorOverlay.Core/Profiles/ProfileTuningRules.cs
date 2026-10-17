using System.Globalization;
using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Profiles;

public static class ProfileTuningRules
{
    public const double MinFps = 1;
    public const double MaxFps = 30;
    public const double MinThreshold = 0;
    public const double MaxThreshold = 0.5;
    public const double MinUpscale = 1.0;
    public const double MaxUpscale = 4.0;
    public const int MinTextHeightLow = 4;
    public const int MinTextHeightHigh = 64;
    public const int MaxListItems = 16;
    public const int MaxProcessNameLength = 128;
    public const int MaxWindowTitleLength = 200;
    public const int MaxLanguageLength = 16;
    public const int MaxGlossaryIdLength = 64;
    public const int MaxDescriptionLength = 2000;
    public const int MaxIgnoreRegions = 16;
    public const string ManualRegionMode = "manual-region";
    public const string LiveMode = "live";

    public static IReadOnlyList<string> RecommendedModes { get; } = [ManualRegionMode, LiveMode];

    public static IReadOnlyList<string> Validate(ProfileTuning before, ProfileTuning after, IEnumerable<string> otherProfileNames)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(otherProfileNames);
        var errors = new List<string>();

        if (after.Name != before.Name && ProfileNames.Validate(after.Name, otherProfileNames) is { } nameError) errors.Add(nameError);

        if (!after.ProcessNames.SequenceEqual(before.ProcessNames, StringComparer.Ordinal)) ValidateProcesses(after.ProcessNames, errors);
        if (!after.WindowTitles.SequenceEqual(before.WindowTitles, StringComparer.Ordinal)) ValidateTitles(after.WindowTitles, errors);

        if (after.SourceLanguage != before.SourceLanguage && !IsLanguage(after.SourceLanguage))
            errors.Add("Język gry musi być kodem języka (np. en).");

        if (after.Glossary != before.Glossary && after.Glossary is { } glossary && !IsContentId(glossary))
            errors.Add("Słownik profilu musi być nazwą folderu słownika (litery, cyfry, myślniki).");

        if (after.OcrUpscale != before.OcrUpscale && after.OcrUpscale is { } upscale && !InRange(upscale, MinUpscale, MaxUpscale))
            errors.Add("Powiększenie przed OCR musi mieścić się w zakresie 1–4×.");

        if (after.OcrMinTextHeight != before.OcrMinTextHeight && after.OcrMinTextHeight is { } height
            && (height < MinTextHeightLow || height > MinTextHeightHigh))
            errors.Add($"Minimalna wysokość tekstu musi mieścić się w zakresie {MinTextHeightLow}–{MinTextHeightHigh} px.");

        if (after.Fps != before.Fps && after.Fps is { } fps && !InRange(fps, MinFps, MaxFps))
            errors.Add($"Liczba analiz na sekundę musi mieścić się w zakresie {MinFps:0}–{MaxFps:0}.");

        if (after.ChangeThreshold != before.ChangeThreshold && after.ChangeThreshold is { } threshold && !InRange(threshold, MinThreshold, MaxThreshold))
            errors.Add($"Próg zmiany obrazu musi mieścić się w zakresie {ProfileNumbers.Format(MinThreshold)}–{ProfileNumbers.Format(MaxThreshold)}.");

        if (after.FontFamily != before.FontFamily && after.FontFamily is { } font && !OverlayFontRuleSet.IsValidFamilyName(font))
            errors.Add("Krój nakładki musi być nazwą kroju (do 64 znaków, bez ścieżki).");

        if (after.RecommendedMode != before.RecommendedMode && after.RecommendedMode is { } mode && !RecommendedModes.Contains(mode))
            errors.Add("Zalecany tryb musi być ręcznym zaznaczeniem regionu albo trybem live.");

        if (after.Description != before.Description && after.Description is { } description)
        {
            if (description.Length > MaxDescriptionLength) errors.Add($"Opis może mieć najwyżej {MaxDescriptionLength} znaków.");
            else if (description.Any(char.IsControl)) errors.Add("Opis nie może zawierać znaków sterujących ani nowych wierszy.");
        }

        if (!ProfileTuningJson.SameRegions(after.IgnoreRegions, before.IgnoreRegions))
        {
            if (after.IgnoreRegions.Count > MaxIgnoreRegions) errors.Add($"Profil może pomijać najwyżej {MaxIgnoreRegions} obszarów.");
            for (var i = 0; i < after.IgnoreRegions.Count; i++)
            {
                if (!IsValidRegion(after.IgnoreRegions[i]))
                    errors.Add($"Obszar {i + 1}: X, Y, szerokość i wysokość to ułamki okna 0–1, wymiary dodatnie, a obszar mieści się w oknie.");
            }
        }

        return errors;
    }

    public static string? PrivateModeViolation(ProfileTuning before, ProfileTuning after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var identityChanged = after.Name != before.Name
            || !after.ProcessNames.SequenceEqual(before.ProcessNames, StringComparer.Ordinal)
            || !after.WindowTitles.SequenceEqual(before.WindowTitles, StringComparer.Ordinal)
            || after.Description != before.Description;
        return identityChanged
            ? "Tryb prywatny: nazwy profilu, procesów, tytułów okien ani opisu nie zapiszę — zdradziłyby na dysku, w co grasz. OCR, krój, słownik i wykrywanie zmian możesz zmienić."
            : null;
    }

    public static bool IsValidRegion(RelativeRegion? region) =>
        region is not null
        && double.IsFinite(region.X) && double.IsFinite(region.Y) && double.IsFinite(region.Width) && double.IsFinite(region.Height)
        && region.IsValid;

    public static bool IsContentId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxGlossaryIdLength
        && id is not ("." or "..")
        && id.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    private static void ValidateProcesses(IReadOnlyList<string> processes, List<string> errors)
    {
        if (processes.Count > MaxListItems) errors.Add($"Profil może mieć najwyżej {MaxListItems} nazw procesów.");
        var seen = new List<string>();
        foreach (var process in processes)
        {
            var normalized = LiveTargetResolver.NormalizeProcessName(process);
            if (normalized.Length == 0 || process.Length > MaxProcessNameLength
                || process.Any(static ch => char.IsControl(ch) || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            {
                errors.Add($"„{process}” nie jest nazwą pliku procesu (np. Gra.exe).");
                continue;
            }
            if (seen.Any(other => LiveTargetResolver.SameProcess(other, process)))
            {
                errors.Add($"Proces „{process}” jest już na liście.");
                continue;
            }
            seen.Add(process);
        }
    }

    private static void ValidateTitles(IReadOnlyList<string> titles, List<string> errors)
    {
        if (titles.Count > MaxListItems) errors.Add($"Profil może mieć najwyżej {MaxListItems} tytułów okien.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in titles)
        {
            var clean = ProfileNames.Clean(title);
            if (clean.Length == 0 || title.Length > MaxWindowTitleLength || title.Any(char.IsControl))
            {
                errors.Add($"Tytuł okna może mieć od 1 do {MaxWindowTitleLength} znaków, bez znaków sterujących.");
                continue;
            }
            if (!seen.Add(clean)) errors.Add($"Tytuł „{clean}” jest już na liście.");
        }
    }

    private static bool IsLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language)
        && language.Length <= MaxLanguageLength
        && language.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

public static class ProfileNumbers
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static string Format(double? value) => value is { } number ? number.ToString("0.###############", Polish) : string.Empty;

    public static bool TryParse(string? text, out double? value)
    {
        value = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return true;
        if (trimmed.Any(static ch => !(char.IsAsciiDigit(ch) || ch is ',' or '.' or '-' or '+'))) return false;
        if (!double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)) return false;
        value = parsed;
        return true;
    }

    public static bool TryParseInteger(string? text, out int? value)
    {
        value = null;
        if (!TryParse(text, out var number)) return false;
        if (number is not { } whole) return true;
        if (whole != Math.Floor(whole) || whole < int.MinValue || whole > int.MaxValue) return false;
        value = (int)whole;
        return true;
    }
}

public sealed record ProfileSummaryRow(string Label, string Value);

public static class ProfileSummary
{
    public static IReadOnlyList<ProfileSummaryRow> Describe(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var rows = new List<ProfileSummaryRow>
        {
            new("Procesy", profile.ProcessNames.Count > 0
                ? string.Join(", ", profile.ProcessNames)
                : "brak — profil wybierasz ręcznie w sekcji Tłumaczenie"),
        };
        if (profile.WindowTitles.Count > 0) rows.Add(new("Tytuły okien", string.Join(", ", profile.WindowTitles)));
        rows.Add(new("Język gry", profile.SourceLanguage));
        if (!string.IsNullOrWhiteSpace(profile.Glossary)) rows.Add(new("Słownik profilu", profile.Glossary));
        if (profile.Ocr?.Upscale is { } upscale) rows.Add(new("Powiększenie OCR", UpscaleLabel(upscale)));
        if (profile.Ocr is { } ocr && ocr.MinTextHeight != new OcrProfileSettings().MinTextHeight)
            rows.Add(new("Min. wysokość tekstu", $"{ocr.MinTextHeight} px"));
        if (profile.ChangeDetection is { } change)
            rows.Add(new("Wykrywanie zmian", $"{ProfileNumbers.Format(change.Fps)} analiz/s, próg {ProfileNumbers.Format(change.Threshold)}"));
        if (!string.IsNullOrWhiteSpace(profile.Overlay?.FontFamily)) rows.Add(new("Krój nakładki", profile.Overlay.FontFamily));
        if (profile.Overlay?.FontRules is { Count: > 0 } rules) rows.Add(new("Reguły kroju", $"{rules.Count} (krój dla rodzaju napisu)"));
        if (profile.Live?.IgnoreRegions is { Count: > 0 } regions) rows.Add(new("Pomijane obszary", $"{regions.Count} (tryb live)"));
        if (!string.IsNullOrWhiteSpace(profile.RecommendedMode)) rows.Add(new("Zalecany tryb", ModeLabel(profile.RecommendedMode)));
        if (profile.Corpus is not null) rows.Add(new("Recepta korpusu", "tak (narzędzie CorpusTool)"));
        if (profile.Online == true) rows.Add(new("Gra online", "tak"));
        return rows;
    }

    public static string UpscaleLabel(double upscale) =>
        upscale == 1.0 ? "1× (bez powiększania)" : ProfileNumbers.Format(upscale) + "×";

    public static string ModeLabel(string mode) => mode switch
    {
        ProfileTuningRules.ManualRegionMode => "ręczne zaznaczenie regionu",
        ProfileTuningRules.LiveMode => "tryb live",
        _ => mode,
    };
}
