using System.Text;

namespace GameTranslatorOverlay.Core.Evaluation;

/// <summary>
/// Statystyki chrF dla jednego rzędu n-gramów: ile n-gramów ma hipoteza, ile referencja
/// i ile się pokrywa (minimum liczności każdego n-gramu).
/// </summary>
public readonly record struct ChrFOrderStats(long Hypothesis, long Reference, long Matched);

/// <summary>
/// Zliczone statystyki chrF dla jednej linii albo sumy linii (korpusu). Sumowanie statystyk
/// zamiast uśredniania wyników linii to zachowanie sacreBLEU — krótkie linie („OK”) nie
/// ważą wtedy tyle samo co długi dialog.
/// </summary>
public sealed class ChrFStatistics
{
    private readonly ChrFOrderStats[] _orders;

    public ChrFStatistics(int charOrder)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(charOrder, 1);
        _orders = new ChrFOrderStats[charOrder];
    }

    public int CharOrder => _orders.Length;
    public IReadOnlyList<ChrFOrderStats> Orders => _orders;

    internal void Set(int orderIndex, ChrFOrderStats stats) => _orders[orderIndex] = stats;

    public void Add(ChrFStatistics other)
    {
        if (other.CharOrder != CharOrder)
            throw new ArgumentException("Nie można sumować statystyk chrF o różnych rzędach n-gramów.", nameof(other));
        for (var i = 0; i < _orders.Length; i++)
        {
            var a = _orders[i];
            var b = other._orders[i];
            _orders[i] = new ChrFOrderStats(a.Hypothesis + b.Hypothesis, a.Reference + b.Reference, a.Matched + b.Matched);
        }
    }
}

/// <summary>
/// chrF (Popović 2015) w wariancie domyślnym sacreBLEU: n-gramy znakowe 1..6, bez białych
/// znaków, precyzja i pełność uśredniane po rzędach, które wystąpiły w obu tekstach, potem
/// F-beta (beta = 2, pełność ważniejsza). Wynik 0..100. Działa na znakach, więc odmiana
/// polskich słów („tarcza”/„tarczy”) jest częściowo nagradzana — dlatego chrF, a nie BLEU.
/// Jedna referencja karze poprawne parafrazy: liczby służą do porównań względnych
/// (dostawca A vs B, prompt A vs B) na tym samym korpusie, nie jako ocena bezwzględna.
/// </summary>
public static class ChrF
{
    public const int DefaultCharOrder = 6;
    public const double DefaultBeta = 2.0;

    /// <summary>chrF jednej linii (0..100).</summary>
    public static double Score(string? hypothesis, string? reference, int charOrder = DefaultCharOrder, double beta = DefaultBeta) =>
        FScore(Statistics(hypothesis, reference, charOrder), beta);

    /// <summary>
    /// chrF korpusu: statystyki wszystkich par są sumowane, a F-beta liczone raz z sum
    /// (jak <c>sacrebleu.corpus_chrf</c>), a nie jako średnia wyników linii.
    /// </summary>
    public static double CorpusScore(
        IEnumerable<(string? Hypothesis, string? Reference)> pairs, int charOrder = DefaultCharOrder, double beta = DefaultBeta)
    {
        var total = new ChrFStatistics(charOrder);
        foreach (var (hypothesis, reference) in pairs)
        {
            total.Add(Statistics(hypothesis, reference, charOrder));
        }
        return FScore(total, beta);
    }

    public static ChrFStatistics Statistics(string? hypothesis, string? reference, int charOrder = DefaultCharOrder)
    {
        var stats = new ChrFStatistics(charOrder);
        var hyp = Prepare(hypothesis);
        var reff = Prepare(reference);

        for (var n = 1; n <= charOrder; n++)
        {
            var hypGrams = CountNGrams(hyp, n);
            var refGrams = CountNGrams(reff, n);
            long matched = 0;
            foreach (var (gram, count) in hypGrams)
            {
                if (refGrams.TryGetValue(gram, out var refCount)) matched += Math.Min(count, refCount);
            }
            stats.Set(n - 1, new ChrFOrderStats(
                Math.Max(0, hyp.Length - n + 1), Math.Max(0, reff.Length - n + 1), matched));
        }
        return stats;
    }

    /// <summary>
    /// F-beta z uśrednionych precyzji i pełności. Rzędy, których nie ma w hipotezie albo
    /// w referencji (np. 6-gramy w „OK”), są pomijane, żeby krótkie teksty nie dostawały
    /// kary za samą długość. Brak wspólnego rzędu (np. pusta hipoteza) → 0.
    /// </summary>
    public static double FScore(ChrFStatistics stats, double beta = DefaultBeta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(beta);
        double precisionSum = 0, recallSum = 0;
        var effectiveOrder = 0;
        foreach (var order in stats.Orders)
        {
            if (order.Hypothesis <= 0 || order.Reference <= 0) continue;
            precisionSum += (double)order.Matched / order.Hypothesis;
            recallSum += (double)order.Matched / order.Reference;
            effectiveOrder++;
        }
        if (effectiveOrder == 0) return 0;

        var precision = precisionSum / effectiveOrder;
        var recall = recallSum / effectiveOrder;
        var factor = beta * beta;
        var denominator = factor * precision + recall;
        return denominator <= 0 ? 0 : 100.0 * (1 + factor) * precision * recall / denominator;
    }

    // Jak sacreBLEU: usuwamy wszystkie białe znaki (także podziały wierszy z OCR), więc
    // różnica „find\nthe” vs „find the” nie wpływa na wynik. NFC — „ą” z OCR bywa
    // rozłożone na „a” + ogonek, a to nadal ta sama litera.
    private static string Prepare(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    private static Dictionary<string, int> CountNGrams(string text, int n)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + n <= text.Length; i++)
        {
            var gram = text.Substring(i, n);
            counts[gram] = counts.TryGetValue(gram, out var c) ? c + 1 : 1;
        }
        return counts;
    }
}
