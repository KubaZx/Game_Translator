using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Text;

public sealed record TextBlock(string Text, RectPx Box, IReadOnlyList<OcrLine> Lines);

public sealed record GroupingOptions(double MaxVerticalGapFactor = 1.0, double MaxHorizontalGapFactor = 2.0)
{
    public static readonly GroupingOptions Default = new();
}

/// <summary>
/// Łączy linie z OCR w spójne bloki (np. jeden tooltip, jeden akapit dialogu).
/// Dwie linie trafiają do wspólnego bloku, gdy leżą blisko siebie w pionie
/// (odstęp mniejszy niż wysokość typowej linii razy współczynnik) i nachodzą na siebie
/// w poziomie. Kolumny tekstu oddalone w poziomie zostają osobnymi blokami.
/// </summary>
public static class TextBlockGrouper
{
    public static IReadOnlyList<TextBlock> Group(IReadOnlyList<OcrLine> lines, GroupingOptions? options = null)
    {
        if (lines.Count == 0) return [];

        options ??= GroupingOptions.Default;
        var medianHeight = Math.Max(1, MedianLineHeight(lines));
        var maxVerticalGap = medianHeight * options.MaxVerticalGapFactor;
        var maxHorizontalGap = medianHeight * options.MaxHorizontalGapFactor;

        var ordered = lines.OrderBy(static l => l.Box.Y).ThenBy(static l => l.Box.X).ToList();
        var clusters = new List<List<OcrLine>>();
        var clusterBoxes = new List<RectPx>();

        foreach (var line in ordered)
        {
            var assigned = -1;
            for (var i = 0; i < clusters.Count; i++)
            {
                if (Belongs(clusterBoxes[i], line.Box, maxVerticalGap, maxHorizontalGap))
                {
                    assigned = i;
                    break;
                }
            }

            if (assigned < 0)
            {
                clusters.Add([line]);
                clusterBoxes.Add(line.Box);
            }
            else
            {
                clusters[assigned].Add(line);
                clusterBoxes[assigned] = clusterBoxes[assigned].Union(line.Box);
            }
        }

        return clusters
            .Select(static cluster =>
            {
                var sorted = cluster.OrderBy(static l => l.Box.Y).ThenBy(static l => l.Box.X).ToList();
                var box = sorted.Aggregate(default(RectPx), static (acc, l) => acc.Union(l.Box));
                var text = string.Join('\n', sorted.Select(static l => l.Text));
                return new TextBlock(text, box, sorted);
            })
            .ToList();
    }

    private static bool Belongs(RectPx cluster, RectPx line, double maxVerticalGap, double maxHorizontalGap)
    {
        var verticalGap = line.Y >= cluster.Bottom
            ? line.Y - cluster.Bottom
            : cluster.Y - line.Bottom;
        if (verticalGap > maxVerticalGap) return false;

        var horizontalGap = line.X >= cluster.Right
            ? line.X - cluster.Right
            : cluster.X - line.Right;
        return horizontalGap <= maxHorizontalGap;
    }

    private static int MedianLineHeight(IReadOnlyList<OcrLine> lines)
    {
        var heights = lines.Select(static l => l.Box.Height).OrderBy(static h => h).ToList();
        return heights[heights.Count / 2];
    }
}
