namespace GameTranslatorOverlay.Core.Text;

/// <summary>
/// Podobieństwo dwóch tekstów w skali 0–1 (1 = identyczne) na bazie odległości
/// Levenshteina. Służy do rozpoznania, że dwa różne odczyty OCR to ten sam napis
/// (drżenie nad ruchomym tłem), a nie nowa treść.
/// </summary>
public static class TextSimilarity
{
    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var longest = Math.Max(a.Length, b.Length);
        if (longest == 0) return 1.0;
        return 1.0 - (double)Distance(a, b) / longest;
    }

    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
