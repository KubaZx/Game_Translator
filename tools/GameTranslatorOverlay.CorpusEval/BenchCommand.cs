using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;

namespace GameTranslatorOverlay.CorpusEval;

internal static class BenchCommand
{
    private sealed record Timing(int Queries, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double MeanMs);

    public static int Run(EvalArgs args)
    {
        var corpus = CorpusJsonl.ReadFile(args.Required("corpus"));
        var samples = EvalData.ReadSamples(args.Required("ocr"));
        var cache = EvalData.ReadCache(args.Required("cache"));
        var outDir = args.Required("out");
        var syntheticSize = args.Int("synthetic", 150_000);
        var fuzzy = double.Parse(args.Optional("fuzzy") ?? "0.85", CultureInfo.InvariantCulture);
        var margin = double.Parse(args.Optional("margin") ?? "0.05", CultureInfo.InvariantCulture);
        var minLength = args.Int("min-length", 12);
        var options = new CorpusSnapOptions
        {
            FuzzyThreshold = fuzzy,
            FragmentThreshold = fuzzy,
            Margin = margin,
            MinFuzzyLength = minLength,
            MinFragmentLength = Math.Max(15, minLength),
        };
        Directory.CreateDirectory(outDir);

        var queries = cache.Where(static b => EvalData.EaDays.Contains(b.Day) || b.Day == EvalData.PoeDay).Select(static b => b.Text)
            .Concat(samples.SelectMany(static s => s.Blocks).Select(static b => b.Text))
            .ToList();

        var buildWatch = Stopwatch.StartNew();
        var eaIndex = CorpusIndex.Build(corpus);
        buildWatch.Stop();
        var eaBuildMs = buildWatch.Elapsed.TotalMilliseconds;
        var eaTiming = Measure(new CorpusSnapper(eaIndex, options), queries, repeats: 5);
        Console.WriteLine(Describe("EA", eaIndex.TextCount, eaTiming, eaBuildMs));

        var random = new Random(150_000);
        var vocabulary = SyntheticVocabulary(random, 6000);
        var synthetic = SyntheticCorpus(random, vocabulary, syntheticSize);
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        buildWatch.Restart();
        var bigIndex = CorpusIndex.Build(synthetic);
        buildWatch.Stop();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var bigQueries = SyntheticQueries(random, synthetic, vocabulary, 2000);
        var bigTiming = Measure(new CorpusSnapper(bigIndex, options), bigQueries, repeats: 3);
        var bigEaQueries = Measure(new CorpusSnapper(bigIndex, options), queries, repeats: 1);
        Console.WriteLine(Describe("syntetyczny", bigIndex.TextCount, bigTiming, buildWatch.Elapsed.TotalMilliseconds));
        Console.WriteLine(Describe("syntetyczny (zapytania EA)", bigIndex.TextCount, bigEaQueries, buildWatch.Elapsed.TotalMilliseconds));

        var report = new
        {
            options = new { fuzzy, margin, minLength },
            machine = new { Environment.ProcessorCount, os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString() },
            ea = new { texts = eaIndex.TextCount, buildMs = Math.Round(eaBuildMs, 1), snapBlock = eaTiming },
            synthetic = new
            {
                texts = bigIndex.TextCount,
                buildMs = Math.Round(buildWatch.Elapsed.TotalMilliseconds, 1),
                indexMemoryMb = Math.Round((memoryAfter - memoryBefore) / 1024.0 / 1024.0, 1),
                snapBlockSyntheticQueries = bigTiming,
                snapBlockEaQueries = bigEaQueries,
            },
        };
        File.WriteAllText(Path.Combine(outDir, "czasy-dopasowania.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }

    private static string Describe(string name, int texts, Timing t, double buildMs) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{name}: {texts} tekstów, budowa indeksu {buildMs:0} ms; SnapBlock na {t.Queries} zapytaniach: p50 {t.P50Ms:0.000} ms, p95 {t.P95Ms:0.000} ms, p99 {t.P99Ms:0.000} ms, max {t.MaxMs:0.0} ms");

    private static Timing Measure(CorpusSnapper snapper, IReadOnlyList<string> queries, int repeats)
    {
        foreach (var query in queries.Take(200)) snapper.SnapBlock(query);
        var times = new List<double>(queries.Count * repeats);
        for (var r = 0; r < repeats; r++)
        {
            foreach (var query in queries)
            {
                var start = Stopwatch.GetTimestamp();
                snapper.SnapBlock(query);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        return new Timing(queries.Count,
            Math.Round(EvalData.Percentile(times, 0.50), 4),
            Math.Round(EvalData.Percentile(times, 0.95), 4),
            Math.Round(EvalData.Percentile(times, 0.99), 4),
            Math.Round(times.Max(), 3),
            Math.Round(times.Average(), 4));
    }

    private static List<string> SyntheticVocabulary(Random random, int size)
    {
        const string consonants = "bcdfghjklmnprstvwz";
        const string vowels = "aeiouy";
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (words.Count < size)
        {
            var syllables = 1 + (int)Math.Floor(Math.Abs(NextGaussian(random)) * 1.2);
            var builder = new StringBuilder();
            for (var s = 0; s < Math.Min(4, syllables); s++)
            {
                builder.Append(consonants[random.Next(consonants.Length)]);
                builder.Append(vowels[random.Next(vowels.Length)]);
                if (random.NextDouble() < 0.3) builder.Append(consonants[random.Next(consonants.Length)]);
            }
            words.Add(builder.ToString());
        }
        return words.ToList();
    }

    private static List<CorpusEntry> SyntheticCorpus(Random random, List<string> vocabulary, int size)
    {
        var entries = new List<CorpusEntry>(size);
        for (var i = 0; i < size; i++)
        {
            var roll = random.NextDouble();
            var words = roll < 0.25 ? random.Next(1, 3) : roll < 0.8 ? random.Next(3, 12) : random.Next(12, 30);
            var text = Sentence(random, vocabulary, words);
            if (random.NextDouble() < 0.1) text += " " + random.Next(1, 500).ToString(CultureInfo.InvariantCulture);
            entries.Add(new CorpusEntry
            {
                Key = $"syn#{i}",
                En = text,
                Kind = words <= 2 ? CorpusEntryKind.Ui : i % 2 == 0 ? CorpusEntryKind.Dialog : CorpusEntryKind.Subtitle,
                Source = "syntetyczny",
            });
        }
        return entries;
    }

    private static List<string> SyntheticQueries(Random random, List<CorpusEntry> corpus, List<string> vocabulary, int count)
    {
        var queries = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var roll = random.NextDouble();
            if (roll < 0.5)
            {
                var entry = corpus[random.Next(corpus.Count)].En;
                queries.Add(Wrap(random, Noise(random, entry, 0.05)));
            }
            else if (roll < 0.7)
            {
                var entry = corpus[random.Next(corpus.Count)].En;
                var words = entry.Split(' ');
                var take = Math.Max(1, words.Length / 2);
                queries.Add(string.Join(' ', words.Take(take)));
            }
            else
            {
                queries.Add(Wrap(random, Sentence(random, vocabulary, random.Next(2, 18))));
            }
        }
        return queries;
    }

    private static string Sentence(Random random, List<string> vocabulary, int words)
    {
        var builder = new StringBuilder();
        for (var w = 0; w < words; w++)
        {
            if (w > 0) builder.Append(' ');
            var index = (int)Math.Min(vocabulary.Count - 1, Math.Floor(Math.Pow(random.NextDouble(), 2.2) * vocabulary.Count));
            var word = vocabulary[index];
            builder.Append(w == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word);
        }
        builder.Append(random.NextDouble() < 0.7 ? "." : random.NextDouble() < 0.5 ? "!" : "?");
        return builder.ToString();
    }

    private static string Noise(Random random, string text, double rate)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var roll = random.NextDouble();
            if (roll < rate / 3) continue;
            if (roll < 2 * rate / 3)
            {
                builder.Append((char)('a' + random.Next(26)));
                continue;
            }
            builder.Append(ch);
            if (roll < rate) builder.Append((char)('a' + random.Next(26)));
        }
        return builder.ToString();
    }

    private static string Wrap(Random random, string text)
    {
        var words = text.Split(' ');
        if (words.Length < 6 || random.NextDouble() < 0.4) return text;
        var lines = Math.Min(3, 1 + words.Length / 6);
        return GameTranslatorOverlay.Core.Text.TextReflow.WrapBalanced(text, lines);
    }

    private static double NextGaussian(Random random) =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
}
