using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.MotionLab.Series;

internal sealed record SeriesOptions(
    string Out, int Runs, IReadOnlyList<int>? Phases, int PeriodMs, bool Reanalyze, string? Truth, double TimeoutMinutes,
    IReadOnlyList<string> ReplayArgs, bool PlainTruth = false)
{
    public AnalyzeOptions Analysis => new(Truth, 15, PlainTruth);

    public bool CustomTruth => Truth is not null || PlainTruth;
}

internal static class SeriesCommand
{
    public static int Run(string recordingName, SeriesOptions options)
    {
        var recording = Recording.Load(recordingName);
        var phases = options.Phases ?? SeriesMath.GoldenPhases(options.Runs, options.PeriodMs);
        var duration = recording.RelativeMs(recording.Frames.Count - 1);
        if (phases.Any(p => p > 0 && p >= duration)) throw new ArgumentException($"Faza startu dłuższa niż nagranie ({duration:0} ms).");
        if (options.Truth is not null && !File.Exists(options.Truth)) throw new ArgumentException($"--truth: nie ma pliku {options.Truth}");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Nie znam ścieżki MotionLab.exe.");
        var toolBuild = $"{exe} ({File.GetLastWriteTimeUtc(exe).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC)";
        Directory.CreateDirectory(options.Out);
        var seriesPath = Path.Combine(options.Out, "series.json");
        SeriesDocument? previous = null;
        if (File.Exists(seriesPath))
        {
            previous = JsonSerializer.Deserialize<SeriesDocument>(File.ReadAllText(seriesPath), SeriesJson.Options);
            if (previous is not null && (previous.Recording != recording.Name || !previous.ReplayArgs.SequenceEqual(options.ReplayArgs)))
                throw new ArgumentException($"{seriesPath}: seria ma inne nagranie albo inne opcje replay — podaj nowy katalog.");
            if (previous?.ToolBuild is { } earlier && earlier != toolBuild)
                throw new ArgumentException($"{seriesPath}: seria powstała innym programem ({earlier}) — dokładanie przebiegów innym kodem pomieszałoby wyniki; podaj nowy katalog.");
        }
        var reanalyze = options.Reanalyze
            || (previous is not null && (previous.AnalysisTruth != options.Truth || previous.AnalysisPlain != options.PlainTruth));
        if (reanalyze && !options.Reanalyze) Console.WriteLine("series: inna prawda niż w zapisanej serii — liczę metryki gotowych przebiegów od nowa.");

        var phasesLabel = options.Phases is null ? "auto: złoty podział okresu" : "lista";
        var tool = typeof(SeriesCommand).Assembly.GetName().Version?.ToString() ?? "?";
        var created = previous?.Created ?? DateTime.Now;
        Console.WriteLine($"series: {recording.Name} — {phases.Count} przebiegów kolejno, fazy {string.Join(", ", phases)} ms → {options.Out}");
        var runs = new List<(SeriesRun Run, JsonElement? Metrics)>();
        var total = 0.0;
        for (var k = 0; k < phases.Count; k++)
        {
            var phase = phases[k];
            var name = $"run{k + 1:D2}-faza{phase:D3}ms";
            var directory = Path.Combine(options.Out, name);
            SeriesRun run;
            if (IsComplete(directory, phase, recording.Name))
            {
                Console.WriteLine($"series: [{k + 1}/{phases.Count}] {name} — gotowy przebieg{(reanalyze ? ", liczę metryki od nowa" : string.Empty)}");
                var analyzed = !reanalyze || AnalyzeCommand.Run(directory, options.Analysis) == 0;
                var wall = previous?.Runs.FirstOrDefault(r => r.Name == name)?.WallSeconds ?? WallSecondsOf(directory);
                run = new SeriesRun(name, phase, analyzed, analyzed ? 0 : 3, wall, analyzed ? null : "analyze nie powiodło się");
            }
            else
            {
                if (Directory.Exists(directory))
                {
                    var aside = directory + ".niepelny-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    Directory.Move(directory, aside);
                    Console.WriteLine($"series: niepełny przebieg przeniesiony do {aside}");
                }
                if (ReplayCommand.DesktopProblem() is { } problem)
                {
                    Console.Error.WriteLine($"series: przerywam przed {name} — {problem}; ponowne uruchomienie serii dokończy brakujące przebiegi.");
                    return ReplayCommand.DesktopUnavailableExit;
                }
                run = RunReplay(exe, recording, directory, phase, options, $"{k + 1}/{phases.Count}", duration);
                if (run.ExitCode == ReplayCommand.DesktopUnavailableExit)
                {
                    if (Directory.Exists(directory))
                        Directory.Move(directory, directory + ".niepelny-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                    Console.Error.WriteLine("series: pulpit niedostępny — przerywam serię; ponowne uruchomienie dokończy brakujące przebiegi.");
                    return ReplayCommand.DesktopUnavailableExit;
                }
                if (run.Ok && options.CustomTruth && AnalyzeCommand.Run(directory, options.Analysis) != 0)
                    run = run with { Ok = false, ExitCode = 3, Error = "analyze z --truth nie powiodło się" };
            }
            total += run.WallSeconds;
            var metricsPath = Path.Combine(directory, "metrics.json");
            JsonElement? metrics = null;
            if (run.Ok && File.Exists(metricsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metricsPath));
                metrics = document.RootElement.Clone();
            }
            runs.Add((run with { Ok = metrics is not null }, metrics));
            Save(options.Out, SeriesAggregator.Build(recording.Name, created, tool, options.PeriodMs, phasesLabel, options.ReplayArgs, runs, total,
                toolBuild, options.Truth, options.PlainTruth));
        }

        var series = SeriesAggregator.Build(recording.Name, created, tool, options.PeriodMs, phasesLabel, options.ReplayArgs, runs, total,
            toolBuild, options.Truth, options.PlainTruth);
        Save(options.Out, series);
        Console.WriteLine($"series: koniec — {series.Runs.Count(static r => r.Ok)} z {series.Runs.Count} przebiegów z metrykami, {total / 60:0.0} min → {Path.Combine(options.Out, "series.md")}");
        foreach (var (path, label) in MetricValues.Key)
        {
            if (!series.Metrics.TryGetValue(path, out var m)) continue;
            Console.WriteLine($"  {label}: mediana {SeriesJson.Format(m.Median)} (IQR {SeriesJson.Format(m.Iqr)}, {SeriesJson.Format(m.Min)}–{SeriesJson.Format(m.Max)})");
        }
        return series.Runs.All(static r => r.Ok) ? 0 : 4;
    }

    private static void Save(string directory, SeriesDocument series)
    {
        File.WriteAllText(Path.Combine(directory, "series.json"), JsonSerializer.Serialize(series, SeriesJson.Options), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "series.md"), SeriesAggregator.Markdown(series), new UTF8Encoding(false));
    }

    private static bool IsComplete(string directory, int phase, string recordingName)
    {
        if (!File.Exists(Path.Combine(directory, "metrics.json")) || !File.Exists(Path.Combine(directory, "replay.json"))) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "replay.json")));
            if (!document.RootElement.TryGetProperty("header", out var header)) return false;
            var recorded = header.TryGetProperty("phaseMs", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
            var name = header.TryGetProperty("recordingName", out var n) ? n.GetString() : null;
            return recorded == phase && name == recordingName;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double WallSecondsOf(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "replay.json")));
            return document.RootElement.TryGetProperty("wallSeconds", out var wall) && wall.ValueKind == JsonValueKind.Number ? wall.GetDouble() : 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return 0;
        }
    }

    private static SeriesRun RunReplay(string exe, Recording recording, string directory, int phase, SeriesOptions options, string progress, double durationMs)
    {
        var name = Path.GetFileName(directory);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "replay", recording.Directory, "--out", directory, "--phase-ms", phase.ToString(CultureInfo.InvariantCulture) }
            .Concat(options.ReplayArgs))
            start.ArgumentList.Add(argument);
        var speedAt = options.ReplayArgs.ToList().IndexOf("--speed");
        var speed = speedAt >= 0 && speedAt + 1 < options.ReplayArgs.Count
            && double.TryParse(options.ReplayArgs[speedAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 1.0;
        var timeout = options.TimeoutMinutes > 0
            ? TimeSpan.FromMinutes(options.TimeoutMinutes)
            : TimeSpan.FromMilliseconds(durationMs / speed * 3 + 600_000);
        Console.WriteLine($"series: [{progress}] {name} — replay z fazą {phase} ms");
        var watch = Stopwatch.StartNew();
        using var log = new StreamWriter(directory + ".log", false, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new Lock();
        void Line(string? text, bool error)
        {
            if (text is null) return;
            lock (gate)
            {
                log.WriteLine(error ? "! " + text : text);
                if (error) Console.Error.WriteLine($"  [{progress}] {text}");
                else Console.WriteLine($"  [{progress}] {text}");
            }
        }
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Line(e.Data, false);
        process.ErrorDataReceived += (_, e) => Line(e.Data, true);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        string? error = null;
        if (!process.WaitForExit(timeout))
        {
            error = $"przekroczony limit czasu {timeout.TotalMinutes:0} min — przebieg przerwany";
            process.Kill(entireProcessTree: true);
        }
        process.WaitForExit();
        var exitCode = process.ExitCode;
        if (ReplayCommand.DesktopProblem() is { } lost)
        {
            error = $"pulpit niedostępny w trakcie przebiegu — {lost}";
            exitCode = ReplayCommand.DesktopUnavailableExit;
        }
        var seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        var ok = error is null && exitCode == 0 && File.Exists(Path.Combine(directory, "metrics.json"));
        if (!ok && error is null) error = $"replay zakończył się kodem {exitCode}";
        Console.WriteLine($"series: [{progress}] {name} — {(ok ? "ok" : "BŁĄD: " + error)}, {seconds:0} s");
        return new SeriesRun(name, phase, ok, exitCode, seconds, error);
    }
}

internal static class CompareCommand
{
    public static int Run(string a, string b, string? output, double alpha, bool all)
    {
        var (seriesA, nameA, _) = Load(a);
        var (seriesB, nameB, directoryB) = Load(b);
        var comparison = SeriesComparison.Compare(seriesA, seriesB, nameA, nameB, alpha);
        var markdown = output ?? Path.Combine(directoryB, $"compare-{string.Concat(nameA.Select(static c => Path.GetInvalidFileNameChars().Contains(c) || c == '\\' ? '_' : c))}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdown)!);
        File.WriteAllText(markdown, SeriesComparison.Markdown(comparison, all), new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(markdown, ".json"), JsonSerializer.Serialize(comparison, SeriesJson.Options), new UTF8Encoding(false));
        Console.WriteLine($"compare: {nameB} względem {nameA} — istotne różnice: {comparison.SignificantKey} z {comparison.ComparedKey} kluczowych " +
            $"(przypadkiem ≈ {comparison.ExpectedFalseAlarmsKey:0.0#}), wszystkich {comparison.SignificantAll} z {comparison.ComparedAll} (≈ {comparison.ExpectedFalseAlarmsAll:0.0}) → {markdown}");
        foreach (var warning in comparison.Warnings) Console.WriteLine($"  uwaga: {warning}");
        foreach (var metric in comparison.Metrics.Where(static m => m.Key))
        {
            Console.WriteLine($"  {metric.Label}: {SeriesJson.Format(metric.A?.Median)} → {SeriesJson.Format(metric.B?.Median)} " +
                $"(Δ {SeriesJson.Format(metric.Delta)}, p {SeriesJson.Format(metric.P, "0.###")}) {metric.Verdict}");
        }
        return 0;
    }

    private static (SeriesDocument Series, string Name, string Directory) Load(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "series.json") : path;
        if (!File.Exists(file)) throw new ArgumentException($"Nie ma series.json: {file}");
        var series = JsonSerializer.Deserialize<SeriesDocument>(File.ReadAllText(file), SeriesJson.Options)
            ?? throw new InvalidDataException($"Pusty series.json: {file}");
        if (series.Type != SeriesDocument.TypeName) throw new ArgumentException($"To nie jest plik serii MotionLab: {file}");
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var parent = Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty);
        var name = string.IsNullOrEmpty(parent) ? Path.GetFileName(directory) : parent + "/" + Path.GetFileName(directory);
        return (series, name, directory);
    }
}
