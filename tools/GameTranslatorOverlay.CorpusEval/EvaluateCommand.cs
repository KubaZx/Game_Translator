using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.CorpusEval;

internal sealed record SweepConfig(double Fuzzy, double Margin, int MinLength, int MinExactLength = 0)
{
    public CorpusSnapOptions Options => new()
    {
        MinExactLength = MinExactLength,
        FuzzyThreshold = Fuzzy,
        FragmentThreshold = Fuzzy,
        Margin = Margin,
        MinFuzzyLength = MinLength,
        MinFragmentLength = Math.Max(15, MinLength),
    };

    public string Label => string.Create(CultureInfo.InvariantCulture,
        $"T={Fuzzy:0.00} m={Margin:0.00} L={MinLength}{(MinExactLength > 0 ? $" E={MinExactLength}" : "")}");
}

internal sealed class SyntheticStats
{
    public int Samples { get; set; }
    public int Correct { get; set; }
    public int Wrong { get; set; }
    public int FragmentCorrect { get; set; }
    public int FragmentWrong { get; set; }
    public int None { get; set; }
    public int CorrectExact { get; set; }
    public int CorrectFuzzy { get; set; }
    public int BackgroundSnaps { get; set; }
    public int RawExactCase { get; set; }
    public int RawExactNoCase { get; set; }
    public int TruthKeysRecomputed { get; set; }
    public double WrongOfSnappedPct => EvalData.Pct(Wrong, Correct + Wrong);
    public double WrongOfAllPct => EvalData.Pct(Wrong, Samples);
    public double CorrectPct => EvalData.Pct(Correct, Samples);
    public double NonePct => EvalData.Pct(None + FragmentCorrect + FragmentWrong, Samples);
}

internal sealed class CacheStats
{
    public int Blocks { get; set; }
    public int Characters { get; set; }
    public int BaselineExactBlocks { get; set; }
    public int BaselineExactCharacters { get; set; }
    public int BlocksAnyMatch { get; set; }
    public int BlocksServedMatch { get; set; }
    public int BlocksFullyServed { get; set; }
    public int ExactCharacters { get; set; }
    public int FuzzyCharacters { get; set; }
    public int FragmentCharacters { get; set; }
    public int AssembledCharacters { get; set; }
    public int PartialLineCharacters { get; set; }
    public int BlocksChanged { get; set; }
    public int IdenticalSegments { get; set; }
    public int ChangedSegments { get; set; }
    public int ServedCharacters => ExactCharacters + FuzzyCharacters;
    public double ServedPct => EvalData.Pct(ServedCharacters, Characters);
    public double ServedWithFragmentsPct => EvalData.Pct(ServedCharacters + FragmentCharacters, Characters);
    public double FullyServedBlocksPct => EvalData.Pct(BlocksFullyServed, Blocks);
    public double AnyMatchBlocksPct => EvalData.Pct(BlocksAnyMatch, Blocks);
    public double ServedMatchBlocksPct => EvalData.Pct(BlocksServedMatch, Blocks);
    public double ChangedBlocksPct => EvalData.Pct(BlocksChanged, Blocks);
}

internal sealed record SweepResult(SweepConfig Config, SyntheticStats Synthetic, CacheStats Ea, CacheStats EaMain, CacheStats Ea0904, CacheStats Poe, CacheStats EaPoeDay);

internal static class EvaluateCommand
{
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
        var caseKeys = EvalData.CaseSensitiveKeys(corpus);
        var ea = cache.Where(static b => EvalData.EaDays.Contains(b.Day)).ToList();
        var eaMain = ea.Where(static b => b.Day != "2026-09-04").ToList();
        var ea0904 = ea.Where(static b => b.Day == "2026-09-04").ToList();
        var poe = cache.Where(static b => b.IsPoeSession).ToList();
        var eaPoeDay = cache.Where(static b => b.IsEaOnPoeDay).ToList();
        Console.WriteLine($"Korpus: {corpus.Count} wpisów, {index.TextCount} unikalnych tekstów; próbki OCR: {samples.Count}; bloki EA: {ea.Count} (w tym 09-04: {ea0904.Count}); bloki PoE2 (sesja do 12:00 UTC): {poe.Count}; bloki EA z 08-06 po 12:00: {eaPoeDay.Count}");

        var configs = new List<SweepConfig>();
        foreach (var fuzzy in new[] { 0.75, 0.80, 0.85, 0.88, 0.90, 0.93, 0.95 })
            foreach (var margin in new[] { 0.03, 0.05, 0.08, 0.12 })
                foreach (var minLength in new[] { 8, 10, 12, 16 })
                    foreach (var minExact in new[] { 0, 8, 10, 12 })
                        configs.Add(new SweepConfig(fuzzy, margin, minLength, minExact));
        if (args.Optional("only") is { } only)
        {
            var parts = only.Split(',').Select(static p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            configs = [new SweepConfig(parts[0], parts[1], (int)parts[2], parts.Length > 3 ? (int)parts[3] : 0)];
        }

        var results = new List<SweepResult>();
        foreach (var config in configs)
        {
            var snapper = new CorpusSnapper(index, config.Options);
            results.Add(new SweepResult(config,
                EvaluateSynthetic(snapper, samples, null),
                EvaluateCache(snapper, ea, caseKeys, null),
                EvaluateCache(snapper, eaMain, caseKeys, null),
                EvaluateCache(snapper, ea0904, caseKeys, null),
                EvaluateCache(snapper, poe, caseKeys, null),
                EvaluateCache(snapper, eaPoeDay, caseKeys, null)));
        }

        var eligible = results
            .Where(static r => r.Synthetic.WrongOfSnappedPct <= 1.0 && r.Poe.AnyMatchBlocksPct <= 1.0 && r.Ea.ServedPct >= 40.0)
            .OrderByDescending(static r => r.Ea.ServedCharacters)
            .ThenByDescending(static r => r.Synthetic.Correct)
            .ThenBy(static r => r.Synthetic.Wrong)
            .ThenByDescending(static r => r.Config.Margin)
            .ToList();
        var relaxed = results
            .Where(static r => r.Synthetic.WrongOfSnappedPct <= 1.0 && r.Poe.ChangedBlocksPct <= 1.0 && r.Ea.ServedPct >= 40.0)
            .OrderByDescending(static r => r.Ea.ServedCharacters)
            .ThenByDescending(static r => r.Synthetic.Correct)
            .ThenBy(static r => r.Synthetic.Wrong)
            .ThenByDescending(static r => r.Config.Margin)
            .ToList();
        var chosen = relaxed.FirstOrDefault() ?? eligible.FirstOrDefault()
            ?? results.OrderBy(static r => r.Synthetic.Wrong + r.Poe.BlocksAnyMatch).First();
        var strictChoice = eligible.FirstOrDefault();

        var chosenSnapper = new CorpusSnapper(index, chosen.Config.Options);
        var strata = samples.GroupBy(static s => s.Stratum)
            .Concat(samples.GroupBy(static s => "zbior:" + s.Dataset))
            .ToDictionary(static g => g.Key, g => EvaluateSynthetic(chosenSnapper, g.ToList(), null));
        var errors = new List<string>();
        EvaluateSynthetic(chosenSnapper, samples, errors);
        var reviewPool = new List<string>();
        EvaluateCache(chosenSnapper, ea.Concat(eaPoeDay).ToList(), caseKeys, reviewPool);
        var poeHits = new List<string>();
        EvaluateCache(chosenSnapper, poe, caseKeys, poeHits);

        WritePrivate(Path.Combine(privateDir, "bledy-syntetyczne.tsv"), errors);
        var random = new Random(20261006);
        var review = reviewPool.OrderBy(_ => random.Next()).Take(50).ToList();
        WritePrivate(Path.Combine(privateDir, "przeglad-przyblizone.tsv"), review);
        WritePrivate(Path.Combine(privateDir, "poe2-trafienia.tsv"), poeHits);

        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(outDir, "sweep.json"), JsonSerializer.Serialize(results.Select(Flatten), json));
        File.WriteAllText(Path.Combine(outDir, "wybrane-progi.json"), JsonSerializer.Serialize(new
        {
            chosen = Flatten(chosen),
            strictChoice = strictChoice is null ? null : Flatten(strictChoice),
            meetsStrictPoe = eligible.Count > 0,
            meetsRelaxedPoe = relaxed.Count > 0,
            strata = strata.ToDictionary(static kv => kv.Key, static kv => kv.Value),
            reviewPoolSize = reviewPool.Count,
            reviewSample = review.Count,
        }, json));
        File.WriteAllText(Path.Combine(outDir, "wyniki-progi.md"), Markdown(results, chosen, strictChoice, strata, eligible.Count > 0, relaxed.Count > 0, corpus.Count, index.TextCount, samples.Count));

        Console.WriteLine($"Wybrane progi: {chosen.Config.Label} (ścisłe PoE2: {eligible.Count > 0}, łagodne: {relaxed.Count > 0})");
        Print(chosen);
        if (strictChoice is not null)
        {
            Console.WriteLine($"Wariant ścisły: {strictChoice.Config.Label}");
            Print(strictChoice);
        }
        return 0;
    }

    private static void Print(SweepResult r)
    {
        var s = r.Synthetic;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  syntetyczne: poprawne {s.Correct}/{s.Samples} ({s.CorrectPct}%), błędne {s.Wrong} ({s.WrongOfSnappedPct}% przyciągniętych), brak {s.None}, fragmenty {s.FragmentCorrect}+{s.FragmentWrong}, tło {s.BackgroundSnaps}; surowy OCR dokładny {s.RawExactCase} / bez wielkości liter {s.RawExactNoCase}"));
        foreach (var (name, c) in new[] { ("EA", r.Ea), ("EA bez 09-04", r.EaMain), ("EA 09-04", r.Ea0904), ("EA 08-06 po 12:00", r.EaPoeDay), ("PoE2", r.Poe) })
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name}: bloki {c.Blocks}, znaki {c.Characters}; lokalnie {c.ServedPct}% znaków (+fragmenty {c.ServedWithFragmentsPct}%), bloki w pełni {c.FullyServedBlocksPct}%, z jakimkolwiek trafieniem {c.AnyMatchBlocksPct}%, z trafieniem obsłużonym {c.ServedMatchBlocksPct}%, ze zmianą tekstu {c.ChangedBlocksPct}% ({c.BlocksChanged}); dziś dokładnie {c.BaselineExactBlocks} bloków"));
        }
    }

    internal static SyntheticStats EvaluateSynthetic(CorpusSnapper snapper, IReadOnlyList<OcrSampleRecord> samples, List<string>? errors)
    {
        var stats = new SyntheticStats();
        foreach (var sample in samples)
        {
            stats.Samples++;
            var truthKey = CorpusText.MatchKey(sample.Truth);
            if (truthKey != sample.TruthKey) stats.TruthKeysRecomputed++;
            var textBlocks = sample.Blocks.Where(static b => b.OverlapsText).ToList();
            var joined = string.Join(' ', textBlocks.Select(static b => b.Text.Replace('\n', ' ')));
            if (CollapseSpaces(joined) == CollapseSpaces(sample.Truth.Replace('\n', ' '))) stats.RawExactCase++;
            if (CorpusText.MatchKey(joined) == truthKey) stats.RawExactNoCase++;

            foreach (var background in sample.Blocks.Where(static b => !b.OverlapsText))
            {
                if (snapper.SnapBlock(background.Text).HasAnyMatch) stats.BackgroundSnaps++;
            }

            var segments = textBlocks.SelectMany(b => snapper.SnapBlock(b.Text).Segments).ToList();
            var wrongFull = segments.Where(s => s.Match.Kind != CorpusMatchKind.Fragment && s.Match.CorpusKey != truthKey).ToList();
            var correctFull = segments.FirstOrDefault(s => s.Match.Kind != CorpusMatchKind.Fragment && s.Match.CorpusKey == truthKey);
            string outcome;
            if (wrongFull.Count > 0)
            {
                stats.Wrong++;
                outcome = "bledne";
            }
            else if (correctFull is not null)
            {
                stats.Correct++;
                if (correctFull.Match.Kind == CorpusMatchKind.Exact) stats.CorrectExact++;
                else stats.CorrectFuzzy++;
                outcome = "poprawne";
            }
            else if (segments.Any(s => s.Match.CorpusKey == truthKey))
            {
                stats.FragmentCorrect++;
                outcome = "fragment-poprawny";
            }
            else if (segments.Count > 0)
            {
                stats.FragmentWrong++;
                outcome = "fragment-bledny";
            }
            else
            {
                stats.None++;
                outcome = "brak";
            }

            if (errors is not null && outcome != "poprawne")
            {
                var detail = string.Join(" || ", segments.Select(static s =>
                    $"{s.Match.Kind}{(s.Partial ? "/czesc" : "")}{(s.Assembled ? "/zlozone" : "")} {s.Match.Score:0.000}: [{s.Text}] -> [{s.Match.CorpusKey}]"));
                errors.Add($"{sample.Dataset}\t{sample.Id}\t{sample.Stratum}\t{outcome}\t{sample.Font} {sample.Size}{(sample.Bold ? "b" : "")}\t{truthKey}\t{OneLine(string.Join(" ## ", textBlocks.Select(static b => b.Text)))}\t{detail}");
            }
        }
        return stats;
    }

    internal static CacheStats EvaluateCache(CorpusSnapper snapper, IReadOnlyList<CachedBlock> blocks, HashSet<string> caseKeys, List<string>? fuzzyOrHits)
    {
        var stats = new CacheStats();
        foreach (var block in blocks)
        {
            var snap = snapper.SnapBlock(block.Text);
            stats.Blocks++;
            stats.Characters += snap.TotalLength;
            var normalized = TextNormalizer.Normalize(block.Text);
            if (caseKeys.Contains(normalized))
            {
                stats.BaselineExactBlocks++;
                stats.BaselineExactCharacters += snap.TotalLength;
            }
            if (snap.HasAnyMatch) stats.BlocksAnyMatch++;
            if (snap.Segments.Any(static s => s.Match.Kind != CorpusMatchKind.Fragment)) stats.BlocksServedMatch++;
            if (snap.IsFullyServed) stats.BlocksFullyServed++;
            if (snap.Segments.Any(static s => CorpusText.LooseKey(s.Text) != CorpusText.LooseKey(s.Match.CorpusKey))) stats.BlocksChanged++;
            foreach (var segment in snap.Segments)
            {
                switch (segment.Match.Kind)
                {
                    case CorpusMatchKind.Exact:
                        stats.ExactCharacters += segment.Length;
                        break;
                    case CorpusMatchKind.Fuzzy:
                        stats.FuzzyCharacters += segment.Length;
                        break;
                    default:
                        stats.FragmentCharacters += segment.Length;
                        break;
                }
                if (segment.Match.Kind != CorpusMatchKind.Fragment)
                {
                    if (segment.Assembled) stats.AssembledCharacters += segment.Length;
                    if (segment.Partial) stats.PartialLineCharacters += segment.Length;
                }
                if (CorpusText.LooseKey(segment.Text) == CorpusText.LooseKey(segment.Match.CorpusKey)) stats.IdenticalSegments++;
                else stats.ChangedSegments++;

                if (fuzzyOrHits is not null && (segment.Match.Kind == CorpusMatchKind.Fuzzy || block.IsPoeSession))
                {
                    fuzzyOrHits.Add($"{block.Day}\t{segment.Match.Kind}{(segment.Partial ? "/czesc" : "")}{(segment.Assembled ? "/zlozone" : "")}\t{segment.Match.Score:0.000}\t{OneLine(segment.Text)}\t{OneLine(segment.Match.CorpusKey)}\t{OneLine(block.Text)}");
                }
            }
        }
        return stats;
    }

    private static object Flatten(SweepResult r) => new
    {
        config = new { r.Config.Fuzzy, r.Config.Margin, r.Config.MinLength, r.Config.MinExactLength, fragment = r.Config.Fuzzy, minFragmentLength = Math.Max(15, r.Config.MinLength) },
        synthetic = r.Synthetic,
        ea = r.Ea,
        eaWithout0904 = r.EaMain,
        ea0904 = r.Ea0904,
        poe2 = r.Poe,
        eaOn0806Afternoon = r.EaPoeDay,
    };

    private static string Markdown(List<SweepResult> results, SweepResult chosen, SweepResult? strictChoice, Dictionary<string, SyntheticStats> strata,
        bool strict, bool relaxed, int corpusEntries, int corpusTexts, int samples)
    {
        var c = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        md.AppendLine("# Eksperyment: przyciąganie odczytów OCR do korpusu Escape Academy");
        md.AppendLine();
        md.AppendLine(string.Create(c, $"Korpus: {corpusEntries} wpisów, {corpusTexts} unikalnych tekstów (bez wielkości liter). Próbki syntetyczne: {samples}."));
        md.AppendLine(string.Create(c, $"Wybrane progi: **{chosen.Config.Label}** (próg przybliżony = próg fragmentu, min. długość fragmentu {Math.Max(15, chosen.Config.MinLength)}). Kryterium PoE2 ścisłe (każde trafienie): {(strict ? "spełnione" : "niespełnione")}; łagodne (przyciągnięcia zmieniające odczytany tekst): {(relaxed ? "spełnione" : "niespełnione")}."));
        md.AppendLine();
        md.AppendLine("## Wybrane progi");
        md.AppendLine();
        md.AppendLine("Wariant domyślny: najwięcej znaków EA obsłużonych lokalnie przy ≤1% błędnych przyciągnięć syntetycznych i ≤1% bloków PoE2 ze zmianą odczytanego tekstu. Wariant ścisły: to samo, ale ≤1% bloków PoE2 z jakimkolwiek trafieniem (także identyczną etykietą).");
        md.AppendLine();
        AppendResultTable(md, strictChoice is null ? [chosen] : [chosen, strictChoice]);
        md.AppendLine();
        md.AppendLine("## Prawda syntetyczna wg warstw (wybrane progi)");
        md.AppendLine();
        md.AppendLine("| warstwa | próbki | poprawne | błędne | % błędnych z przyciągniętych | brak | fragment poprawny | fragment błędny | surowy OCR dokładny |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, s) in strata.OrderBy(static kv => kv.Key))
        {
            md.AppendLine(string.Create(c, $"| {name} | {s.Samples} | {s.Correct} | {s.Wrong} | {s.WrongOfSnappedPct} | {s.None} | {s.FragmentCorrect} | {s.FragmentWrong} | {s.RawExactCase} |"));
        }
        md.AppendLine();
        md.AppendLine("## Przegląd progów (wszystkie konfiguracje)");
        md.AppendLine();
        AppendResultTable(md, results);
        return md.ToString();
    }

    private static void AppendResultTable(StringBuilder md, IEnumerable<SweepResult> rows)
    {
        var c = CultureInfo.InvariantCulture;
        md.AppendLine("| progi | synt. poprawne % | synt. błędne (z przyciągniętych) % | synt. brak/fragment % | tło | EA lokalnie % znaków | EA +fragmenty % | EA bloki w pełni % | EA dziś dokładnie bloki | PoE2 bloki z trafieniem % | PoE2 bloki obsłużone % | PoE2 bloki ze zmianą tekstu % |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            md.AppendLine(string.Create(c,
                $"| {r.Config.Label} | {r.Synthetic.CorrectPct} | {r.Synthetic.WrongOfSnappedPct} ({r.Synthetic.Wrong}) | {r.Synthetic.NonePct} | {r.Synthetic.BackgroundSnaps} | {r.Ea.ServedPct} | {r.Ea.ServedWithFragmentsPct} | {r.Ea.FullyServedBlocksPct} | {r.Ea.BaselineExactBlocks}/{r.Ea.Blocks} | {r.Poe.AnyMatchBlocksPct} ({r.Poe.BlocksAnyMatch}) | {r.Poe.ServedMatchBlocksPct} ({r.Poe.BlocksServedMatch}) | {r.Poe.ChangedBlocksPct} ({r.Poe.BlocksChanged}) |"));
        }
    }

    private static void WritePrivate(string path, IEnumerable<string> lines) =>
        File.WriteAllLines(path, lines, new UTF8Encoding(false));

    private static string OneLine(string text) => text.Replace("\t", " ").Replace("\r", "").Replace("\n", " ⏎ ");

    private static string CollapseSpaces(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
