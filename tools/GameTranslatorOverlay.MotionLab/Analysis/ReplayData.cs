using System.Text.Json;

internal sealed class ReplayData
{
    private ReplayData(string directory, ReplayHeaderDto header, ReplaySummaryDto? summary, IReadOnlyList<ShownDto> shown,
        IReadOnlyList<UpdateDto> updates, IReadOnlyList<OcrDto> ocr, IReadOnlyDictionary<int, LayerDto> layers,
        IReadOnlyDictionary<int, PatchDto> patches)
    {
        Directory = directory;
        Header = header;
        Summary = summary;
        Shown = shown;
        Updates = updates;
        Ocr = ocr;
        Layers = layers;
        Patches = patches;
    }

    public string Directory { get; }
    public ReplayHeaderDto Header { get; }
    public ReplaySummaryDto? Summary { get; }
    public IReadOnlyList<ShownDto> Shown { get; }
    public IReadOnlyList<UpdateDto> Updates { get; }
    public IReadOnlyList<OcrDto> Ocr { get; }
    public IReadOnlyDictionary<int, LayerDto> Layers { get; }
    public IReadOnlyDictionary<int, PatchDto> Patches { get; }

    public string PathOf(params string[] parts) => Path.Combine([Directory, .. parts]);

    public static ReplayData Load(string directory)
    {
        var replayJson = Path.Combine(directory, "replay.json");
        if (!File.Exists(replayJson)) throw new ArgumentException($"Brak replay.json w {directory}");
        var text = File.ReadAllText(replayJson);
        ReplaySummaryDto? summary = null;
        ReplayHeaderDto header;
        using (var document = JsonDocument.Parse(text))
        {
            if (document.RootElement.TryGetProperty("header", out _))
            {
                summary = JsonSerializer.Deserialize<ReplaySummaryDto>(text, Json.Options)
                    ?? throw new InvalidDataException("Uszkodzony replay.json.");
                header = summary.Header;
            }
            else
            {
                header = JsonSerializer.Deserialize<ReplayHeaderDto>(text, Json.Options)
                    ?? throw new InvalidDataException("Uszkodzony replay.json.");
            }
        }
        var shown = Json.ReadLines<ShownDto>(Path.Combine(directory, "shown.jsonl")).OrderBy(static s => s.I).ToList();
        var updates = Json.ReadLines<UpdateDto>(Path.Combine(directory, "updates.jsonl")).OrderBy(static u => u.AppliedMs).ThenBy(static u => u.Seq).ToList();
        var ocr = File.Exists(Path.Combine(directory, "ocr.jsonl"))
            ? Json.ReadLines<OcrDto>(Path.Combine(directory, "ocr.jsonl")).OrderBy(static o => o.StartMs).ToList()
            : [];
        var layers = File.Exists(Path.Combine(directory, "layers.jsonl"))
            ? Json.ReadLines<LayerDto>(Path.Combine(directory, "layers.jsonl")).ToDictionary(static l => l.Id)
            : [];
        var patches = File.Exists(Path.Combine(directory, "patches.jsonl"))
            ? Json.ReadLines<PatchDto>(Path.Combine(directory, "patches.jsonl")).ToDictionary(static p => p.Id)
            : [];
        return new ReplayData(directory, header, summary, shown, updates, ocr, layers, patches);
    }
}
