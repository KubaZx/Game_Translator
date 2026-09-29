using System.Globalization;

namespace GameTranslatorOverlay.Core.Usage;

/// <summary>Etapy mierzone w działającej aplikacji.</summary>
public enum LatencyStage
{
    /// <summary>Przechwycenie klatki okna gry albo regionu.</summary>
    Capture,

    /// <summary>Rozpoznanie tekstu przez Windows OCR.</summary>
    Ocr,

    /// <summary>Jedno udane zapytanie do dostawcy tłumaczeń (sieć + czas po stronie dostawcy).</summary>
    Provider,

    /// <summary>Od przechwycenia klatki do gotowego napisu, gdy cały tekst był już znany (cache, słownik).</summary>
    KnownText,

    /// <summary>Od przechwycenia klatki do gotowego napisu, gdy trzeba było zapytać dostawcę.</summary>
    NewText,
}

public sealed record LatencySummary(int Count, double MedianMs, double P90Ms, double MaxMs, double LastMs);

/// <summary>
/// Czasy ostatnich operacji w tej sesji aplikacji: mediana, 90. percentyl i maksimum dla
/// każdego etapu z okna ostatnich próbek. Tylko liczby — bez tekstu i obrazów, nic nie
/// trafia na dysk. Bezpieczny wątkowo; zapis próbki to kilka instrukcji pod blokadą.
/// </summary>
public sealed class LatencyMonitor
{
    public const int DefaultWindow = 200;

    private readonly Lock _gate = new();
    private readonly int _window;
    private readonly Dictionary<LatencyStage, Queue<double>> _samples = [];
    private readonly Dictionary<LatencyStage, double> _last = [];

    public LatencyMonitor(int window = DefaultWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        _window = window;
    }

    public void Record(LatencyStage stage, double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            if (!_samples.TryGetValue(stage, out var queue))
            {
                queue = new Queue<double>(Math.Min(_window, 64));
                _samples[stage] = queue;
            }
            if (queue.Count == _window) queue.Dequeue();
            queue.Enqueue(milliseconds);
            _last[stage] = milliseconds;
        }
    }

    public LatencySummary? Summarize(LatencyStage stage)
    {
        double[] values;
        double last;
        lock (_gate)
        {
            if (!_samples.TryGetValue(stage, out var queue) || queue.Count == 0) return null;
            values = queue.ToArray();
            last = _last[stage];
        }

        Array.Sort(values);
        return new LatencySummary(values.Length, Percentile(values, 0.5), Percentile(values, 0.9), values[^1], last);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _last.Clear();
        }
    }

    /// <summary>Percentyl z interpolacją liniową między sąsiednimi próbkami (posortowane rosnąco).</summary>
    internal static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    /// <summary>
    /// Krótki opis dla okna aplikacji, np.
    /// „Nowy tekst: 0,82 s (p90 1,4 s) • Znany: 0,21 s • DeepL: 0,52 s (p90 0,9 s) • OCR: 95 ms • Klatka: 31 ms”.
    /// Pusty napis, gdy nic jeszcze nie zmierzono.
    /// </summary>
    public string Describe(string providerName)
    {
        var parts = new List<string>();
        Add(parts, "Nowy tekst", Summarize(LatencyStage.NewText), withP90: true);
        Add(parts, "Znany", Summarize(LatencyStage.KnownText), withP90: false);
        Add(parts, providerName, Summarize(LatencyStage.Provider), withP90: true);
        Add(parts, "OCR", Summarize(LatencyStage.Ocr), withP90: false);
        Add(parts, "Klatka", Summarize(LatencyStage.Capture), withP90: false);
        return string.Join(" • ", parts);
    }

    /// <summary>
    /// Szczegółowy raport do skopiowania (np. do zgłoszenia albo README): liczba próbek,
    /// mediana, p90 i maksimum dla każdego zmierzonego etapu. Bez tekstu z gry.
    /// </summary>
    public string Report(string providerName)
    {
        var lines = new List<string> { $"GameTranslatorOverlay — czasy (ostatnie {_window} próbek na etap)" };
        foreach (var (stage, label) in StageLabels(providerName))
        {
            if (Summarize(stage) is not { } s) continue;
            lines.Add($"{label}: mediana {FormatMs(s.MedianMs)}, p90 {FormatMs(s.P90Ms)}, max {FormatMs(s.MaxMs)} ({s.Count} pomiarów)");
        }
        return lines.Count == 1 ? string.Empty : string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<(LatencyStage Stage, string Label)> StageLabels(string providerName) =>
    [
        (LatencyStage.NewText, "Nowy tekst (klatka → napis)"),
        (LatencyStage.KnownText, "Znany tekst (klatka → napis)"),
        (LatencyStage.Provider, $"Odpowiedź {providerName}"),
        (LatencyStage.Ocr, "OCR"),
        (LatencyStage.Capture, "Przechwycenie klatki"),
    ];

    private static void Add(List<string> parts, string label, LatencySummary? summary, bool withP90)
    {
        if (summary is null) return;
        var text = $"{label}: {FormatMs(summary.MedianMs)}";
        if (withP90 && summary.Count >= 5) text += $" (p90 {FormatMs(summary.P90Ms)})";
        parts.Add(text);
    }

    /// <summary>Poniżej sekundy w milisekundach, powyżej w sekundach z jednym–dwoma miejscami.</summary>
    public static string FormatMs(double milliseconds)
    {
        var culture = CultureInfo.GetCultureInfo("pl-PL");
        if (milliseconds < 1000) return $"{Math.Round(milliseconds).ToString("0", culture)} ms";
        var seconds = milliseconds / 1000.0;
        return $"{seconds.ToString(seconds < 10 ? "0.##" : "0.#", culture)} s";
    }
}
