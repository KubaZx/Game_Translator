using System.Globalization;

namespace GameTranslatorOverlay.Core.Profiles;

public sealed class OverlayFontRule
{
    public const int DefaultColorTolerance = 40;

    public string? FontFamily { get; set; }
    public string? TextColor { get; set; }
    public int ColorTolerance { get; set; } = DefaultColorTolerance;
    public bool? Outlined { get; set; }
    public double? MinLetterHeight { get; set; }
    public double? MaxLetterHeight { get; set; }

    public bool Matches(TextStyleSample sample)
    {
        if (TextColor is not null)
        {
            if (!OverlayFontRuleSet.TryParseColor(TextColor, out var rgb) || !OverlayFontRuleSet.ColorWithin(sample.TextRgb, rgb, ColorTolerance))
                return false;
        }
        if (Outlined is { } outlined && outlined != sample.Outlined) return false;
        if (MinLetterHeight is { } min && !(sample.LetterHeight >= min)) return false;
        if (MaxLetterHeight is { } max && !(sample.LetterHeight <= max)) return false;
        return true;
    }
}

public readonly record struct TextStyleSample(int TextRgb, bool Outlined, double LetterHeight);

public sealed record OverlayFontRuleCheck(IReadOnlyList<OverlayFontRule> Rules, IReadOnlyList<string> Warnings);

public static class OverlayFontRuleSet
{
    public const int MaxRules = 16;

    public static bool IsValidFamilyName(string? name) =>
        name is not null
        && name.Trim().Length is > 0 and <= 64
        && name.IndexOfAny(['\\', '/', ':', '#', '*', '?', '"', '<', '>', '|']) < 0;

    public static bool TryParseColor(string? text, out int rgb)
    {
        rgb = -1;
        if (text is null) return false;
        var hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return false;
        rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool ColorWithin(int sample, int target, int tolerance)
    {
        if (sample < 0 || target < 0 || tolerance < 0) return false;
        return Math.Abs(((sample >> 16) & 0xFF) - ((target >> 16) & 0xFF)) <= tolerance
            && Math.Abs(((sample >> 8) & 0xFF) - ((target >> 8) & 0xFF)) <= tolerance
            && Math.Abs((sample & 0xFF) - (target & 0xFF)) <= tolerance;
    }

    public static OverlayFontRule? Match(IReadOnlyList<OverlayFontRule> rules, TextStyleSample sample)
    {
        foreach (var rule in rules)
        {
            if (rule.Matches(sample)) return rule;
        }
        return null;
    }

    public static string Resolve(string family, IReadOnlyList<OverlayFontRule> rules, TextStyleSample sample) =>
        Match(rules, sample)?.FontFamily?.Trim() is { Length: > 0 } chosen ? chosen : family;

    public static OverlayFontRuleCheck Validate(OverlayProfileSettings? overlay, Func<string, bool>? isFamilyAvailable = null)
    {
        var warnings = new List<string>();
        if (overlay is null) return new OverlayFontRuleCheck([], warnings);

        if (overlay.FontFamily is { } family && IsValidFamilyName(family) && isFamilyAvailable is not null && !isFamilyAvailable(family.Trim()))
        {
            warnings.Add($"Krój overlay.fontFamily „{family.Trim()}” nie jest dołączony do aplikacji ani zainstalowany w Windows — nakładka użyje kroju zastępczego.");
        }

        var rules = new List<OverlayFontRule>();
        var source = overlay.FontRules ?? [];
        for (var i = 0; i < source.Count; i++)
        {
            if (i >= MaxRules)
            {
                warnings.Add($"Profil ma więcej niż {MaxRules} reguł overlay.fontRules — pomijam nadmiarowe ({source.Count - MaxRules}).");
                break;
            }
            var problem = Problem(source[i], isFamilyAvailable);
            if (problem is null)
            {
                rules.Add(source[i]);
            }
            else
            {
                warnings.Add($"Reguła overlay.fontRules[{i + 1}]: {problem} Pomijam tę regułę.");
            }
        }
        return new OverlayFontRuleCheck(rules, warnings);
    }

    private static string? Problem(OverlayFontRule? rule, Func<string, bool>? isFamilyAvailable)
    {
        if (rule is null) return "pusta reguła.";
        if (!IsValidFamilyName(rule.FontFamily)) return "pole fontFamily musi być nazwą kroju (do 64 znaków, bez ścieżki).";
        var family = rule.FontFamily!.Trim();
        if (rule.TextColor is not null && !TryParseColor(rule.TextColor, out _))
            return $"kolor textColor „{rule.TextColor}” nie jest zapisem #RRGGBB.";
        if (rule.ColorTolerance is < 0 or > 255) return "colorTolerance musi mieścić się w zakresie 0–255.";
        if (rule.MinLetterHeight is { } min && !(min > 0 && min <= 1))
            return "minLetterHeight musi być ułamkiem wysokości ekranu (powyżej 0, najwyżej 1).";
        if (rule.MaxLetterHeight is { } max && !(max > 0 && max <= 1))
            return "maxLetterHeight musi być ułamkiem wysokości ekranu (powyżej 0, najwyżej 1).";
        if (rule is { MinLetterHeight: { } low, MaxLetterHeight: { } high } && low > high)
            return "minLetterHeight nie może być większe niż maxLetterHeight.";
        if (rule is { TextColor: null, Outlined: null, MinLetterHeight: null, MaxLetterHeight: null })
            return "reguła musi mieć warunek (textColor, outlined albo wysokość liter) — krój dla całego tekstu ustawia overlay.fontFamily.";
        if (isFamilyAvailable is not null && !isFamilyAvailable(family))
            return $"krój „{family}” nie jest dołączony do aplikacji ani zainstalowany w Windows.";
        return null;
    }
}
