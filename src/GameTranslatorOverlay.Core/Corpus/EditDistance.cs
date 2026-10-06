namespace GameTranslatorOverlay.Core.Corpus;

public readonly record struct AlignmentResult(int Distance, int Start, int End);

public static class EditDistance
{
    public static int Bounded(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int maxDistance)
    {
        if (maxDistance < 0) return 0;
        if (Math.Abs(a.Length - b.Length) > maxDistance) return maxDistance + 1;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var width = b.Length + 1;
        Span<int> buffer = width <= 512 ? stackalloc int[width * 2] : new int[width * 2];
        var previous = buffer[..width];
        var current = buffer[width..];
        for (var j = 0; j < width; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = i;
            var ai = a[i - 1];
            for (var j = 1; j < width; j++)
            {
                var substitution = previous[j - 1] + (ai == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                var value = substitution < deletion ? substitution : deletion;
                if (insertion < value) value = insertion;
                current[j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > maxDistance) return maxDistance + 1;
            var swap = previous;
            previous = current;
            current = swap;
        }

        var result = previous[b.Length];
        return result > maxDistance ? maxDistance + 1 : result;
    }

    public static double Ratio(ReadOnlySpan<char> a, ReadOnlySpan<char> b, double minimumRatio)
    {
        var longest = Math.Max(a.Length, b.Length);
        if (longest == 0) return 1.0;
        var maxDistance = (int)Math.Floor((1.0 - minimumRatio) * longest + 1e-9);
        var distance = Bounded(a, b, maxDistance);
        return distance > maxDistance ? 0.0 : 1.0 - (double)distance / longest;
    }

    public static int BoundedGuarded(ReadOnlySpan<char> reading, ReadOnlySpan<char> corpus, int maxDistance)
    {
        if (maxDistance < 0) return 0;
        if (Math.Abs(reading.Length - corpus.Length) > maxDistance) return maxDistance + 1;

        var blocked = maxDistance + 1;
        var width = corpus.Length + 1;
        Span<bool> readingGuard = reading.Length <= 512 ? stackalloc bool[reading.Length] : new bool[reading.Length];
        Span<bool> corpusGuard = corpus.Length <= 512 ? stackalloc bool[corpus.Length] : new bool[corpus.Length];
        for (var i = 0; i < reading.Length; i++) readingGuard[i] = CorpusText.IsNumberCharacter(reading, i);
        for (var j = 0; j < corpus.Length; j++) corpusGuard[j] = CorpusText.IsNumberCharacter(corpus, j);

        Span<int> buffer = width <= 512 ? stackalloc int[width * 2] : new int[width * 2];
        var previous = buffer[..width];
        var current = buffer[width..];
        previous[0] = 0;
        for (var j = 1; j < width; j++) previous[j] = Math.Min(blocked, corpusGuard[j - 1] ? blocked : previous[j - 1] + 1);

        for (var i = 1; i <= reading.Length; i++)
        {
            var r = reading[i - 1];
            var rGuard = readingGuard[i - 1];
            current[0] = rGuard ? blocked : Math.Min(blocked, previous[0] + 1);
            var rowMin = current[0];
            for (var j = 1; j < width; j++)
            {
                var c = corpus[j - 1];
                var cGuard = corpusGuard[j - 1];
                int step;
                if (r == c) step = 0;
                else if (!rGuard && !cGuard) step = 1;
                else if (!cGuard && char.IsAsciiDigit(r) && CorpusText.IsDigitMistakenFor(r, c)) step = 1;
                else step = blocked;
                var value = Math.Min(blocked, previous[j - 1] + step);
                if (!rGuard) value = Math.Min(value, previous[j] + 1);
                if (!cGuard) value = Math.Min(value, current[j - 1] + 1);
                current[j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > maxDistance) return blocked;
            var swap = previous;
            previous = current;
            current = swap;
        }

        return Math.Min(blocked, previous[corpus.Length]);
    }

    public static double GuardedRatio(ReadOnlySpan<char> reading, ReadOnlySpan<char> corpus, double minimumRatio)
    {
        if (!CorpusText.HasDigit(reading) && !CorpusText.HasDigit(corpus)) return Ratio(reading, corpus, minimumRatio);
        var longest = Math.Max(reading.Length, corpus.Length);
        if (longest == 0) return 1.0;
        var maxDistance = (int)Math.Floor((1.0 - minimumRatio) * longest + 1e-9);
        var distance = BoundedGuarded(reading, corpus, maxDistance);
        return distance > maxDistance ? 0.0 : 1.0 - (double)distance / longest;
    }

    public static AlignmentResult? FindWithin(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, int maxDistance)
    {
        if (pattern.Length == 0 || text.Length == 0 || maxDistance < 0) return null;

        var width = text.Length + 1;
        var costs = new int[width * 2];
        var starts = new int[width * 2];
        var previousOffset = 0;
        var currentOffset = width;
        for (var j = 0; j < width; j++)
        {
            costs[j] = 0;
            starts[j] = j;
        }

        for (var i = 1; i <= pattern.Length; i++)
        {
            costs[currentOffset] = i;
            starts[currentOffset] = 0;
            var rowMin = i;
            var pi = pattern[i - 1];
            for (var j = 1; j < width; j++)
            {
                var diagonal = costs[previousOffset + j - 1] + (pi == text[j - 1] ? 0 : 1);
                var up = costs[previousOffset + j] + 1;
                var left = costs[currentOffset + j - 1] + 1;
                int value;
                int start;
                if (diagonal <= up && diagonal <= left)
                {
                    value = diagonal;
                    start = starts[previousOffset + j - 1];
                }
                else if (up <= left)
                {
                    value = up;
                    start = starts[previousOffset + j];
                }
                else
                {
                    value = left;
                    start = starts[currentOffset + j - 1];
                }
                costs[currentOffset + j] = value;
                starts[currentOffset + j] = start;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > maxDistance) return null;
            (previousOffset, currentOffset) = (currentOffset, previousOffset);
        }

        var bestDistance = int.MaxValue;
        var bestEnd = 0;
        var bestStart = 0;
        for (var j = 1; j < width; j++)
        {
            var value = costs[previousOffset + j];
            var start = starts[previousOffset + j];
            if (value < bestDistance || (value == bestDistance && j - start > bestEnd - bestStart))
            {
                bestDistance = value;
                bestEnd = j;
                bestStart = start;
            }
        }
        return bestDistance > maxDistance ? null : new AlignmentResult(bestDistance, bestStart, bestEnd);
    }
}
