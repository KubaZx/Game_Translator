using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Text;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.CorpusEval;

internal sealed record CachedBlock(string Day, string Time, string Text)
{
    public bool IsPoeSession => Day == EvalData.PoeDay && string.CompareOrdinal(Time, EvalData.PoeSessionEnd) < 0;
    public bool IsEaOnPoeDay => Day == EvalData.PoeDay && !IsPoeSession;
}

internal static class EvalData
{
    public static readonly string[] EaDays = ["2026-09-04", "2026-09-12", "2026-09-13", "2026-09-15"];
    public const string PoeDay = "2026-08-06";
    public const string PoeSessionEnd = "12:00:00";

    public static IReadOnlyList<CachedBlock> ReadCache(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT substr(created_at, 1, 10), substr(created_at, 12, 8), normalized_text FROM translations ORDER BY created_at, id;";
        var blocks = new List<CachedBlock>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) blocks.Add(new CachedBlock(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return blocks;
    }

    public static IReadOnlyList<OcrSampleRecord> ReadSamples(string paths) =>
        paths.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(static path => File.ReadLines(path)
                .Where(static line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<OcrSampleRecord>(line, RenderCommand.Json)! with
                {
                    Dataset = Path.GetFileNameWithoutExtension(path),
                }))
            .ToList();

    public static HashSet<string> CaseSensitiveKeys(IEnumerable<CorpusEntry> corpus) =>
        new(corpus.Select(static e => CorpusTranslationKey.Normalize(e.En)), StringComparer.Ordinal);

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(static v => v).ToList();
        var rank = p * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    public static double Pct(double part, double total) => total <= 0 ? 0 : Math.Round(100.0 * part / total, 2);
}
