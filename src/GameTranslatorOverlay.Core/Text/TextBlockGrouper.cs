using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Text;

public sealed record TextBlock(string Text, RectPx Box, IReadOnlyList<OcrLine> Lines);

public static class TextBlockMetrics
{
    /// <summary>
    /// Mediana wysokości linii bloku (px) — pozwala nakładce dobrać rozmiar czcionki
    /// tłumaczenia do rozmiaru oryginalnego tekstu.
    /// </summary>
    public static int MedianLineHeight(TextBlock block)
    {
        if (block.Lines.Count == 0) return block.Box.Height;
        var heights = block.Lines.Select(static l => l.Box.Height).OrderBy(static h => h).ToList();
        return heights[heights.Count / 2];
    }
}

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
            var matching = new List<int>();
            for (var i = 0; i < clusters.Count; i++)
            {
                if (Belongs(clusterBoxes[i], line.Box, maxVerticalGap, maxHorizontalGap)
                    && SimilarLineHeight(clusters[i], line))
                {
                    matching.Add(i);
                }
            }

            if (matching.Count == 0)
            {
                clusters.Add([line]);
                clusterBoxes.Add(line.Box);
                continue;
            }

            // Linia-mostek (np. szeroki nagłówek nad dwiema kolumnami) łączy wszystkie
            // pasujące klastry w jeden — bez scalania blok byłby rozerwany, a kolejność
            // czytania przeplatana między fragmentami.
            var target = matching[0];
            for (var j = matching.Count - 1; j >= 1; j--)
            {
                var index = matching[j];
                clusters[target].AddRange(clusters[index]);
                clusterBoxes[target] = clusterBoxes[target].Union(clusterBoxes[index]);
                clusters.RemoveAt(index);
                clusterBoxes.RemoveAt(index);
            }

            clusters[target].Add(line);
            clusterBoxes[target] = clusterBoxes[target].Union(line.Box);
        }

        return clusters
            .Select(static cluster =>
            {
                var rows = SplitIntoRows(cluster);
                var sorted = rows.SelectMany(static row => row).ToList();
                var box = sorted.Aggregate(default(RectPx), static (acc, l) => acc.Union(l.Box));
                // Fragmenty leżące w jednym wierszu (OCR potrafi pociąć „Chapter One: Fall Term”
                // na dwie „linie” obok siebie) łączymy spacją — inaczej tłumaczenie
                // dostałoby sztuczny podział na dwa wiersze, a nakładka dwuwierszową czcionkę.
                var text = string.Join('\n', rows.Select(static row => string.Join(' ', row.Select(static l => l.Text))));
                return new TextBlock(text, box, sorted);
            })
            .ToList();
    }

    /// <summary>
    /// Dzieli linie klastra na wiersze wizualne: linia trafia do bieżącego wiersza, gdy
    /// w pionie nachodzi na niego co najmniej w połowie swojej (lub jego) wysokości.
    /// Wewnątrz wiersza kolejność czytania wyznacza X.
    /// </summary>
    private static List<List<OcrLine>> SplitIntoRows(List<OcrLine> cluster)
    {
        var byTop = cluster.OrderBy(static l => l.Box.Y).ThenBy(static l => l.Box.X).ToList();
        var rows = new List<List<OcrLine>>();
        var rowTop = 0;
        var rowBottom = 0;

        foreach (var line in byTop)
        {
            if (rows.Count > 0)
            {
                var overlap = Math.Min(rowBottom, line.Box.Bottom) - Math.Max(rowTop, line.Box.Y);
                var reference = Math.Max(1, Math.Min(rowBottom - rowTop, line.Box.Height));
                if (overlap >= reference * 0.5)
                {
                    rows[^1].Add(line);
                    rowTop = Math.Min(rowTop, line.Box.Y);
                    rowBottom = Math.Max(rowBottom, line.Box.Bottom);
                    continue;
                }
            }

            rows.Add([line]);
            rowTop = line.Box.Y;
            rowBottom = line.Box.Bottom;
        }

        foreach (var row in rows)
        {
            row.Sort(static (a, b) => a.Box.X.CompareTo(b.Box.X));
        }
        return rows;
    }

    /// <summary>
    /// Linia o wysokości różniącej się od typowej wysokości klastra ponad 2,2× to inny
    /// element interfejsu (podpowiedź „Tab” obok daty, nagłówek nad drobnym opisem) —
    /// sklejenie ich dałoby blok o sztucznie wysokim boxie i nieczytelne tłumaczenie.
    /// </summary>
    private static bool SimilarLineHeight(List<OcrLine> cluster, OcrLine line)
    {
        var typical = MedianLineHeight(cluster);
        var ratio = (double)Math.Max(typical, line.Box.Height) / Math.Max(1, Math.Min(typical, line.Box.Height));
        return ratio <= 2.2;
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
