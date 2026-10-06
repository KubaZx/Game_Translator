using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;

namespace GameTranslatorOverlay.CorpusEval;

internal sealed record PrefixProbe(string Set, string Reading, string TruthKey, int Letters, int Words);

internal sealed class PrefixStats
{
    public int Probes { get; set; }
    public int Correct { get; set; }
    public int CorrectViaPrefix { get; set; }
    public int Wrong { get; set; }
    public int WrongViaPrefix { get; set; }
    public int None { get; set; }
    public double CorrectPct => EvalData.Pct(Correct, Probes);
    public double WrongOfSnappedPct => EvalData.Pct(Wrong, Correct + Wrong);
}

internal sealed record PrefixConfig(int MinLetters, int MinWords, double ErrorRate, double Margin)
{
    public CorpusSnapOptions Options => CorpusSnapOptions.Default with
    {
        MinPrefixLetters = MinLetters,
        MinPrefixWords = MinWords,
        PrefixErrorRate = ErrorRate,
        PrefixMargin = Margin,
    };

    public string Label => string.Create(CultureInfo.InvariantCulture, $"L={MinLetters} W={MinWords} e={ErrorRate:0.00} m={Margin:0.0}");
}

internal static class PrefixCommand
{
    private static readonly int[] Cuts = [6, 8, 10, 12, 14, 16, 18, 20, 25, 30, 40, 50, 60, 80];

    public static int Run(EvalArgs args)
    {
        var corpus = CorpusJsonl.ReadFile(args.Required("corpus"));
        var samples = EvalData.ReadSamples(args.Required("ocr"));
        var cache = EvalData.ReadCache(args.Required("cache"));
        var outDir = args.Required("out");
        var privateDir = args.Required("private");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(privateDir);

        var index = CorpusIndex.Build(corpus);
        var spokenKeys = corpus
            .Where(static e => e.Kind is CorpusEntryKind.Dialog or CorpusEntryKind.Subtitle)
            .Select(static e => CorpusText.MatchKey(e.En))
            .Where(static k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var probes = new List<PrefixProbe>();
        foreach (var sample in samples.Where(static s => s.Kind is "dialog" or "subtitle"))
        {
            var reading = string.Join(' ', sample.Blocks.Where(static b => b.OverlapsText).Select(static b => b.Text.Replace('\n', ' ')));
            probes.AddRange(CutsOf("ocr", reading, CorpusText.MatchKey(sample.Truth)));
        }
        foreach (var key in spokenKeys) probes.AddRange(CutsOf("korpus", key, key));
        foreach (var block in cache.Where(static b => b.IsPoeSession))
        {
            var text = block.Text.Replace('\n', ' ');
            probes.Add(Probe("poe2-calosc", text, string.Empty));
            probes.AddRange(CutsOf("poe2-uciete", text, string.Empty));
        }

        var configs = new List<PrefixConfig>();
        foreach (var minLetters in new[] { 8, 10, 12, 15, 20 })
            foreach (var minWords in new[] { 2, 3, 4 })
                configs.Add(new PrefixConfig(minLetters, minWords, 0.10, 1.0));
        foreach (var rate in new[] { 0.06, 0.10, 0.14 })
            foreach (var margin in new[] { 0.5, 1.0, 2.0 })
                configs.Add(new PrefixConfig(12, 3, rate, margin));
        configs = configs.Distinct().ToList();
        if (args.Optional("only") is { } only)
        {
            var parts = only.Split(',').Select(static p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            configs = [new PrefixConfig((int)parts[0], (int)parts[1], parts[2], parts[3])];
        }

        var results = new List<object>();
        var markdown = new StringBuilder();
        markdown.AppendLine("# Progi dialogu pisanego literami (przyciąganie początku linii do pełnej linii korpusu)");
        markdown.AppendLine();
        markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Sondy: ucięte odczyty OCR linii dialogu ({probes.Count(static p => p.Set == "ocr")}), ucięte linie korpusu bez błędów ({probes.Count(static p => p.Set == "korpus")}), bloki PoE2 w całości i ucięte ({probes.Count(static p => p.Set.StartsWith("poe2", StringComparison.Ordinal))}). Cięcie po k znakach (litery i cyfry). Liczby bez tekstów gry."));
        markdown.AppendLine();
        markdown.AppendLine("| progi | OCR: poprawne % | OCR: błędne (z przyciągniętych) | korpus: poprawne % | korpus: błędne | PoE2: przyciągnięte |");
        markdown.AppendLine("|---|---|---|---|---|---|");
        var wrongRows = new List<string>();
        foreach (var config in configs)
        {
            var snapper = new CorpusSnapper(index, config.Options);
            var bySet = new Dictionary<string, PrefixStats>(StringComparer.Ordinal);
            var byCut = new Dictionary<string, PrefixStats>(StringComparer.Ordinal);
            foreach (var probe in probes)
            {
                var set = probe.Set.StartsWith("poe2", StringComparison.Ordinal) ? "poe2" : probe.Set;
                var stats = bySet.TryGetValue(set, out var existing) ? existing : bySet[set] = new PrefixStats();
                var cutKey = $"{set}:{Bucket(probe.Letters)}";
                var cutStats = byCut.TryGetValue(cutKey, out var existingCut) ? existingCut : byCut[cutKey] = new PrefixStats();
                var outcome = Classify(snapper, probe);
                foreach (var target in new[] { stats, cutStats })
                {
                    target.Probes++;
                    switch (outcome.Result)
                    {
                        case "poprawne":
                            target.Correct++;
                            if (outcome.ViaPrefix) target.CorrectViaPrefix++;
                            break;
                        case "bledne":
                            target.Wrong++;
                            if (outcome.ViaPrefix) target.WrongViaPrefix++;
                            break;
                        default:
                            target.None++;
                            break;
                    }
                }
                if (outcome.Result == "bledne" && configs.Count == 1)
                {
                    wrongRows.Add(string.Join('\t', probe.Set, probe.Letters, probe.Words, OneLine(probe.Reading), OneLine(probe.TruthKey), OneLine(outcome.Detail)));
                }
            }
            var ocr = bySet.GetValueOrDefault("ocr") ?? new PrefixStats();
            var clean = bySet.GetValueOrDefault("korpus") ?? new PrefixStats();
            var poe = bySet.GetValueOrDefault("poe2") ?? new PrefixStats();
            markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {config.Label} | {ocr.CorrectPct} ({ocr.Correct}/{ocr.Probes}, prefiks {ocr.CorrectViaPrefix}) | {ocr.Wrong} ({ocr.WrongOfSnappedPct}%, prefiks {ocr.WrongViaPrefix}) | {clean.CorrectPct} ({clean.Correct}/{clean.Probes}, prefiks {clean.CorrectViaPrefix}) | {clean.Wrong} ({clean.WrongOfSnappedPct}%, prefiks {clean.WrongViaPrefix}) | {poe.Correct + poe.Wrong} (prefiks {poe.WrongViaPrefix}) z {poe.Probes} |"));
            results.Add(new { config, bySet, byCut });
        }

        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(outDir, "prefiksy.json"), JsonSerializer.Serialize(results, json));
        File.WriteAllText(Path.Combine(outDir, "prefiksy.md"), markdown.ToString());
        if (configs.Count == 1) File.WriteAllLines(Path.Combine(privateDir, "prefiksy-bledne.tsv"), wrongRows, new UTF8Encoding(false));
        Console.WriteLine(markdown.ToString());
        return 0;
    }

    private static IEnumerable<PrefixProbe> CutsOf(string set, string reading, string truthKey)
    {
        var total = CorpusText.LetterOrDigitCount(reading);
        foreach (var cut in Cuts)
        {
            if (cut >= total - 3) yield break;
            var seen = 0;
            var end = 0;
            while (end < reading.Length && seen < cut)
            {
                if (char.IsLetterOrDigit(reading[end])) seen++;
                end++;
            }
            yield return Probe(set, reading[..end], truthKey);
        }
    }

    private static PrefixProbe Probe(string set, string reading, string truthKey) =>
        new(set, reading, truthKey, CorpusText.LetterOrDigitCount(reading), CorpusText.WordCount(CorpusText.MatchKey(reading)));

    private static string Bucket(int letters) => letters switch
    {
        < 10 => "06-09",
        < 14 => "10-13",
        < 20 => "14-19",
        < 30 => "20-29",
        < 50 => "30-49",
        _ => "50+",
    };

    private static (string Result, bool ViaPrefix, string Detail) Classify(CorpusSnapper snapper, PrefixProbe probe)
    {
        var segments = snapper.SnapBlock(probe.Reading).Segments.Where(static s => s.Match.Kind != CorpusMatchKind.Fragment).ToList();
        if (segments.Count == 0) return ("brak", false, string.Empty);
        var detail = string.Join(" | ", segments.Select(static s => $"{(s.Match.IsPrefix ? "prefiks" : s.Match.IsLabel ? "etykieta" : s.Match.Kind.ToString())} {s.Match.Score:0.00} -> {s.Match.CorpusKey}"));
        var wrong = segments.Where(s => s.Match.CorpusKey != probe.TruthKey).ToList();
        if (wrong.Count > 0) return ("bledne", wrong.Any(static s => s.Match.IsPrefix), detail);
        return ("poprawne", segments.Any(static s => s.Match.IsPrefix), detail);
    }

    private static string OneLine(string text) => text.Replace("\t", " ").Replace("\r", "").Replace("\n", " ⏎ ");
}
