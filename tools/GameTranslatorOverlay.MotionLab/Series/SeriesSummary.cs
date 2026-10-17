using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslatorOverlay.MotionLab.Series;

internal sealed record SeriesRun(string Name, int PhaseMs, bool Ok, int ExitCode, double WallSeconds, string? Error);

internal sealed record MetricSummary(
    int N, double? Median, double? Q1, double? Q3, double? Iqr, double? Min, double? Max, double? Mean, IReadOnlyList<double?> Values)
{
    [JsonIgnore]
    public IReadOnlyList<double> Present => Values.Where(static v => v is not null).Select(static v => v!.Value).ToList();
}

internal sealed record LatencyCluster(
    int Events, int NeverShown, double? EventsPerRun, double? Median, double? Q1, double? Q3, double? MedianOfRunMedians,
    IReadOnlyList<int> EventsByRun, IReadOnlyList<double> Values);

internal sealed record LatencyEpisode(
    string Key, string Text, bool Static, bool StartedInMotion, int Runs, int NeverShown, double? Median, double? Min, double? Max,
    IReadOnlyList<bool> Present, IReadOnlyList<double?> Latencies);

internal sealed record SeriesDocument(
    string Type, int Version, string Recording, DateTime Created, string Tool, int PeriodMs, string Phases,
    IReadOnlyList<string> ReplayArgs, string? Truth, IReadOnlyList<SeriesRun> Runs, double TotalSeconds,
    IReadOnlyDictionary<string, MetricSummary> Metrics, IReadOnlyDictionary<string, LatencyCluster> LatencyClusters,
    IReadOnlyList<LatencyEpisode> LatencyEpisodes, string? TruthMode = null, string? ToolBuild = null, string? AnalysisTruth = null,
    bool AnalysisPlain = false)
{
    public const string TypeName = "motionlab-series";
    public const int FormatVersion = 1;
}

internal static class SeriesJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true,
    };

    public static string Format(double? value, string format = "0.##") =>
        value is not { } v || double.IsNaN(v) ? "—" : v.ToString(format, CultureInfo.InvariantCulture);

    public static string Escape(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}

internal static class SeriesAggregator
{
    public static readonly string[] ClusterNames = ["all", "startedInMotion", "startedAtRest"];

    private sealed record Event(string Key, string Text, bool Static, bool Typing, bool InMotion, double? Latency);

    public static MetricSummary Summarize(IReadOnlyList<double?> values)
    {
        var present = values.Where(static v => v is { } x && !double.IsNaN(x)).Select(static v => v!.Value).Order().ToList();
        if (present.Count == 0) return new MetricSummary(0, null, null, null, null, null, null, null, values);
        var q1 = SeriesMath.Quantile(present, 0.25);
        var q3 = SeriesMath.Quantile(present, 0.75);
        return new MetricSummary(present.Count, Round(SeriesMath.Quantile(present, 0.5)), Round(q1), Round(q3), Round(q3 - q1),
            present[0], present[^1], Round(present.Average()), values);
    }

    public static SeriesDocument Build(string recording, DateTime created, string tool, int periodMs, string phases,
        IReadOnlyList<string> replayArgs, IReadOnlyList<(SeriesRun Run, JsonElement? Metrics)> runs, double totalSeconds,
        string? toolBuild = null, string? analysisTruth = null, bool analysisPlain = false)
    {
        var flattened = runs.Select(static r => r.Metrics is { } m ? MetricValues.Flatten(m) : null).ToList();
        var paths = flattened.Where(static f => f is not null).SelectMany(static f => f!.Keys).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var metrics = new SortedDictionary<string, MetricSummary>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var counter = MetricValues.IsCounter(path);
            var values = flattened.Select(f => f is null ? null : f.TryGetValue(path, out var v) ? v : counter ? 0 : null).ToList();
            metrics[path] = Summarize(values);
        }

        var events = runs.Select(static r => r.Metrics is { } m ? EventsOf(m) : null).ToList();
        var clusters = new Dictionary<string, LatencyCluster>(StringComparer.Ordinal);
        foreach (var name in ClusterNames) clusters[name] = Cluster(events, name);

        var keys = events.Where(static e => e is not null).SelectMany(static e => e!).Select(static e => e.Key)
            .Distinct(StringComparer.Ordinal).ToList();
        var episodes = new List<LatencyEpisode>();
        foreach (var key in keys)
        {
            var present = new List<bool>();
            var latencies = new List<double?>();
            Event? sample = null;
            foreach (var run in events)
            {
                var found = run?.FirstOrDefault(e => e.Key == key);
                present.Add(found is not null);
                latencies.Add(found?.Latency);
                sample ??= found;
            }
            var shown = latencies.Where(static l => l is not null).Select(static l => l!.Value).ToList();
            episodes.Add(new LatencyEpisode(key, sample!.Text, sample.Static, sample.InMotion, present.Count(static p => p),
                present.Where((p, i) => p && latencies[i] is null).Count(),
                shown.Count == 0 ? null : Round(SeriesMath.Median(shown)), shown.Count == 0 ? null : shown.Min(),
                shown.Count == 0 ? null : shown.Max(), present, latencies));
        }
        episodes = episodes.OrderBy(static e => FrameOf(e.Key)).ThenBy(static e => e.Key, StringComparer.Ordinal).ToList();

        return new SeriesDocument(SeriesDocument.TypeName, SeriesDocument.FormatVersion, recording, created, tool, periodMs, phases,
            replayArgs, TextOf(runs, "truth"), runs.Select(static r => r.Run).ToList(), Math.Round(totalSeconds, 1), metrics, clusters, episodes,
            TextOf(runs, "truthMode"), toolBuild, analysisTruth, analysisPlain);
    }

    private static string? TextOf(IReadOnlyList<(SeriesRun Run, JsonElement? Metrics)> runs, string name)
    {
        var values = runs.Select(r => r.Metrics is { } m && m.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
            .Where(static t => t is not null).Distinct(StringComparer.Ordinal).ToList();
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => string.Join(" | ", values),
        };
    }

    private static int FrameOf(string key) =>
        int.TryParse(key.AsSpan(0, Math.Max(0, key.IndexOf('|'))), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame) ? frame : int.MaxValue;

    private static LatencyCluster Cluster(IReadOnlyList<List<Event>?> runs, string name)
    {
        bool Selected(Event e) => name switch
        {
            "startedInMotion" => !e.Typing && e.InMotion,
            "startedAtRest" => !e.Typing && !e.InMotion,
            _ => true,
        };
        var values = new List<double>();
        var never = 0;
        var byRun = new List<int>();
        var runMedians = new List<double>();
        var okRuns = 0;
        foreach (var run in runs)
        {
            if (run is null) continue;
            okRuns++;
            var selected = run.Where(Selected).ToList();
            var shown = selected.Where(static e => e.Latency is not null).Select(static e => e.Latency!.Value).ToList();
            never += selected.Count - shown.Count;
            byRun.Add(shown.Count);
            values.AddRange(shown);
            if (shown.Count > 0) runMedians.Add(SeriesMath.Median(shown));
        }
        values.Sort();
        return new LatencyCluster(values.Count, never, okRuns == 0 ? null : Round((double)values.Count / okRuns),
            values.Count == 0 ? null : Round(SeriesMath.Quantile(values, 0.5)),
            values.Count == 0 ? null : Round(SeriesMath.Quantile(values, 0.25)),
            values.Count == 0 ? null : Round(SeriesMath.Quantile(values, 0.75)),
            runMedians.Count == 0 ? null : Round(SeriesMath.Median(runMedians)), byRun, values);
    }

    private static List<Event> EventsOf(JsonElement metrics)
    {
        var result = new List<Event>();
        if (!metrics.TryGetProperty("latency", out var latency) || !latency.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in list.EnumerateArray())
        {
            if (Bool(item, "atStart")) continue;
            var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var frame = item.TryGetProperty("startFrame", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : -1;
            double? value = item.TryGetProperty("latencyMs", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null;
            result.Add(new Event(frame.ToString(CultureInfo.InvariantCulture) + "|" + text, text, Bool(item, "static"), Bool(item, "typing"),
                Bool(item, "startedInMotion"), value));
        }
        return result;
    }

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double Round(double value) => Math.Round(value, 2);

    public static string Markdown(SeriesDocument series)
    {
        var sb = new StringBuilder();
        var ok = series.Runs.Count(static r => r.Ok);
        sb.AppendLine($"# MotionLab — seria {series.Recording}");
        sb.AppendLine();
        sb.AppendLine($"Przebiegów: {series.Runs.Count} (z metrykami: {ok}); fazy startu sesji: {string.Join(", ", series.Runs.Select(static r => r.PhaseMs))} ms " +
            $"({series.Phases}, okres {series.PeriodMs} ms); opcje replay: {(series.ReplayArgs.Count == 0 ? "domyślne" : "`" + string.Join(' ', series.ReplayArgs) + "`")}; " +
            $"czas serii {SeriesJson.Format(series.TotalSeconds / 60.0, "0.0")} min (przebieg średnio {SeriesJson.Format(series.Runs.Count == 0 ? null : series.TotalSeconds / series.Runs.Count, "0")} s); " +
            $"prawda: {series.Truth ?? "?"} ({series.TruthMode ?? "?"}).");
        sb.AppendLine();
        sb.AppendLine("## Kluczowe metryki");
        sb.AppendLine();
        sb.AppendLine("| Metryka | Mediana | IQR (Q1–Q3) | min–max | Przebiegi |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var (path, label) in MetricValues.Key)
        {
            if (!series.Metrics.TryGetValue(path, out var m)) continue;
            sb.AppendLine($"| {label} | {SeriesJson.Format(m.Median)} | {SeriesJson.Format(m.Iqr)} ({SeriesJson.Format(m.Q1)}–{SeriesJson.Format(m.Q3)}) | " +
                $"{SeriesJson.Format(m.Min)}–{SeriesJson.Format(m.Max)} | {string.Join(", ", m.Values.Select(v => SeriesJson.Format(v)))} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Opóźnienia: skupiska (epizody spoza startu sesji)");
        sb.AppendLine();
        sb.AppendLine("| Skupisko | Pokazanych zdarzeń (na przebieg) | Mediana zbiorcza [Q1–Q3] ms | Mediana median przebiegów ms | Nigdy nie pokazane (suma) |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var name in ClusterNames)
        {
            if (!series.LatencyClusters.TryGetValue(name, out var c)) continue;
            sb.AppendLine($"| {ClusterLabel(name)} | {c.Events} ({SeriesJson.Format(c.EventsPerRun)}; {string.Join("/", c.EventsByRun)}) | " +
                $"{SeriesJson.Format(c.Median, "0")} [{SeriesJson.Format(c.Q1, "0")}–{SeriesJson.Format(c.Q3, "0")}] | {SeriesJson.Format(c.MedianOfRunMedians, "0")} | {c.NeverShown} |");
        }
        sb.AppendLine();
        if (series.LatencyEpisodes.Count > 0)
        {
            sb.AppendLine("## Epizody: opóźnienie w kolejnych przebiegach [ms] („—” = nie pokazano, „·” = brak epizodu)");
            sb.AppendLine();
            sb.AppendLine("| Klatka | Tekst | Rodzaj | Mediana | Przebiegi |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var e in series.LatencyEpisodes)
            {
                var cells = e.Latencies.Select((l, i) => e.Present[i] ? SeriesJson.Format(l, "0") : "·");
                sb.AppendLine($"| {FrameOf(e.Key)} | {SeriesJson.Escape(e.Text)} | {(e.Static ? "stały" : "ruchomy")}, {(e.StartedInMotion ? "start w ruchu" : "start w spoczynku")} | " +
                    $"{SeriesJson.Format(e.Median, "0")} | {string.Join(", ", cells)} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Przebiegi");
        sb.AppendLine();
        sb.AppendLine("| Przebieg | Faza [ms] | Czas [s] | Wynik |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var run in series.Runs)
            sb.AppendLine($"| {run.Name} | {run.PhaseMs} | {SeriesJson.Format(run.WallSeconds, "0")} | {(run.Ok ? "ok" : $"BŁĄD (kod {run.ExitCode}{(run.Error is null ? string.Empty : ": " + SeriesJson.Escape(run.Error))})")} |");
        sb.AppendLine();
        sb.AppendLine("## Wszystkie metryki liczbowe");
        sb.AppendLine();
        sb.AppendLine("| Metryka | n | Mediana | IQR | min–max |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var (path, m) in series.Metrics)
            sb.AppendLine($"| {SeriesJson.Escape(path)} | {m.N} | {SeriesJson.Format(m.Median)} | {SeriesJson.Format(m.Iqr)} | {SeriesJson.Format(m.Min)}–{SeriesJson.Format(m.Max)} |");
        return sb.ToString();
    }

    public static string ClusterLabel(string name) => name switch
    {
        "startedInMotion" => "start w ruchu",
        "startedAtRest" => "start w spoczynku",
        _ => "wszystkie",
    };
}
