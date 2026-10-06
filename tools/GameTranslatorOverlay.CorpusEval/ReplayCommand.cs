using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.CorpusEval;

internal sealed record ReplayBlock(string Day, string Time, string Text, long UseCount)
{
    public bool IsPoeSession => Day == EvalData.PoeDay && string.CompareOrdinal(Time, EvalData.PoeSessionEnd) < 0;
}

internal sealed class CountingMock : ITranslationProvider
{
    private readonly MockTranslationProvider _inner = new();
    public int Requests { get; private set; }
    public int Texts { get; private set; }
    public long Characters { get; private set; }
    public string Name => MockTranslationProvider.ProviderName;
    public bool RequiresApiKey => false;

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        Requests++;
        Texts += texts.Count;
        Characters += texts.Sum(static t => (long)t.Length);
        return _inner.TranslateBatchAsync(texts, sourceLanguage, targetLanguage, cancellationToken);
    }

    public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        _inner.TestConnectionAsync(cancellationToken);
}

internal sealed class ReplayStats
{
    public string Variant { get; init; } = string.Empty;
    public string Blocks { get; init; } = string.Empty;
    public bool Corpus { get; init; }
    public bool SplitParagraphs { get; init; }
    public bool PrefilledCache { get; init; }
    public int BlockCount { get; set; }
    public long Characters { get; set; }
    public int BlocksRejected { get; set; }
    public long CharactersRejected { get; set; }
    public long OccurrencesRejected { get; set; }
    public int BlocksLocal { get; set; }
    public long CharactersLocal { get; set; }
    public int BlocksPartlyLocal { get; set; }
    public int BlocksSnapped { get; set; }
    public int BlocksSnappedChangedText { get; set; }
    public int BlocksSnappedOtherCase { get; set; }
    public int CorpusParts { get; set; }
    public int CorpusPartsFromCache { get; set; }
    public int LiteralParts { get; set; }
    public int BlocksTranslated { get; set; }
    public int BlocksLayoutKept { get; set; }
    public int ProviderRequests { get; set; }
    public int ProviderTexts { get; set; }
    public long ProviderCharacters { get; set; }
    public long Occurrences { get; set; }
    public long OccurrencesLocal { get; set; }
    public double LocalProbeP50Ms { get; set; }
    public double LocalProbeP95Ms { get; set; }
    public double LocalProbeMaxMs { get; set; }
    public double BlocksLocalPct => EvalData.Pct(BlocksLocal, BlockCount);
    public double CharactersLocalPct => EvalData.Pct(CharactersLocal, Characters);
    public double OccurrencesLocalPct => EvalData.Pct(OccurrencesLocal, Occurrences);
    public double LayoutKeptPct => EvalData.Pct(BlocksLayoutKept, BlocksTranslated);
}

internal static class ReplayCommand
{
    private const string Source = "en";
    private const string Target = "pl";

    public static int Run(EvalArgs args)
    {
        var corpusPath = args.Required("corpus");
        var cachePath = args.Required("cache");
        var prefilled = args.Required("prefilled");
        var workDir = args.Required("work");
        var outDir = args.Required("out");
        var profile = args.Optional("profile") ?? "escape-academy";
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(outDir);

        var corpus = CorpusJsonl.ReadFile(corpusPath);
        var watch = Stopwatch.StartNew();
        var snapper = new CorpusSnapper(CorpusIndex.Build(corpus),
            CorpusFeatures.Off(args) ? CorpusFeatures.Disabled(CorpusSnapOptions.Default) : CorpusSnapOptions.Default);
        var indexMs = watch.Elapsed.TotalMilliseconds;
        var blocks = ReadBlocks(cachePath);
        var ea = blocks.Where(static b => EvalData.EaDays.Contains(b.Day)).ToList();
        var eaMain = ea.Where(static b => b.Day != "2026-09-04").ToList();
        var poe = blocks.Where(static b => b.IsPoeSession).ToList();

        var glossaryPaths = new AppPaths(Path.Combine(workDir, "app-data"));
        var review = new List<string> { string.Join('\t', "bloki", "wariant", "odczyt", "wyswietlane", "przyciagniete", "doslowne") };
        var results = new List<ReplayStats>();
        foreach (var (label, set) in new[] { ("EA 242 (09-04/12/13/15)", ea), ("EA 217 (09-12/13/15)", eaMain), ("PoE2 247 (08-06 do 12:00)", poe) })
        {
            results.Add(Replay("A: HEAD, bez korpusu, pusty cache", label, set, null, false, null, null, workDir, glossaryPaths, null));
            results.Add(Replay("A2: bez korpusu, klucze po akapitach, pusty cache", label, set, null, true, null, null, workDir, glossaryPaths, null));
            results.Add(Replay("B0: korpus EA + przyciąganie, pusty cache", label, set, snapper, true, null, profile, workDir, glossaryPaths, null));
            results.Add(Replay("B: korpus EA + przyciąganie, cache z CorpusTool translate --provider mock", label, set, snapper, true, prefilled, profile, workDir, glossaryPaths,
                label.StartsWith("EA 217", StringComparison.Ordinal) ? null : review));
        }

        var junk = JunkFilterStats(snapper.Index, corpus);
        var report = new
        {
            measuredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            corpusEntries = corpus.Count,
            corpusTexts = snapper.Index.TextCount,
            indexBuildMs = Math.Round(indexMs, 1),
            cacheBlocks = new { ea = ea.Count, eaMain = eaMain.Count, poe = poe.Count },
            snapOptions = snapper.Options with { GlyphTokens = new HashSet<string>() },
            junkFilter = junk,
            variants = results,
        };
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(outDir, "powtorka.json"), JsonSerializer.Serialize(report, json));
        File.WriteAllText(Path.Combine(outDir, "powtorka.md"), Markdown(results, corpus.Count, snapper.Index.TextCount, junk));
        File.WriteAllLines(Path.Combine(workDir, "przyciagniecia-do-przegladu.tsv"), review);
        Console.WriteLine(Markdown(results, corpus.Count, snapper.Index.TextCount, junk));
        return 0;
    }

    private static IReadOnlyList<ReplayBlock> ReadBlocks(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT substr(created_at, 1, 10), substr(created_at, 12, 8), normalized_text, use_count FROM translations ORDER BY created_at, id;";
        var blocks = new List<ReplayBlock>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) blocks.Add(new ReplayBlock(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return blocks;
    }

    private static ReplayStats Replay(
        string variant, string label, IReadOnlyList<ReplayBlock> blocks, CorpusSnapper? snapper, bool split,
        string? prefilled, string? profile, string workDir, AppPaths glossaryPaths, List<string>? review)
    {
        var databaseDir = Path.Combine(workDir, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(databaseDir);
        var databasePath = Path.Combine(databaseDir, "cache.db");
        if (prefilled is not null) File.Copy(prefilled, databasePath);
        var cache = new SqliteTranslationCache(databasePath);
        cache.Initialize();

        var glossary = new GlossaryService();
        var (document, _) = GlossaryCatalog.CreateDefault(glossaryPaths).TryLoad("global", Source, Target);
        if (document is not null) glossary.LoadDocument(document);

        var provider = new CountingMock();
        var usage = new UsageTracker();
        var pipeline = new TranslationPipeline(glossary, cache, provider, usage, new TranslationPipelineOptions
        {
            GameProfile = profile ?? string.Empty,
            Corpus = snapper,
            SplitParagraphs = split,
        });

        var stats = new ReplayStats
        {
            Variant = variant,
            Blocks = label,
            Corpus = snapper is not null,
            SplitParagraphs = split,
            PrefilledCache = prefilled is not null,
        };
        var probeTimes = new List<double>(blocks.Count);
        foreach (var block in blocks)
        {
            var letters = CorpusText.LetterOrDigitCount(block.Text);
            stats.BlockCount++;
            stats.Characters += letters;
            stats.Occurrences += Math.Max(1, block.UseCount);

            if (pipeline.IsCorpusNoise(block.Text) && !pipeline.IsExactCorpusText(block.Text))
            {
                stats.BlocksRejected++;
                stats.CharactersRejected += letters;
                stats.OccurrencesRejected += Math.Max(1, block.UseCount);
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            var local = pipeline.TranslateLocalAsync([block.Text], Source, Target).GetAwaiter().GetResult();
            probeTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var outcome = local[0] ?? pipeline.TranslateAsync([block.Text], Source, Target, local).GetAwaiter().GetResult()[0];
            var fullyLocal = local[0] is not null;

            if (fullyLocal)
            {
                stats.BlocksLocal++;
                stats.CharactersLocal += letters;
                stats.OccurrencesLocal += Math.Max(1, block.UseCount);
            }
            else
            {
                stats.OccurrencesLocal += Math.Max(0, block.UseCount - 1);
                if (outcome.Parts is { } parts)
                {
                    var localLetters = parts
                        .Where(static p => p.IsLiteral || p.Origin is TranslationOrigin.Cache or TranslationOrigin.Glossary)
                        .Sum(static p => CorpusText.LetterOrDigitCount(p.ScreenText));
                    if (localLetters > 0) stats.BlocksPartlyLocal++;
                    stats.CharactersLocal += Math.Min(letters, localLetters);
                }
            }

            if (outcome.Parts is { } allParts)
            {
                var corpusParts = allParts.Where(static p => p.FromCorpus).ToList();
                if (corpusParts.Count > 0)
                {
                    stats.BlocksSnapped++;
                    if (corpusParts.Any(static p => CorpusText.MatchKey(p.CacheKey!) != CorpusText.MatchKey(p.ScreenText)))
                        stats.BlocksSnappedChangedText++;
                    else if (corpusParts.Any(static p => !string.Equals(p.CacheKey, p.ScreenText, StringComparison.Ordinal)))
                        stats.BlocksSnappedOtherCase++;
                    review?.Add(string.Join('\t', label, variant, Escape(block.Text), Escape(outcome.TranslatedText ?? "(brak)"),
                        string.Join(" | ", corpusParts.Select(static p => $"{Escape(p.ScreenText)} => {Escape(p.CacheKey!)} [{p.Origin}]")),
                        string.Join(" | ", allParts.Where(static p => p.IsLiteral).Select(static p => Escape(p.ScreenText)))));
                }
                stats.CorpusParts += corpusParts.Count;
                stats.CorpusPartsFromCache += corpusParts.Count(static p => p.Origin == TranslationOrigin.Cache);
                stats.LiteralParts += allParts.Count(static p => p.IsLiteral);
            }

            if (outcome.TranslatedText is { } translated)
            {
                stats.BlocksTranslated++;
                if (LineCount(translated) == LineCount(TextNormalizer.Normalize(block.Text))) stats.BlocksLayoutKept++;
            }
        }

        stats.ProviderRequests = provider.Requests;
        stats.ProviderTexts = provider.Texts;
        stats.ProviderCharacters = provider.Characters;
        stats.LocalProbeP50Ms = Math.Round(EvalData.Percentile(probeTimes, 0.50), 3);
        stats.LocalProbeP95Ms = Math.Round(EvalData.Percentile(probeTimes, 0.95), 3);
        stats.LocalProbeMaxMs = Math.Round(probeTimes.Count == 0 ? 0 : probeTimes.Max(), 3);
        SqliteConnection.ClearAllPools();
        return stats;
    }

    private static string Escape(string text) => text.Replace('\t', ' ').Replace("\n", " / ");

    private static int LineCount(string text) => text.Split('\n').Count(static l => l.Trim().Length > 0);

    private static object JunkFilterStats(CorpusIndex index, IReadOnlyList<CorpusEntry> corpus)
    {
        var rejected = 0;
        var rejectedWithLetters = 0;
        var byKind = new Dictionary<string, int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in corpus)
        {
            var key = CorpusText.MatchKey(entry.En);
            if (key.Length == 0 || !seen.Add(key)) continue;
            var display = TextNormalizer.Normalize(CorpusTranslationKey.DisplayText(entry.En));
            if (JunkFilter.IsMeaningful(display)) continue;
            rejected++;
            if (key.Count(char.IsLetter) >= 2) rejectedWithLetters++;
            var kind = entry.Kind.ToString();
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
        }
        return new { uniqueTexts = index.TextCount, rejectedByJunkFilter = rejected, rejectedWithTwoLetters = rejectedWithLetters, byKind };
    }

    private static string Markdown(IReadOnlyList<ReplayStats> results, int entries, int texts, object junk)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Powtórka bloków z kopii cache przez TranslationPipeline (Mock)");
        builder.AppendLine();
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Korpus: {entries} wpisów, {texts} unikalnych tekstów. Każdy blok raz, w kolejności created_at, ścieżką live (TranslateLocalAsync, potem TranslateAsync z wynikiem próby). Liczby bez tekstów gry."));
        builder.AppendLine();
        builder.AppendLine("| Bloki | Wariant | Bloki lokalnie | Znaki lokalnie | Wystąpienia lokalnie (waga use_count) | Zapytania do dostawcy | Teksty / znaki do dostawcy | Bloki przyciągnięte (zmieniona treść / tylko wielkość liter) | Odrzucone jako szum (bloki / znaki / wystąpienia) | Układ wierszy zachowany | Próba lokalna p50/p95 ms |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Blocks} | {r.Variant} | {r.BlocksLocal}/{r.BlockCount} ({r.BlocksLocalPct:0.0}%) | {r.CharactersLocal}/{r.Characters} ({r.CharactersLocalPct:0.0}%) | {r.OccurrencesLocal}/{r.Occurrences} ({r.OccurrencesLocalPct:0.0}%) | {r.ProviderRequests} | {r.ProviderTexts} / {r.ProviderCharacters} | {r.BlocksSnapped} ({r.BlocksSnappedChangedText} / {r.BlocksSnappedOtherCase}) | {r.BlocksRejected} / {r.CharactersRejected} / {r.OccurrencesRejected} | {r.BlocksLayoutKept}/{r.BlocksTranslated} ({r.LayoutKeptPct:0.0}%) | {r.LocalProbeP50Ms:0.###}/{r.LocalProbeP95Ms:0.###} |"));
        }
        builder.AppendLine();
        builder.AppendLine("JunkFilter na tekstach korpusu: " + JsonSerializer.Serialize(junk));
        return builder.ToString();
    }
}
