using System.Text;

namespace GameTranslatorOverlay.MotionLab.Series;

internal sealed record MetricComparison(
    string Path, string? Label, bool Key, int Direction, MetricSummary? A, MetricSummary? B, double? Delta, double? Shift,
    double? Cliff, double? P, double? Size, string Verdict)
{
    public bool Significant => Verdict is SeriesComparison.Better or SeriesComparison.Worse or SeriesComparison.Changed;
}

internal sealed record ClusterComparison(
    string Cluster, double? EventsPerRunA, double? EventsPerRunB, double? MedianA, double? MedianB, double? RunMedianA,
    double? RunMedianB, int NeverShownA, int NeverShownB);

internal sealed record EpisodeComparison(
    string Key, string Text, bool Static, bool StartedInMotion, double? MedianA, double? MedianB, double? Delta,
    int RunsA, int RunsB, int NeverShownA, int NeverShownB);

internal sealed record EpisodePairing(
    int Paired, int FasterInB, int SlowerInB, int Same, double? MedianDelta, double P, IReadOnlyList<EpisodeComparison> Items);

internal sealed record ComparisonDocument(
    string Type, int Version, string A, string B, string RecordingA, string RecordingB, int RunsA, int RunsB, double Alpha,
    int SignificantKey, int ComparedKey, double ExpectedFalseAlarmsKey, int SignificantAll, int ComparedAll,
    double ExpectedFalseAlarmsAll, IReadOnlyList<string> Warnings, IReadOnlyList<MetricComparison> Metrics,
    IReadOnlyList<ClusterComparison> Clusters, EpisodePairing Episodes);

internal static class SeriesComparison
{
    public const string Better = "lepiej";
    public const string Worse = "gorzej";
    public const string Changed = "zmiana";
    public const string WithinSpread = "w rozrzucie";
    public const string Constant = "bez zmian (stała)";
    public const string Undecidable = "nierozstrzygalne";
    public const string Missing = "brak w jednej serii";

    public static MetricComparison CompareMetric(string path, MetricSummary? a, MetricSummary? b, double alpha)
    {
        var label = MetricValues.LabelOf(path);
        var key = label is not null;
        var direction = MetricValues.Direction(path);
        if (a is null || b is null || a.N == 0 || b.N == 0)
            return new MetricComparison(path, label, key, direction, a, b, null, null, null, null, null, Missing);
        var x = a.Present;
        var y = b.Present;
        var delta = Math.Round(b.Median!.Value - a.Median!.Value, 2);
        if (x.Concat(y).Distinct().Count() == 1)
            return new MetricComparison(path, label, key, direction, a, b, 0, 0, 0, 1, 0, Constant);
        var test = SeriesMath.MannWhitney(x, y, alpha);
        var shift = Math.Round(SeriesMath.HodgesLehmann(x, y), 2);
        var cliff = Math.Round(SeriesMath.CliffDelta(x, y), 2);
        string verdict;
        if (test.Size <= 0) verdict = Undecidable;
        else if (test.P > alpha + 1e-12) verdict = WithinSpread;
        else if (direction == 0) verdict = Changed;
        else verdict = Math.Sign(cliff) * direction > 0 ? Better : Worse;
        return new MetricComparison(path, label, key, direction, a, b, delta, shift, cliff, Math.Round(test.P, 4), Math.Round(test.Size, 4), verdict);
    }

    private static MetricSummary? MetricOf(SeriesDocument series, string path) =>
        series.Metrics.GetValueOrDefault(path)
        ?? (MetricValues.IsCounter(path) && series.Metrics.Count > 0
            ? SeriesAggregator.Summarize(Enumerable.Repeat<double?>(0, series.Metrics.Values.Max(static m => m.N)).ToList())
            : null);

    public static ComparisonDocument Compare(SeriesDocument a, SeriesDocument b, string nameA, string nameB, double alpha)
    {
        var paths = a.Metrics.Keys.Union(b.Metrics.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var metrics = paths.Select(p => CompareMetric(p, MetricOf(a, p), MetricOf(b, p), alpha)).ToList();
        var keyOrder = MetricValues.Key.Select(static (k, i) => (k.Path, i)).ToDictionary(static k => k.Path, static k => k.i, StringComparer.Ordinal);
        metrics = metrics.OrderBy(m => keyOrder.TryGetValue(m.Path, out var i) ? i : int.MaxValue).ThenBy(static m => m.Path, StringComparer.Ordinal).ToList();
        var tested = metrics.Where(static m => m.Size is not null).ToList();
        var testedKey = tested.Where(static m => m.Key).ToList();

        var warnings = new List<string>();
        if (a.Recording != b.Recording) warnings.Add($"Różne nagrania: {a.Recording} i {b.Recording}.");
        if (!a.ReplayArgs.SequenceEqual(b.ReplayArgs))
            warnings.Add($"Różne opcje replay: A `{string.Join(' ', a.ReplayArgs)}`, B `{string.Join(' ', b.ReplayArgs)}`.");
        var phasesA = a.Runs.Where(static r => r.Ok).Select(static r => r.PhaseMs).Order().ToList();
        var phasesB = b.Runs.Where(static r => r.Ok).Select(static r => r.PhaseMs).Order().ToList();
        if (!phasesA.SequenceEqual(phasesB))
            warnings.Add($"Różne fazy startu: A {string.Join(", ", phasesA)} ms, B {string.Join(", ", phasesB)} ms.");
        if (!string.Equals(a.Truth, b.Truth, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Różne pliki prawdy: A {a.Truth ?? "?"}, B {b.Truth ?? "?"}.");
        if (a.ToolBuild is not null && b.ToolBuild is not null && !string.Equals(a.ToolBuild, b.ToolBuild, StringComparison.Ordinal))
            warnings.Add($"Różne programy MotionLab: A {a.ToolBuild}, B {b.ToolBuild} (oczekiwane przy porównaniu dwóch wersji kodu).");
        if (!string.Equals(a.TruthMode, b.TruthMode, StringComparison.Ordinal))
            warnings.Add($"Różny tryb prawdy: A {a.TruthMode ?? "?"}, B {b.TruthMode ?? "?"}.");
        if (a.Metrics.GetValueOrDefault("tracks.all")?.Median != b.Metrics.GetValueOrDefault("tracks.all")?.Median)
            warnings.Add("Inna liczba wierszy prawdy (tracks.all) — prawda się różni, porównanie epizodów jest niewiarygodne.");
        var okA = a.Runs.Count(static r => r.Ok);
        var okB = b.Runs.Count(static r => r.Ok);
        if (okA < 5 || okB < 5) warnings.Add($"Mniej niż 5 udanych przebiegów (A {okA}, B {okB}) — test ma małą moc.");

        var clusters = SeriesAggregator.ClusterNames.Select(name =>
        {
            var ca = a.LatencyClusters.GetValueOrDefault(name);
            var cb = b.LatencyClusters.GetValueOrDefault(name);
            return new ClusterComparison(name, ca?.EventsPerRun, cb?.EventsPerRun, ca?.Median, cb?.Median, ca?.MedianOfRunMedians,
                cb?.MedianOfRunMedians, ca?.NeverShown ?? 0, cb?.NeverShown ?? 0);
        }).ToList();

        return new ComparisonDocument("motionlab-compare", 1, nameA, nameB, a.Recording, b.Recording, okA, okB, alpha,
            testedKey.Count(static m => m.Significant), testedKey.Count, Math.Round(testedKey.Sum(static m => m.Size!.Value), 2),
            tested.Count(static m => m.Significant), tested.Count, Math.Round(tested.Sum(static m => m.Size!.Value), 2),
            warnings, metrics, clusters, PairEpisodes(a, b));
    }

    public static EpisodePairing PairEpisodes(SeriesDocument a, SeriesDocument b)
    {
        var byKey = b.LatencyEpisodes.ToDictionary(static e => e.Key, StringComparer.Ordinal);
        var items = new List<EpisodeComparison>();
        foreach (var ea in a.LatencyEpisodes)
        {
            if (!byKey.TryGetValue(ea.Key, out var eb)) continue;
            double? delta = ea.Median is { } ma && eb.Median is { } mb ? Math.Round(mb - ma, 1) : null;
            items.Add(new EpisodeComparison(ea.Key, ea.Text, ea.Static, ea.StartedInMotion, ea.Median, eb.Median, delta,
                ea.Runs, eb.Runs, ea.NeverShown, eb.NeverShown));
        }
        var deltas = items.Where(static i => i.Delta is not null).Select(static i => i.Delta!.Value).ToList();
        var test = SeriesMath.WilcoxonSignedRank(deltas);
        return new EpisodePairing(deltas.Count, deltas.Count(static d => d < 0), deltas.Count(static d => d > 0), deltas.Count(static d => d == 0),
            deltas.Count == 0 ? null : Math.Round(SeriesMath.Median(deltas), 1), Math.Round(test.P, 4), items);
    }

    public static string Markdown(ComparisonDocument doc, bool all)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MotionLab — porównanie serii");
        sb.AppendLine();
        sb.AppendLine($"A: **{SeriesJson.Escape(doc.A)}** ({doc.RecordingA}, przebiegów: {doc.RunsA}), B: **{SeriesJson.Escape(doc.B)}** ({doc.RecordingB}, przebiegów: {doc.RunsB}). " +
            $"Test: dokładny dwustronny Mann–Whitney na wartościach z przebiegów, α = {SeriesJson.Format(doc.Alpha, "0.###")}.");
        sb.AppendLine();
        sb.AppendLine($"**Istotne różnice: {doc.SignificantKey} z {doc.ComparedKey} kluczowych metryk** (przy braku różnic spodziewanych przypadkiem ≈ {SeriesJson.Format(doc.ExpectedFalseAlarmsKey, "0.0#")}); " +
            $"wszystkich metryk: {doc.SignificantAll} z {doc.ComparedAll} (≈ {SeriesJson.Format(doc.ExpectedFalseAlarmsAll, "0.0")} przypadkiem).");
        sb.AppendLine();
        foreach (var warning in doc.Warnings) sb.AppendLine($"- Uwaga: {SeriesJson.Escape(warning)}");
        if (doc.Warnings.Count > 0) sb.AppendLine();

        sb.AppendLine("## Kluczowe metryki");
        sb.AppendLine();
        Table(sb, doc.Metrics.Where(static m => m.Key));
        sb.AppendLine("Δ = mediana B − mediana A; HL = przesunięcie Hodgesa–Lehmanna (mediana różnic par); δ = δ Cliffa (−1…1, + = B większe); " +
            "„w rozrzucie” = różnica nie wychodzi poza rozrzut przebiegów.");
        sb.AppendLine();

        sb.AppendLine("## Opóźnienia: skupiska");
        sb.AppendLine();
        sb.AppendLine("| Skupisko | Pokazanych na przebieg A / B | Mediana zbiorcza A / B [ms] | Mediana median przebiegów A / B [ms] | Nigdy nie pokazane A / B |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in doc.Clusters)
        {
            sb.AppendLine($"| {SeriesAggregator.ClusterLabel(c.Cluster)} | {SeriesJson.Format(c.EventsPerRunA)} / {SeriesJson.Format(c.EventsPerRunB)} | " +
                $"{SeriesJson.Format(c.MedianA, "0")} / {SeriesJson.Format(c.MedianB, "0")} | {SeriesJson.Format(c.RunMedianA, "0")} / {SeriesJson.Format(c.RunMedianB, "0")} | " +
                $"{c.NeverShownA} / {c.NeverShownB} |");
        }
        sb.AppendLine();
        var pairing = doc.Episodes;
        sb.AppendLine("## Epizody sparowane (mediana opóźnienia epizodu z przebiegów serii)");
        sb.AppendLine();
        sb.AppendLine($"Sparowanych: {pairing.Paired}; w B szybciej: {pairing.FasterInB}, wolniej: {pairing.SlowerInB}, bez zmian: {pairing.Same}; " +
            $"mediana różnicy {SeriesJson.Format(pairing.MedianDelta, "0")} ms; dokładny test Wilcoxona (znakowane rangi): p = {SeriesJson.Format(pairing.P, "0.###")}.");
        sb.AppendLine();
        if (pairing.Items.Count > 0)
        {
            sb.AppendLine("| Klatka | Tekst | Rodzaj | A [ms] | B [ms] | Δ [ms] | Nigdy A / B |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var e in pairing.Items)
            {
                sb.AppendLine($"| {e.Key[..Math.Max(0, e.Key.IndexOf('|'))]} | {SeriesJson.Escape(e.Text)} | {(e.Static ? "stały" : "ruchomy")}, {(e.StartedInMotion ? "w ruchu" : "w spoczynku")} | " +
                    $"{SeriesJson.Format(e.MedianA, "0")} | {SeriesJson.Format(e.MedianB, "0")} | {SeriesJson.Format(e.Delta, "0")} | {e.NeverShownA} / {e.NeverShownB} |");
            }
            sb.AppendLine();
        }

        var others = doc.Metrics.Where(static m => !m.Key && m.Significant).ToList();
        sb.AppendLine($"## Pozostałe metryki z istotną różnicą ({others.Count})");
        sb.AppendLine();
        if (others.Count > 0) Table(sb, others);
        else sb.AppendLine("Brak.");
        sb.AppendLine();
        if (all)
        {
            sb.AppendLine("## Wszystkie metryki");
            sb.AppendLine();
            Table(sb, doc.Metrics);
        }
        return sb.ToString();
    }

    private static void Table(StringBuilder sb, IEnumerable<MetricComparison> rows)
    {
        sb.AppendLine("| Metryka | A: mediana [Q1–Q3] | B: mediana [Q1–Q3] | Δ | HL | δ | p | Werdykt |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var m in rows)
        {
            var name = m.Label is null ? SeriesJson.Escape(m.Path) : $"{m.Label} (`{m.Path}`)";
            var verdict = m.Significant ? $"**{m.Verdict}**" : m.Verdict;
            sb.AppendLine($"| {name} | {Cell(m.A)} | {Cell(m.B)} | {SeriesJson.Format(m.Delta)} | {SeriesJson.Format(m.Shift)} | {SeriesJson.Format(m.Cliff)} | " +
                $"{SeriesJson.Format(m.P, "0.####")} | {verdict} |");
        }
        sb.AppendLine();
    }

    private static string Cell(MetricSummary? m) =>
        m is null || m.N == 0 ? "—" : $"{SeriesJson.Format(m.Median)} [{SeriesJson.Format(m.Q1)}–{SeriesJson.Format(m.Q3)}]";
}
