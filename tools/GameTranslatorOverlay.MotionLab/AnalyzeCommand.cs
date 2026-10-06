using System.Diagnostics;
using System.Text.Json;

internal sealed record AnalyzeOptions(string? Truth, int Worst);

internal static class AnalyzeCommand
{
    public static int Run(string outDirectory, AnalyzeOptions options)
    {
        var watch = Stopwatch.StartNew();
        var data = ReplayData.Load(outDirectory);
        var recording = Recording.Load(data.Header.Recording);
        var truthPath = options.Truth ?? recording.TruthPath;
        if (!File.Exists(truthPath))
        {
            Console.WriteLine("analyze: brak prawdy — uruchamiam truth");
            TruthCommand.Run(recording.Directory, new TruthOptions(1, 6, false, data.Header.Corpus, data.Header.Profile));
        }
        var (_, truth) = TruthCommand.Load(truthPath);
        var analyzer = new MotionAnalyzer(data, recording, truth);
        analyzer.Run();
        new PatchAnalyzer(analyzer, Math.Clamp(Environment.ProcessorCount / 4, 2, 8)).Run();

        var worst = PickWorst(analyzer.Problems, options.Worst);
        var sheets = Path.Combine(outDirectory, "sheets");
        Directory.CreateDirectory(sheets);
        foreach (var old in Directory.EnumerateFiles(sheets, "*.jpg")) File.Delete(old);
        var written = new List<(Problem Problem, string Sheet)>();
        for (var i = 0; i < worst.Count; i++)
        {
            written.Add((worst[i], SheetWriter.Write(analyzer, worst[i], i + 1, sheets)));
        }

        var metrics = new Dictionary<string, object?>(analyzer.Metrics, StringComparer.Ordinal)
        {
            ["recording"] = recording.Name,
            ["truth"] = truthPath,
            ["replay"] = data.Summary is { } s
                ? new { s.FramesShown, s.LateSetMedianMs, s.LateSetP90Ms, s.LateSetMaxMs, s.LateRenderMedianMs, s.DecodeStalls, s.WallSeconds, s.Error, s.CompositesDropped }
                : null,
            ["worst"] = written.Select(static w => new
            {
                w.Problem.Type,
                startMs = Json.R(w.Problem.StartMs),
                endMs = Json.R(w.Problem.EndMs),
                w.Problem.Frame,
                score = Math.Round(w.Problem.Score, 2),
                w.Problem.Text,
                w.Problem.Detail,
                sheet = Path.GetFileName(w.Sheet),
            }).ToList(),
            ["problemsByType"] = analyzer.Problems.GroupBy(static p => p.Type)
                .ToDictionary(static g => g.Key, static g => new { count = g.Count(), seconds = Math.Round(g.Sum(static p => p.EndMs - p.StartMs) / 1000, 1) }),
        };
        var metricsPath = Path.Combine(outDirectory, "metrics.json");
        Json.Write(metricsPath, metrics);
        var element = JsonSerializer.SerializeToElement(metrics, Json.Options);
        ReportWriter.Write(Path.Combine(outDirectory, "RAPORT.md"), analyzer, element, written);
        Console.WriteLine($"analyze: {recording.Name} — pokrycie {ReportWriter.Value(element, "coverage.all.percent", " %")} " +
            $"(HUD {ReportWriter.Value(element, "coverage.static.percent", " %")}, ruchome {ReportWriter.Value(element, "coverage.moving.percent", " %")}), " +
            $"nieaktualne {ReportWriter.Value(element, "stale.seconds", " s")}, opóźnienie mediana {ReportWriter.Value(element, "latency.nonTyping.median", " ms")}, " +
            $"{written.Count} stykówek, {watch.Elapsed.TotalSeconds:0} s → {Path.Combine(outDirectory, "RAPORT.md")}");
        return 0;
    }

    private static List<Problem> PickWorst(IReadOnlyList<Problem> problems, int count)
    {
        var picked = new List<Problem>();
        foreach (var problem in problems.OrderByDescending(static p => p.Score))
        {
            var group = problem.Type.Split(' ')[0];
            var duplicate = picked.Any(p => p.Type.Split(' ')[0] == group && p.Subject == problem.Subject
                && p.StartMs - 500 < problem.EndMs && problem.StartMs < p.EndMs + 500);
            var sameMoment = picked.Any(p => p.Type == problem.Type && Math.Abs(p.Frame - problem.Frame) <= 3);
            if (duplicate || sameMoment) continue;
            picked.Add(problem);
            if (picked.Count >= count) break;
        }
        return picked;
    }
}
