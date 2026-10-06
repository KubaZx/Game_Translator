using System.Globalization;
using System.Text.RegularExpressions;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.CorpusTool.Unity;

namespace GameTranslatorOverlay.CorpusTool.Parsing;

public sealed class SourceStats
{
    public required string Id { get; init; }
    public int Assets { get; set; }
    public int SkippedAssets { get; set; }
    public int Entries { get; set; }
    public int FallbackBytes { get; set; }
    public int AssetsWithFallback { get; set; }
}

public sealed record RecipeResult(IReadOnlyList<CorpusEntry> Entries, IReadOnlyList<SourceStats> Sources);

public static class RecipeRunner
{
    public static RecipeResult Run(CorpusRecipe recipe, IReadOnlyList<TextAssetData> assets)
    {
        var entries = new List<CorpusEntry>();
        var stats = new List<SourceStats>();
        foreach (var source in recipe.Sources)
        {
            var sourceStats = new SourceStats { Id = source.Id };
            stats.Add(sourceStats);
            if (!CorpusEntry.TryParseKind(source.Kind, out var kind))
            {
                throw new FormatException($"Źródło „{source.Id}”: nieznany rodzaj „{source.Kind}”.");
            }
            var include = source.Include.Select(static g => new NamePattern(g)).ToList();
            var exclude = source.Exclude.Select(static g => new NamePattern(g)).ToList();
            var speaker = string.IsNullOrWhiteSpace(source.SpeakerPattern)
                ? null
                : new Regex(source.SpeakerPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

            foreach (var asset in assets)
            {
                if (!NamePattern.Matches(asset.Name, include, exclude)) continue;
                var decoded = TolerantText.Decode(asset.Script);
                var produced = source.Parser.ToLowerInvariant() switch
                {
                    "csv" => FromCsv(source, kind, speaker, asset.Name, decoded.Text),
                    "srt" => FromSrt(source, kind, speaker, asset.Name, decoded.Text),
                    _ => throw new FormatException($"Źródło „{source.Id}”: nieznany parser „{source.Parser}”."),
                };
                if (produced is null)
                {
                    sourceStats.SkippedAssets++;
                    continue;
                }
                sourceStats.Assets++;
                sourceStats.Entries += produced.Count;
                if (decoded.FallbackBytes > 0)
                {
                    sourceStats.AssetsWithFallback++;
                    sourceStats.FallbackBytes += decoded.FallbackBytes;
                }
                entries.AddRange(produced);
            }
        }
        return new RecipeResult(entries, stats);
    }

    private static List<CorpusEntry>? FromCsv(CorpusSourceRecipe source, CorpusEntryKind kind, Regex? speakerPattern, string assetName, string text)
    {
        var table = CsvTable.Parse(text);
        var textColumn = table.ColumnIndex(source.TextColumn);
        if (textColumn < 0) return null;
        var keyColumn = table.ColumnIndex(source.KeyColumn);
        var contextColumns = source.ContextColumns.Select(table.ColumnIndex).Where(static i => i >= 0).ToList();
        var nodeColumns = source.NodeColumns.Select(table.ColumnIndex).Where(static i => i >= 0).ToList();
        var orderColumn = table.ColumnIndex(source.OrderColumn);

        var entries = new List<CorpusEntry>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var (speaker, body) = Clean(CsvTable.Cell(row, textColumn), source, speakerPattern);
            if (body is null) continue;
            var key = CsvTable.Cell(row, keyColumn).Trim();
            entries.Add(new CorpusEntry
            {
                Key = key.Length > 0 ? key : $"{assetName}#{r + 1}",
                En = body,
                Context = Join(contextColumns.Select(i => CsvTable.Cell(row, i)), " | "),
                Kind = kind,
                Speaker = speaker,
                Node = Join(nodeColumns.Select(i => CsvTable.Cell(row, i)), "/"),
                Order = int.TryParse(CsvTable.Cell(row, orderColumn).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : null,
                Source = assetName,
            });
        }
        return entries;
    }

    private static List<CorpusEntry>? FromSrt(CorpusSourceRecipe source, CorpusEntryKind kind, Regex? speakerPattern, string assetName, string text)
    {
        if (!SrtParser.LooksLikeSrt(text)) return null;
        var entries = new List<CorpusEntry>();
        string? lastSpeaker = null;
        foreach (var cue in SrtParser.Parse(text))
        {
            var (speaker, body) = Clean(cue.Text, source, speakerPattern);
            if (speaker is not null) lastSpeaker = speaker;
            else if (source.InheritSpeaker) speaker = lastSpeaker;
            if (body is null) continue;
            entries.Add(new CorpusEntry
            {
                Key = $"{assetName}#{cue.Index}",
                En = body,
                Kind = kind,
                Speaker = speaker,
                Order = cue.Index,
                DurationMs = cue.DurationMs,
                Source = assetName,
            });
        }
        return entries;
    }

    private static (string? Speaker, string? Body) Clean(string raw, CorpusSourceRecipe source, Regex? speakerPattern)
    {
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        if (source.StripRichText) text = CorpusText.StripRichText(text);
        text = text.Trim();
        string? speaker = null;
        if (speakerPattern is not null)
        {
            var match = speakerPattern.Match(text);
            if (match.Success && match.Index == 0)
            {
                var name = match.Groups["speaker"].Value.Trim();
                speaker = name.Length > 0 ? name : null;
                text = text[match.Length..].Trim();
            }
        }
        text = string.Join('\n', text.Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0));
        return (speaker, CorpusText.LetterOrDigitCount(text) == 0 ? null : text);
    }

    private static string? Join(IEnumerable<string> parts, string separator)
    {
        var values = parts.Select(static p => p.Trim()).Where(static p => p.Length > 0).ToList();
        return values.Count == 0 ? null : string.Join(separator, values);
    }
}
