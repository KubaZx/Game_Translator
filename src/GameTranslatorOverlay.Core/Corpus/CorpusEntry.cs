using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslatorOverlay.Core.Corpus;

[JsonConverter(typeof(JsonStringEnumConverter<CorpusEntryKind>))]
public enum CorpusEntryKind
{
    [JsonStringEnumMemberName("ui")] Ui,
    [JsonStringEnumMemberName("dialog")] Dialog,
    [JsonStringEnumMemberName("subtitle")] Subtitle,
}

public sealed record CorpusEntry
{
    public required string Key { get; init; }
    public required string En { get; init; }
    public string? Context { get; init; }
    public required CorpusEntryKind Kind { get; init; }
    public string? Speaker { get; init; }
    public string? Node { get; init; }
    public int? Order { get; init; }
    public int? DurationMs { get; init; }
    public required string Source { get; init; }

    public static bool TryParseKind(string? text, out CorpusEntryKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "ui":
                kind = CorpusEntryKind.Ui;
                return true;
            case "dialog":
                kind = CorpusEntryKind.Dialog;
                return true;
            case "subtitle":
                kind = CorpusEntryKind.Subtitle;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

public static class CorpusJsonl
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(CorpusEntry entry) => JsonSerializer.Serialize(entry, Options);

    public static CorpusEntry Deserialize(string line) =>
        JsonSerializer.Deserialize<CorpusEntry>(line, Options)
        ?? throw new FormatException("Pusty wiersz korpusu.");

    public static void Write(TextWriter writer, IEnumerable<CorpusEntry> entries)
    {
        foreach (var entry in entries)
        {
            writer.Write(Serialize(entry));
            writer.Write('\n');
        }
    }

    public static IReadOnlyList<CorpusEntry> Read(TextReader reader)
    {
        var entries = new List<CorpusEntry>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                entries.Add(Deserialize(line));
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Wiersz {lineNumber} korpusu nie jest poprawnym wpisem JSON: {ex.Message}", ex);
            }
        }
        return entries;
    }

    public static IReadOnlyList<CorpusEntry> ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }
}
