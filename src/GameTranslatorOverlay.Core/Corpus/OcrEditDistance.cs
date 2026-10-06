using System.Buffers;
using System.Text;

namespace GameTranslatorOverlay.Core.Corpus;

public readonly record struct OcrPrefixResult(int Cost, int End);

public static class OcrEditDistance
{
    public const int Unit = 100;
    public const int StrokeConfusion = 25;
    public const int DigitConfusion = 25;
    public const int HnConfusion = 40;
    public const int CeConfusion = 50;
    public const int SplitConfusion = 30;
    public const int Spacing = 30;
    public const int Punctuation = 30;
    public const int EdgeStroke = 30;

    private const int Blocked = int.MaxValue / 4;
    private const int StackLimit = 2048;

    public static int Distance(ReadOnlySpan<char> reading, ReadOnlySpan<char> corpus, int maxCost, bool strictEdges = false)
    {
        if (maxCost < 0) return Blocked;
        if (reading.Length == 0 && corpus.Length == 0) return 0;
        var cells = (reading.Length + 1) * (corpus.Length + 1);
        int[]? rented = null;
        Span<int> table = cells <= StackLimit ? stackalloc int[cells] : (rented = ArrayPool<int>.Shared.Rent(cells));
        try
        {
            if (!Fill(reading, corpus, corpus.Length, maxCost, prefix: false, table, strictEdges)) return maxCost + 1;
            var cost = table[reading.Length * (corpus.Length + 1) + corpus.Length];
            return cost > maxCost ? maxCost + 1 : cost;
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    public static OcrPrefixResult? Prefix(ReadOnlySpan<char> reading, ReadOnlySpan<char> corpus, int maxCost)
    {
        if (reading.Length == 0 || corpus.Length == 0 || maxCost < 0) return null;
        var width = Math.Min(corpus.Length, reading.Length + maxCost / Math.Min(Spacing, EdgeStroke) + 1);
        var cells = (reading.Length + 1) * (width + 1);
        int[]? rented = null;
        Span<int> table = cells <= StackLimit ? stackalloc int[cells] : (rented = ArrayPool<int>.Shared.Rent(cells));
        try
        {
            if (!Fill(reading, corpus, width, maxCost, prefix: true, table, strictEdges: false)) return null;
            var row = table.Slice(reading.Length * (width + 1), width + 1);
            var best = Blocked;
            var end = -1;
            for (var j = 1; j <= width; j++)
            {
                if (row[j] < best)
                {
                    best = row[j];
                    end = j;
                }
            }
            return end < 0 || best > maxCost ? null : new OcrPrefixResult(best, end);
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static bool Fill(ReadOnlySpan<char> reading, ReadOnlySpan<char> corpus, int width, int maxCost, bool prefix, Span<int> table,
        bool strictEdges)
    {
        var n = reading.Length;
        var stride = width + 1;
        var readingGuarded = Guarded(reading);
        var corpusGuarded = Guarded(corpus[..width]);
        var corpusEnd = prefix ? -1 : corpus.Length;
        var previousRowMin = Blocked;

        for (var i = 0; i <= n; i++)
        {
            var rowMin = Blocked;
            var rowOffset = i * stride;
            var r = i > 0 ? reading[i - 1] : '\0';
            var rGuard = i > 0 && readingGuarded && CorpusText.IsNumberCharacter(reading, i - 1);
            for (var j = 0; j <= width; j++)
            {
                int value;
                if (i == 0 && j == 0)
                {
                    value = 0;
                }
                else
                {
                    value = Blocked;
                    var c = j > 0 ? corpus[j - 1] : '\0';
                    var cGuard = j > 0 && corpusGuarded && CorpusText.IsNumberCharacter(corpus, j - 1);
                    if (i > 0 && j > 0)
                    {
                        var step = Substitution(r, rGuard, c, cGuard);
                        if (step < Blocked) value = Math.Min(value, table[rowOffset - stride + j - 1] + step);
                    }
                    if (i > 0 && !rGuard)
                    {
                        var step = Gap(r, j == 0 || j == corpusEnd, strictEdges);
                        if (step < Blocked) value = Math.Min(value, table[rowOffset - stride + j] + step);
                    }
                    if (j > 0 && !cGuard)
                    {
                        var step = Gap(c, i == 0 || i == n, strictEdges);
                        if (step < Blocked) value = Math.Min(value, table[rowOffset + j - 1] + step);
                    }
                    if (i >= 2 && j >= 1 && IsSplit(reading[i - 2], r, c))
                    {
                        value = Math.Min(value, table[rowOffset - 2 * stride + j - 1] + SplitConfusion);
                    }
                    if (i >= 1 && j >= 2 && IsSplit(corpus[j - 2], c, r))
                    {
                        value = Math.Min(value, table[rowOffset - stride + j - 2] + SplitConfusion);
                    }
                    if (value > Blocked) value = Blocked;
                }
                table[rowOffset + j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > maxCost && previousRowMin > maxCost) return false;
            previousRowMin = rowMin;
        }
        return true;
    }

    private static bool Guarded(ReadOnlySpan<char> text) => text.IndexOfAny("0123456789") >= 0;

    private static bool IsSplit(char first, char second, char single) =>
        (first, second, single) is ('r', 'n', 'm') or ('c', 'l', 'd') or ('v', 'v', 'w');

    private static int Substitution(char reading, bool readingGuard, char corpus, bool corpusGuard)
    {
        if (reading == corpus) return 0;
        if (readingGuard || corpusGuard)
        {
            return !corpusGuard && char.IsAsciiDigit(reading) && CorpusText.IsDigitMistakenFor(reading, corpus)
                ? DigitConfusion
                : Blocked;
        }
        var r = char.ToLowerInvariant(reading);
        var c = char.ToLowerInvariant(corpus);
        if (r == c) return 0;
        if (IsStroke(r) && IsStroke(c)) return StrokeConfusion;
        if ((r, c) is ('h', 'n') or ('n', 'h')) return HnConfusion;
        if ((r, c) is ('c', 'e') or ('e', 'c')) return CeConfusion;
        if (IsPunctuation(r) && IsPunctuation(c)) return Punctuation;
        return Unit;
    }

    private static int Gap(char ch, bool edge, bool strictEdges)
    {
        if (ch == ' ') return Spacing;
        if (IsPunctuation(ch)) return Punctuation;
        if (edge && IsStroke(char.ToLowerInvariant(ch))) return EdgeStroke;
        return edge && strictEdges ? Blocked : Unit;
    }

    public static bool IsStroke(char ch) => ch is 'i' or 'l' or '|' or '!' or '\'';

    private static bool IsPunctuation(char ch) => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch);

    public static string Canonical(string text)
    {
        var compact = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var ch = char.ToLowerInvariant(raw);
            if (ch == '\'' || ch == '|' || ch == '!') compact.Append('l');
            else if (char.IsLetterOrDigit(ch)) compact.Append(ch);
        }
        var builder = new StringBuilder(compact.Length);
        for (var i = 0; i < compact.Length; i++)
        {
            var ch = compact[i];
            var next = i + 1 < compact.Length ? compact[i + 1] : '\0';
            if (IsSplit(ch, next, 'm') || IsSplit(ch, next, 'd') || IsSplit(ch, next, 'w'))
            {
                builder.Append(ch == 'r' ? 'm' : ch == 'c' ? 'd' : 'w');
                i++;
                continue;
            }
            builder.Append(ch switch
            {
                'i' or '1' => 'l',
                'h' => 'n',
                'c' => 'e',
                '0' => 'o',
                '5' => 's',
                '8' => 'b',
                _ => ch,
            });
        }
        var start = 0;
        var end = builder.Length;
        while (start < end && builder[start] == 'l') start++;
        while (end > start && builder[end - 1] == 'l') end--;
        return builder.ToString(start, end - start);
    }

    public static string SortedCanonical(string text)
    {
        var characters = Canonical(text).ToCharArray();
        Array.Sort(characters);
        return new string(characters);
    }

    public static uint Mask(ReadOnlySpan<char> text)
    {
        var mask = 0u;
        foreach (var ch in text)
        {
            mask |= ch is >= 'a' and <= 'z' ? 1u << (ch - 'a') : 1u << (26 + ch % 6);
        }
        return mask;
    }

    public static bool MaskWithin(uint a, uint b, int gap) =>
        System.Numerics.BitOperations.PopCount(a & ~b) <= gap && System.Numerics.BitOperations.PopCount(b & ~a) <= gap;

    public static int BagGap(ReadOnlySpan<char> sortedA, ReadOnlySpan<char> sortedB)
    {
        var onlyA = 0;
        var onlyB = 0;
        var i = 0;
        var j = 0;
        while (i < sortedA.Length && j < sortedB.Length)
        {
            if (sortedA[i] == sortedB[j])
            {
                i++;
                j++;
            }
            else if (sortedA[i] < sortedB[j])
            {
                onlyA++;
                i++;
            }
            else
            {
                onlyB++;
                j++;
            }
        }
        onlyA += sortedA.Length - i;
        onlyB += sortedB.Length - j;
        return Math.Max(onlyA, onlyB);
    }
}
