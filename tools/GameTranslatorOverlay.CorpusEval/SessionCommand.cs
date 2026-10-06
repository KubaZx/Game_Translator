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

internal sealed record SessionReading(string CreatedAt, string Text, long UseCount);

internal sealed class SessionStats
{
    public string Variant { get; init; } = string.Empty;
    public int Readings { get; set; }
    public long Characters { get; set; }
    public int Dropped { get; set; }
    public long DroppedCharacters { get; set; }
    public int Local { get; set; }
    public long LocalCharacters { get; set; }
    public int SentToProvider { get; set; }
    public int ProviderRequests { get; set; }
    public int ProviderTexts { get; set; }
    public long ProviderCharacters { get; set; }
    public int Snapped { get; set; }
    public int SnappedLabel { get; set; }
    public int SnappedPrefix { get; set; }
    public int SnappedOther { get; set; }
    public int SameIdentityGroups { get; set; }
}

internal static class CorpusFeatures
{
    public static CorpusSnapOptions Disabled(CorpusSnapOptions options) =>
        options with { AllowLabels = false, AllowPrefixes = false, RejectNoise = false };

    public static bool Off(EvalArgs args) => string.Equals(args.Optional("features"), "off", StringComparison.OrdinalIgnoreCase);
}

internal static class SessionCommand
{
    private const string Source = "en";
    private const string Target = "pl";

    public static int Run(EvalArgs args)
    {
        var corpusPath = args.Required("corpus");
        var cachePath = args.Required("cache");
        var since = args.Required("since");
        var workDir = args.Required("work");
        var outDir = args.Required("out");
        var profile = args.Optional("profile") ?? "escape-academy";
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(outDir);

        var corpus = CorpusJsonl.ReadFile(corpusPath);
        var index = CorpusIndex.Build(corpus);
        var readings = ReadReadings(cachePath, since);
        var glossaryPaths = new AppPaths(Path.Combine(workDir, "app-data"));

        var review = new List<string> { string.Join('\t', "wariant", "czas", "odczyt", "decyzja", "wyswietlane", "czesci_korpusu", "tozsamosc") };
        var baseline = Replay("przed: dopasowanie jak w 008f39c (bez etykiet, prefiksów i reguły szumu)", readings, index,
            CorpusFeatures.Disabled(CorpusSnapOptions.Default),
            cachePath, since, profile, workDir, glossaryPaths, review);
        var current = Replay("po: etykiety + dialog pisany literami + szum", readings, index,
            CorpusSnapOptions.Default,
            cachePath, since, profile, workDir, glossaryPaths, review);

        var report = new
        {
            measuredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            since,
            corpusEntries = corpus.Count,
            corpusTexts = index.TextCount,
            readings = readings.Count,
            occurrences = readings.Sum(static r => Math.Max(1, r.UseCount)),
            variants = new[] { baseline, current },
        };
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(outDir, "sesja.json"), JsonSerializer.Serialize(report, json));
        File.WriteAllText(Path.Combine(outDir, "sesja.md"), Markdown(readings.Count, [baseline, current]));
        File.WriteAllLines(Path.Combine(workDir, "sesja-przeglad.tsv"), review, new UTF8Encoding(false));
        Console.WriteLine(Markdown(readings.Count, [baseline, current]));
        return 0;
    }

    private static List<SessionReading> ReadReadings(string path, string since)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT created_at, normalized_text, use_count FROM translations WHERE created_at >= $since ORDER BY created_at, id;";
        command.Parameters.AddWithValue("$since", since);
        var readings = new List<SessionReading>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) readings.Add(new SessionReading(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return readings;
    }

    private static string PrepareCache(string sourcePath, string since, string workDir)
    {
        var databaseDir = Path.Combine(workDir, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(databaseDir);
        var databasePath = Path.Combine(databaseDir, "cache.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using (var source = new SqliteConnection(builder.ToString()))
        {
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", databasePath);
            command.ExecuteNonQuery();
        }
        var target = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        using (var connection = new SqliteConnection(target.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM translations WHERE created_at >= $since;";
            command.Parameters.AddWithValue("$since", since);
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return databasePath;
    }

    private static SessionStats Replay(
        string variant, IReadOnlyList<SessionReading> readings, CorpusIndex index, CorpusSnapOptions snapOptions,
        string cachePath, string since, string profile, string workDir, AppPaths glossaryPaths, List<string> review)
    {
        var cache = new SqliteTranslationCache(PrepareCache(cachePath, since, workDir));
        cache.Initialize();
        var glossary = new GlossaryService();
        var (document, _) = GlossaryCatalog.CreateDefault(glossaryPaths).TryLoad("global", Source, Target);
        if (document is not null) glossary.LoadDocument(document);
        var provider = new CountingMock();
        var pipeline = new TranslationPipeline(glossary, cache, provider, new UsageTracker(), new TranslationPipelineOptions
        {
            GameProfile = profile,
            Corpus = new CorpusSnapper(index, snapOptions),
            SplitParagraphs = true,
        });

        var stats = new SessionStats { Variant = variant };
        var identities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var reading in readings)
        {
            var letters = CorpusText.LetterOrDigitCount(reading.Text);
            stats.Readings++;
            stats.Characters += letters;
            if (!pipeline.ShouldTranslateLive(reading.Text))
            {
                stats.Dropped++;
                stats.DroppedCharacters += letters;
                review.Add(Row(variant, reading, "odrzucony", "", "", ""));
                continue;
            }

            var requestsBefore = provider.Texts;
            var local = pipeline.TranslateLocalAsync([reading.Text], Source, Target).GetAwaiter().GetResult();
            var outcome = local[0] ?? pipeline.TranslateAsync([reading.Text], Source, Target, local).GetAwaiter().GetResult()[0];
            var sent = provider.Texts > requestsBefore;
            if (sent)
            {
                stats.SentToProvider++;
            }
            else
            {
                stats.Local++;
                stats.LocalCharacters += letters;
            }

            var snap = pipeline.Options.Corpus!.SnapBlock(reading.Text);
            var served = snap.Segments.Where(static s => s.Match.Kind != CorpusMatchKind.Fragment).ToList();
            if (outcome.Parts?.Any(static p => p.FromCorpus) == true && served.Count > 0)
            {
                stats.Snapped++;
                if (served.Any(static s => s.Match.IsPrefix)) stats.SnappedPrefix++;
                else if (served.Any(static s => s.Match.IsLabel)) stats.SnappedLabel++;
                else stats.SnappedOther++;
            }
            var identity = pipeline.CorpusIdentity(reading.Text);
            if (identity is not null)
            {
                if (!identities.TryGetValue(identity, out var texts)) identities[identity] = texts = new HashSet<string>(StringComparer.Ordinal);
                texts.Add(reading.Text);
            }
            var parts = string.Join(" | ", served.Select(static s =>
                $"{s.Text} => {s.Match.CorpusKey} [{(s.Match.IsPrefix ? "prefiks" : s.Match.IsLabel ? "etykieta" : s.Match.Kind.ToString())} {s.Match.Score:0.00}]"));
            review.Add(Row(variant, reading, sent ? "dostawca" : "lokalnie", outcome.TranslatedText ?? "(brak)", parts, identity is null ? "" : "tak"));
        }

        stats.ProviderRequests = provider.Requests;
        stats.ProviderTexts = provider.Texts;
        stats.ProviderCharacters = provider.Characters;
        stats.SameIdentityGroups = identities.Count(static kv => kv.Value.Count > 1);
        SqliteConnection.ClearAllPools();
        return stats;
    }

    private static string Row(string variant, SessionReading reading, string decision, string display, string parts, string identity) =>
        string.Join('\t', variant, reading.CreatedAt, Escape(reading.Text), decision, Escape(display), Escape(parts), identity);

    private static string Escape(string text) => text.Replace('\t', ' ').Replace("\n", " / ");

    private static string Markdown(int readings, IReadOnlyList<SessionStats> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Powtórka odczytów z sesji przez TranslationPipeline (Mock)");
        builder.AppendLine();
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Wejście: {readings} odczytów OCR, które w sesji poszły do dostawcy. Cache: kopia bazy gracza bez tych odczytów. Liczby bez tekstów gry."));
        builder.AppendLine();
        builder.AppendLine("| Wariant | Odczyty | Odrzucone (szum) | Lokalnie | Do dostawcy | Znaki do dostawcy | Przyciągnięte: etykieta / prefiks dialogu / inne | Wspólna tożsamość (grupy) |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|");
        foreach (var r in results)
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Variant} | {r.Readings} | {r.Dropped} | {r.Local} | {r.SentToProvider} | {r.ProviderCharacters} | {r.SnappedLabel} / {r.SnappedPrefix} / {r.SnappedOther} | {r.SameIdentityGroups} |"));
        }
        return builder.ToString();
    }
}
