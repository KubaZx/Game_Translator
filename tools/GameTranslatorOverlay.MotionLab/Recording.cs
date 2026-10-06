using System.Globalization;

internal static class LabDefaults
{
    public const string RecordingsRoot = @"C:\Users\szuch\Codex Projects\GTO Diagnostics\20261006-ruch\nagrania";
    public const string Cache = @"C:\Users\szuch\Codex Projects\GTO Diagnostics\20261005-natywne-spolszczenie\sesja-2026-10-06\private\cache.db";
    public const string Corpus = @"C:\Users\szuch\Codex Projects\GTO Diagnostics\20261005-natywne-spolszczenie\krok2\private\escape-academy.corpus.jsonl";
    public const string Profile = "escape-academy";
    public const string TruthFile = "truth.jsonl";
}

internal sealed record MotionStep(double StartMs, double EndMs, string Label)
{
    public bool IsMotion => !Label.Equals("pauza", StringComparison.OrdinalIgnoreCase)
        && !Label.Equals("koniec", StringComparison.OrdinalIgnoreCase);
}

internal sealed class Recording
{
    private Recording(string directory, IReadOnlyList<RecordedFrame> frames)
    {
        Directory = directory;
        Frames = frames;
    }

    public string Directory { get; }

    public string Name => Path.GetFileName(Directory.TrimEnd('\\', '/'));

    public IReadOnlyList<RecordedFrame> Frames { get; }

    public int Width => Frames[0].W;

    public int Height => Frames[0].H;

    public string FramePath(int index) => Path.Combine(Directory, $"f{index:D5}.jpg");

    public string TruthPath => Path.Combine(Directory, LabDefaults.TruthFile);

    public double RelativeMs(int index) => Frames[index].TMs - Frames[0].TMs;

    public double NominalIntervalMs
    {
        get
        {
            if (Frames.Count < 2) return 100;
            var gaps = new List<double>();
            for (var i = 1; i < Frames.Count; i++) gaps.Add(Frames[i].TMs - Frames[i - 1].TMs);
            gaps.Sort();
            return gaps[gaps.Count / 2];
        }
    }

    public static string Resolve(string nameOrPath)
    {
        if (System.IO.Directory.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        var candidate = Path.Combine(LabDefaults.RecordingsRoot, nameOrPath);
        if (System.IO.Directory.Exists(candidate)) return candidate;
        throw new ArgumentException($"Nie ma nagrania: {nameOrPath}");
    }

    public static Recording Load(string nameOrPath)
    {
        var directory = Resolve(nameOrPath);
        var meta = Path.Combine(directory, "frames.jsonl");
        if (!File.Exists(meta)) throw new ArgumentException($"Brak frames.jsonl w {directory}");
        var frames = Json.ReadLines<RecordedFrame>(meta).OrderBy(static f => f.I).ToList();
        if (frames.Count == 0) throw new ArgumentException($"Puste nagranie: {directory}");
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].I != i) throw new InvalidDataException($"Nieciągła numeracja klatek w {meta} (oczekiwano {i}, jest {frames[i].I}).");
            if (!File.Exists(Path.Combine(directory, $"f{i:D5}.jpg"))) throw new InvalidDataException($"Brak pliku klatki f{i:D5}.jpg.");
            if (frames[i].W != frames[0].W || frames[i].H != frames[0].H) throw new InvalidDataException("Klatki mają różne rozmiary.");
        }
        return new Recording(directory, frames);
    }

    public IReadOnlyList<(TimeSpan Clock, string Label)> LoadStepClock()
    {
        var path = System.IO.Directory.GetParent(Directory.TrimEnd('\\', '/')) is { } parent
            ? Path.Combine(parent.FullName, Name + ".kroki.txt")
            : null;
        if (path is null || !File.Exists(path)) return [];
        var result = new List<(TimeSpan, string)>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            if (!TimeSpan.TryParseExact(line[..space], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var clock)) continue;
            result.Add((clock, line[(space + 1)..].Trim()));
        }
        return result;
    }

    public static IReadOnlyList<MotionStep> PlaceSteps(IReadOnlyList<(TimeSpan Clock, string Label)> steps, double offsetMs, double endMs)
    {
        var result = new List<MotionStep>();
        if (steps.Count == 0) return result;
        var first = steps[0].Clock;
        for (var i = 0; i < steps.Count; i++)
        {
            var start = (steps[i].Clock - first).TotalMilliseconds + offsetMs;
            var end = i + 1 < steps.Count ? (steps[i + 1].Clock - first).TotalMilliseconds + offsetMs : endMs;
            result.Add(new MotionStep(start, Math.Max(start, end), steps[i].Label));
        }
        return result;
    }
}
