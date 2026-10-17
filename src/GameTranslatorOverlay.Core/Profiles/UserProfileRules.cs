using System.Globalization;
using System.Text;
using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Profiles;

public static class ProfileIds
{
    public const int MaxLength = 48;

    public const string Fallback = "gra";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private static readonly Dictionary<char, string> Transliteration = new()
    {
        ['ł'] = "l", ['Ł'] = "l", ['ø'] = "o", ['Ø'] = "o", ['đ'] = "d", ['Đ'] = "d",
        ['ß'] = "ss", ['æ'] = "ae", ['Æ'] = "ae", ['œ'] = "oe", ['Œ'] = "oe", ['þ'] = "th", ['Þ'] = "th", ['ı'] = "i",
    };

    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (Transliteration.TryGetValue(ch, out var replacement))
            {
                builder.Append(replacement);
                continue;
            }
            var lower = char.ToLowerInvariant(ch);
            builder.Append(char.IsAsciiLetterLower(lower) || char.IsAsciiDigit(lower) ? lower : '-');
        }

        var slug = string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return Trim(slug, MaxLength);
    }

    public static string CreateUnique(string? name, string? processName, IEnumerable<string> takenIds)
    {
        ArgumentNullException.ThrowIfNull(takenIds);
        var taken = new HashSet<string>(takenIds.Where(static id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);

        var baseId = Slugify(name);
        if (baseId.Length == 0) baseId = Slugify(LiveTargetResolver.NormalizeProcessName(processName));
        if (baseId.Length == 0) baseId = Fallback;

        if (IsFree(baseId, taken)) return baseId;
        for (var suffix = 2; ; suffix++)
        {
            var tail = "-" + suffix.ToString(CultureInfo.InvariantCulture);
            var candidate = Trim(baseId, MaxLength - tail.Length) + tail;
            if (IsFree(candidate, taken)) return candidate;
        }
    }

    public static bool IsReservedDeviceName(string id) => ReservedDeviceNames.Contains(id);

    private static bool IsFree(string id, HashSet<string> taken) => !taken.Contains(id) && !IsReservedDeviceName(id);

    private static string Trim(string slug, int maxLength)
    {
        if (slug.Length <= maxLength) return slug;
        var cut = slug[..maxLength];
        if (slug[maxLength] == '-') return cut.TrimEnd('-');
        var lastDash = cut.LastIndexOf('-');
        return (lastDash >= maxLength / 2 ? cut[..lastDash] : cut).TrimEnd('-');
    }
}

public static class ProfileNames
{
    public const int MaxLength = 80;

    public static string Clean(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string SuggestFromWindow(string? windowTitle, string? processName)
    {
        var title = Clean(windowTitle);
        if (title.Length == 0) title = Clean(LiveTargetResolver.NormalizeProcessName(processName));
        if (title.Length <= MaxLength) return title;
        var cut = title[..MaxLength];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > MaxLength / 2 ? cut[..lastSpace] : cut).TrimEnd(' ', '-', '—', ':', ',');
    }

    public static string SuggestCopy(string? sourceName, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var taken = new HashSet<string>(existingNames.Select(Clean), StringComparer.OrdinalIgnoreCase);
        var source = Clean(sourceName);
        if (source.Length == 0) source = "Profil";
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " (własny)" : $" (własny {number.ToString(CultureInfo.InvariantCulture)})";
            var head = source.Length + suffix.Length > MaxLength ? source[..(MaxLength - suffix.Length)].TrimEnd() : source;
            var candidate = head + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    public static string? Validate(string? name, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var clean = Clean(name);
        if (clean.Length == 0) return "Podaj nazwę profilu.";
        if (clean.Length > MaxLength) return $"Nazwa profilu może mieć najwyżej {MaxLength} znaków.";
        if (clean.Any(char.IsControl)) return "Nazwa profilu nie może zawierać znaków sterujących.";
        if (existingNames.Any(existing => string.Equals(Clean(existing), clean, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Profil o nazwie „{clean}” już istnieje — wybierz inną nazwę.";
        }
        return null;
    }
}
