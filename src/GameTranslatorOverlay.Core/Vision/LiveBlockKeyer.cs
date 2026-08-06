using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Vision;

public sealed record KeyedTextBlock(string Key, TextBlock Block, string NormalizedText);

/// <summary>
/// Nadaje blokom tekstu stabilne klucze (skrót treści + numer wystąpienia).
/// Klucz nie zależy od pozycji — tekst, który tylko się przesunął, zachowuje klucz,
/// więc nakładka aktualizuje jego położenie zamiast migotać (usuwać i dodawać).
/// </summary>
public static class LiveBlockKeyer
{
    public static IReadOnlyList<KeyedTextBlock> AssignKeys(IReadOnlyList<TextBlock> blocks)
    {
        var ordered = blocks
            .OrderBy(static b => b.Box.Y)
            .ThenBy(static b => b.Box.X);

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<KeyedTextBlock>(blocks.Count);

        foreach (var block in ordered)
        {
            var normalized = TextNormalizer.Normalize(block.Text);
            var hash = TextHasher.Sha256Hex(normalized);
            var index = occurrences.TryGetValue(hash, out var seen) ? seen : 0;
            occurrences[hash] = index + 1;
            result.Add(new KeyedTextBlock($"{hash[..16]}#{index}", block, normalized));
        }

        return result;
    }
}
