using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Core.Ocr;

internal readonly record struct Box(int X, int Y, int W, int H)
{
    [JsonIgnore] public int Right => X + W;
    [JsonIgnore] public int Bottom => Y + H;
    [JsonIgnore] public double Cx => X + W / 2.0;
    [JsonIgnore] public double Cy => Y + H / 2.0;
    [JsonIgnore] public bool IsEmpty => W <= 0 || H <= 0;
    [JsonIgnore] public long Area => IsEmpty ? 0 : (long)W * H;

    public static Box From(RectPx r) => new(r.X, r.Y, r.Width, r.Height);

    public RectPx ToRect() => new(X, Y, W, H);

    public Box Offset(int dx, int dy) => new(X + dx, Y + dy, W, H);

    public Box Inflate(int amount) => new(X - amount, Y - amount, W + 2 * amount, H + 2 * amount);

    public Box Intersect(Box other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y ? default : new Box(x, y, right - x, bottom - y);
    }

    public Box Union(Box other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new Box(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public double IoU(Box other)
    {
        var inter = Intersect(other).Area;
        if (inter == 0) return 0;
        return (double)inter / (Area + other.Area - inter);
    }

    public bool ContainsPoint(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public double CenterDistance(Box other) => Math.Sqrt((Cx - other.Cx) * (Cx - other.Cx) + (Cy - other.Cy) * (Cy - other.Cy));

    public override string ToString() => $"{X},{Y} {W}×{H}";
}

internal sealed record RecordedFrame(int I, double TMs, double CaptureMs, int W, int H, bool Fallback);

internal sealed record CoverDto(
    int Id, bool New, double X, double Y, double W, double H, int PixelW, int PixelH,
    bool Soft, Box Anchor, double BuildMs, double MaskFraction);

internal sealed record BlockDto(
    string Key, Box Box, string Text, bool Same, string? Src, int Misses, int LineHeight, CoverDto? Cover);

internal sealed record DiagDto(
    double CaptureToUpdateMs, long CaptureMs, long OcrMs, double? OcrOperationMs, long TranslateMs,
    int OcrWidth, int OcrHeight, int RawLines, int RecognizedBlocks, int ReusedBlocks, int RetainedBlocks,
    int DisplayedBlocks, bool PartialOcr, bool SceneCut, bool WhiffSuspected, bool UsedScreenFallback,
    int OcrSceneChecks, double OcrSceneCheckMs, int TranslationSceneChecks, double TranslationSceneCheckMs,
    double GlyphCoverMs, double GlyphCoverWaitMs);

internal sealed record UpdateDto(
    int Seq, double EmitMs, double AppliedMs, string Status, bool Clear, bool Hide, bool Stopped,
    bool ClearSubtitle, string? Subtitle, string? Notice, Box Bounds, DiagDto? Diag, IReadOnlyList<BlockDto>? Blocks);

internal sealed record ElementDto(string? Key, Box Box, double Opacity, int Element, bool Native);

internal sealed record ShownDto(
    int I, double DueMs, double SetMs, double RenderMs, double DecodeWaitMs, double SnapMs,
    bool OverlayVisible, int Layer, int UpdateSeq, IReadOnlyList<ElementDto> Elements);

internal sealed record LayerDto(int Id, Box Box, double Ms, string File);

internal sealed record PatchDto(
    int Id, int PixelW, int PixelH, double W, double H, bool Soft, double BuildMs, double MaskFraction,
    int FirstSeq, double FirstEmitMs, double? CaptureAtMs, string File);

internal sealed record OcrDto(int N, double StartMs, double EndMs, int W, int H, int Lines, bool Failed, double? Angle);

internal sealed record FidelityDto(int CapturedW, int CapturedH, bool Fallback, double MeanAbsDiff, double DiffFraction);

internal sealed record SessionOptionsDto(
    double Fps, double ChangeThreshold, double MotionThreshold, double MaxMotionPauseMs, double SceneCutThreshold,
    double StabilityDelayMs, double ForcedProcessIntervalMs, double StaticRescanIntervalMs, int BlockMissGrace,
    double OcrUpscale, bool AllowAutoUpscale, bool BuildGlyphCovers, bool HoldTypingPrefixes, bool IdentityEchoSafe,
    bool CaptureExclusionActive);

internal sealed record ReplayHeaderDto(
    string Recording, string RecordingName, int Frames, int FrameW, int FrameH, double Speed, int ProviderDelayMs,
    string? Cache, string Corpus, string Profile, string Placement, string Live, double Opacity, double FontSize,
    string FontFamily, int RenderEvery, double CompositeScale, bool OverlayShown, DateTime Started,
    Box Monitor, double DpiScale, int OcrMaxDimension, FidelityDto? Fidelity, SessionOptionsDto Session,
    bool SourceTextReflection, bool OverlayElementsReflection, string Tool);

internal sealed record ReplaySummaryDto(
    ReplayHeaderDto Header, double PlaybackMs, int FramesShown, double LateSetMedianMs, double LateSetP90Ms,
    double LateSetMaxMs, double LateRenderMedianMs, double LateRenderP90Ms, double LateRenderMaxMs,
    int DecodeStalls, double DecodeStallMaxMs, int Updates, int OcrCalls, int AngleProbes, int Layers, int Patches,
    int CompositesWritten, int CompositesDropped, double SessionStopMs, bool SessionStoppedCleanly, double WallSeconds,
    string? Error);

internal sealed record TruthRowDto(string Text, string Norm, Box Box);

internal sealed record TruthBlockDto(string Text, string Norm, Box Box, bool Live, string? Identity, string KeyHash, int Lines, IReadOnlyList<TruthRowDto> Rows);

internal sealed record TruthFrameDto(
    int I, double TMs, double? Angle, double Changed, double Strong, double MeanDiff, double OcrMs,
    IReadOnlyList<TruthBlockDto> Blocks);

internal sealed record TruthHeaderDto(
    string Type, int Version, int Frames, int Every, string Profile, string CorpusFile, long CorpusLength,
    string Language, int OcrMaxDimension, double Downscale, bool AngleCorrected, DateTime Created);

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Line<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static IEnumerable<T> ReadLines<T>(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            yield return JsonSerializer.Deserialize<T>(line, Options)
                ?? throw new InvalidDataException($"Pusty wiersz JSON w {path}.");
        }
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"Pusty JSON: {path}.");

    public static void Write<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Indented), new UTF8Encoding(false));

    public static double R(double value) => Math.Round(value, 1);

    public static double R3(double value) => Math.Round(value, 3);
}

internal sealed class JsonlWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private bool _disposed;

    public JsonlWriter(string path)
    {
        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
    }

    public int Count { get; private set; }

    public void Write<T>(T value)
    {
        var line = Json.Line(value);
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
            Count++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}

internal static class Stats
{
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        var position = (sorted.Count - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    public static (double Median, double P90, double Max, int Count) Summary(IEnumerable<double> values)
    {
        var sorted = values.Where(static v => !double.IsNaN(v)).Order().ToList();
        if (sorted.Count == 0) return (double.NaN, double.NaN, double.NaN, 0);
        return (Quantile(sorted, 0.5), Quantile(sorted, 0.9), sorted[^1], sorted.Count);
    }
}
