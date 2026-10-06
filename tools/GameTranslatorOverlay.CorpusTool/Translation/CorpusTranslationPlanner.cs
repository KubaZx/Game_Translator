using System.Text;
using System.Text.RegularExpressions;
using GameTranslatorOverlay.Core.Corpus;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public sealed record CorpusUniqueText(string Key, string SourceText, CorpusEntry Entry, int Occurrences, int Position);

public sealed record CorpusTranslationBatch(
    int Chain,
    int Sequence,
    CorpusEntryKind Kind,
    string Scene,
    IReadOnlyList<CorpusUniqueText> Texts,
    IReadOnlyList<string?> Notes);

public sealed record CorpusUniqueTexts(IReadOnlyList<CorpusUniqueText> Texts, int Entries, int Duplicates, int Empty);

public static partial class CorpusTranslationPlanner
{
    public const int MaxNoteLength = 160;
    public const int MaxSpeakersInScene = 8;

    public static CorpusUniqueTexts Unique(IReadOnlyList<CorpusEntry> entries, IReadOnlySet<CorpusEntryKind>? kinds = null)
    {
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var texts = new List<CorpusUniqueText>();
        var duplicates = 0;
        var empty = 0;
        var considered = 0;
        foreach (var entry in entries)
        {
            if (kinds is not null && !kinds.Contains(entry.Kind)) continue;
            considered++;
            var key = CorpusTranslationKey.For(entry);
            if (key.Length == 0 || !key.Any(char.IsLetterOrDigit))
            {
                empty++;
                continue;
            }
            if (byKey.TryGetValue(key, out var index))
            {
                texts[index] = texts[index] with { Occurrences = texts[index].Occurrences + 1 };
                duplicates++;
                continue;
            }
            byKey[key] = texts.Count;
            texts.Add(new CorpusUniqueText(key, CorpusTranslationKey.DisplayText(entry.En).Trim(), entry, 1, texts.Count));
        }
        return new CorpusUniqueTexts(texts, considered, duplicates, empty);
    }

    public static IReadOnlyList<CorpusTranslationBatch> Batches(IReadOnlyList<CorpusUniqueText> texts, int batchSize)
    {
        var size = Math.Max(1, batchSize);
        var batches = new List<CorpusTranslationBatch>();
        var chain = 0;

        var dialogUnits = texts.Where(static t => t.Entry.Kind == CorpusEntryKind.Dialog)
            .GroupBy(static t => (t.Entry.Source, t.Entry.Node))
            .OrderBy(static g => g.Min(static t => t.Position));
        foreach (var unit in dialogUnits)
        {
            var ordered = unit.OrderBy(static t => t.Entry.Order ?? int.MaxValue).ThenBy(static t => t.Position).ToList();
            var sequence = 0;
            foreach (var chunk in ordered.Chunk(size))
            {
                batches.Add(Build(chain, sequence++, CorpusEntryKind.Dialog, DialogScene(unit.Key.Node ?? unit.Key.Source, chunk), chunk));
            }
            chain++;
        }

        var subtitles = texts.Where(static t => t.Entry.Kind == CorpusEntryKind.Subtitle)
            .OrderBy(static t => t.Entry.Source, StringComparer.Ordinal)
            .ThenBy(static t => t.Entry.Order ?? int.MaxValue)
            .ThenBy(static t => t.Position)
            .ToList();
        foreach (var chunk in subtitles.Chunk(size))
        {
            batches.Add(Build(chain++, 0, CorpusEntryKind.Subtitle, SubtitleScene(chunk), chunk));
        }

        var uiUnits = texts.Where(static t => t.Entry.Kind == CorpusEntryKind.Ui)
            .GroupBy(static t => t.Entry.Source)
            .OrderBy(static g => g.Min(static t => t.Position));
        foreach (var unit in uiUnits)
        {
            foreach (var chunk in unit.OrderBy(static t => t.Position).Chunk(size))
            {
                batches.Add(Build(chain++, 0, CorpusEntryKind.Ui, $"User interface strings from the table \"{OneLine(unit.Key)}\"", chunk));
            }
        }
        return batches;
    }

    public static string? Note(CorpusEntry entry)
    {
        var parts = new List<string>();
        switch (entry.Kind)
        {
            case CorpusEntryKind.Dialog:
                if (Clean(entry.Speaker) is { } dialogSpeaker) parts.Add($"speaker: {dialogSpeaker}");
                break;
            case CorpusEntryKind.Subtitle:
                if (Clean(entry.Speaker) is { } subtitleSpeaker) parts.Add($"speaker: {subtitleSpeaker}");
                if (Clean(entry.Source) is { } clip) parts.Add($"clip: {clip}");
                break;
            default:
                if (Clean(entry.Key) is { } key) parts.Add($"key: {key}");
                if (CleanContext(entry.Context) is { } context) parts.Add($"context: {context}");
                break;
        }
        if (parts.Count == 0) return null;
        var note = string.Join("; ", parts);
        return note.Length <= MaxNoteLength ? note : note[..MaxNoteLength].TrimEnd();
    }

    public static string? CleanContext(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return null;
        var parts = context.Split('|')
            .Select(static part => Clean(LocalizationFlag().Replace(part, " ")))
            .Where(static part => part is not null)
            .ToList();
        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static CorpusTranslationBatch Build(int chain, int sequence, CorpusEntryKind kind, string scene, IReadOnlyList<CorpusUniqueText> texts) =>
        new(chain, sequence, kind, scene, texts, texts.Select(static t => Note(t.Entry)).ToList());

    private static string DialogScene(string name, IReadOnlyList<CorpusUniqueText> texts)
    {
        var scene = $"Dialogue \"{OneLine(name)}\"";
        var speakers = Speakers(texts);
        return speakers.Length > 0 ? $"{scene}; speakers: {speakers}" : scene;
    }

    private static string SubtitleScene(IReadOnlyList<CorpusUniqueText> texts)
    {
        const string scene = "Voice-over subtitles; each line belongs to a separate audio clip";
        var speakers = Speakers(texts);
        return speakers.Length > 0 ? $"{scene}; speakers: {speakers}" : scene;
    }

    private static string Speakers(IReadOnlyList<CorpusUniqueText> texts) =>
        string.Join(", ", texts.Select(static t => Clean(t.Entry.Speaker)).Where(static s => s is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSpeakersInScene));

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var line = OneLine(value);
        return line.Length == 0 ? null : line;
    }

    private static string OneLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(ch);
        }
        return builder.ToString().Trim(' ', ';', ',');
    }

    [GeneratedRegex(@"%[A-Za-z][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizationFlag();
}
