using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public static partial class TextMarkers
{
    public static IReadOnlyDictionary<string, int> Find(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Marker().Matches(text))
        {
            counts[match.Value] = counts.GetValueOrDefault(match.Value) + 1;
        }
        return counts;
    }

    public static bool HasMarkers(string text) => Marker().IsMatch(text);

    public static bool Preserved(string source, string translated)
    {
        var expected = Find(source);
        if (expected.Count == 0) return true;
        var actual = Find(translated);
        return expected.All(pair => actual.GetValueOrDefault(pair.Key) >= pair.Value);
    }

    [GeneratedRegex(@"\{[A-Za-z0-9_.:\-]{1,24}\}|\[[A-Za-z0-9]{1,3}\]|%(?:\d+\$)?[sdif]|%%", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
}
