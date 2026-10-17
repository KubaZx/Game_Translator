namespace GameTranslatorOverlay.MotionLab.Series;

internal static class SeriesMath
{
    private const double GoldenFraction = 0.6180339887498949;

    public sealed record RankTest(double P, double Size, double U, bool Exact);

    public sealed record SignedRankTest(double P, int N, int Positive, int Negative, int Zero);

    public static IReadOnlyList<(int Start, int End)> IsolatedRuns(int count, Func<int, bool> agrees, int maxLength)
    {
        var runs = new List<(int Start, int End)>();
        for (var k = 0; k < count;)
        {
            if (agrees(k))
            {
                k++;
                continue;
            }
            var end = k;
            while (end < count && !agrees(end)) end++;
            if (k > 0 && end < count && end - k <= maxLength) runs.Add((k, end));
            k = end;
        }
        return runs;
    }

    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        var position = (sorted.Count - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(static v => !double.IsNaN(v)).Order().ToList();
        return Quantile(sorted, 0.5);
    }

    public static IReadOnlyList<int> GoldenPhases(int count, int periodMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(periodMs, 1);
        var phases = new List<int>(count);
        for (var k = 0; k < count; k++)
        {
            var fraction = k * GoldenFraction % 1.0;
            phases.Add((int)Math.Round(fraction * periodMs) % periodMs);
        }
        return phases;
    }

    public static RankTest MannWhitney(IReadOnlyList<double> a, IReadOnlyList<double> b, double alpha)
    {
        if (a.Count == 0 || b.Count == 0) return new RankTest(1, 0, double.NaN, true);
        var pooled = a.Select(static v => (Value: v, Group: 0)).Concat(b.Select(static v => (Value: v, Group: 1)))
            .OrderBy(static x => x.Value).ToList();
        var n = pooled.Count;
        var doubledRanks = new int[n];
        for (var i = 0; i < n;)
        {
            var j = i;
            while (j + 1 < n && pooled[j + 1].Value == pooled[i].Value) j++;
            var doubled = i + j + 2;
            for (var k = i; k <= j; k++) doubledRanks[k] = doubled;
            i = j + 1;
        }
        var observed = 0;
        for (var i = 0; i < n; i++) if (pooled[i].Group == 1) observed += doubledRanks[i];

        var take = b.Count;
        var maxSum = doubledRanks.Sum();
        var ways = new double[take + 1, maxSum + 1];
        ways[0, 0] = 1;
        foreach (var rank in doubledRanks)
        {
            for (var k = take; k >= 1; k--)
            {
                for (var s = maxSum; s >= rank; s--)
                {
                    var add = ways[k - 1, s - rank];
                    if (add != 0) ways[k, s] += add;
                }
            }
        }
        var expected = take * (n + 1);
        var total = 0.0;
        var byDeviation = new SortedDictionary<int, double>();
        for (var s = 0; s <= maxSum; s++)
        {
            var count = ways[take, s];
            if (count == 0) continue;
            total += count;
            var deviation = Math.Abs(s - expected);
            byDeviation[deviation] = byDeviation.GetValueOrDefault(deviation) + count;
        }
        var observedDeviation = Math.Abs(observed - expected);
        var tail = new Dictionary<int, double>();
        var cumulative = 0.0;
        foreach (var deviation in byDeviation.Keys.Reverse())
        {
            cumulative += byDeviation[deviation];
            tail[deviation] = cumulative / total;
        }
        var p = Math.Min(1.0, tail[observedDeviation]);
        var size = byDeviation.Where(kv => tail[kv.Key] <= alpha + 1e-12).Sum(static kv => kv.Value) / total;
        var u = observed / 2.0 - take * (take + 1) / 2.0;
        return new RankTest(p, size, u, true);
    }

    public static double HodgesLehmann(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0) return double.NaN;
        var differences = new List<double>(a.Count * b.Count);
        foreach (var y in b)
            foreach (var x in a)
                differences.Add(y - x);
        differences.Sort();
        return Quantile(differences, 0.5);
    }

    public static double CliffDelta(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0) return double.NaN;
        var balance = 0;
        foreach (var y in b)
            foreach (var x in a)
                balance += y > x ? 1 : y < x ? -1 : 0;
        return (double)balance / (a.Count * b.Count);
    }

    public static SignedRankTest WilcoxonSignedRank(IReadOnlyList<double> differences)
    {
        var nonZero = differences.Where(static d => d != 0 && !double.IsNaN(d)).ToList();
        var zero = differences.Count(static d => d == 0);
        var positive = nonZero.Count(static d => d > 0);
        var negative = nonZero.Count - positive;
        if (nonZero.Count == 0) return new SignedRankTest(1, 0, 0, 0, zero);
        var ordered = nonZero.Select(static d => (Abs: Math.Abs(d), Positive: d > 0)).OrderBy(static x => x.Abs).ToList();
        var n = ordered.Count;
        var doubledRanks = new int[n];
        for (var i = 0; i < n;)
        {
            var j = i;
            while (j + 1 < n && ordered[j + 1].Abs == ordered[i].Abs) j++;
            for (var k = i; k <= j; k++) doubledRanks[k] = i + j + 2;
            i = j + 1;
        }
        var observed = 0;
        for (var i = 0; i < n; i++) if (ordered[i].Positive) observed += doubledRanks[i];
        var maxSum = doubledRanks.Sum();
        var ways = new double[maxSum + 1];
        ways[0] = 1;
        foreach (var rank in doubledRanks)
            for (var s = maxSum; s >= rank; s--)
                ways[s] += ways[s - rank];
        var total = Math.Pow(2, n);
        var expected2 = maxSum;
        var observedDeviation = Math.Abs(2 * observed - expected2);
        var extreme = 0.0;
        for (var s = 0; s <= maxSum; s++)
            if (ways[s] != 0 && Math.Abs(2 * s - expected2) >= observedDeviation) extreme += ways[s];
        return new SignedRankTest(Math.Min(1.0, extreme / total), n, positive, negative, zero);
    }
}
