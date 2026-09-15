using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.LiveDiag;

/// <summary>Only numeric/boolean diagnostic fields enter JSONL. Never serialize LiveUpdate itself.</summary>
internal sealed class LiveDiagReport : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(double AtMs, LiveFrameDiagnostics Frame)> _frames = [];
    private readonly StreamWriter? _writer;
    private bool _finished;
    private int _stopped;
    private int _writeFailed;
    private int _callbacks;

    public LiveDiagReport(string? outputPath)
    {
        if (outputPath is null) return;
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    public UsageTracker Usage { get; } = new();

    public bool SessionStopped => Volatile.Read(ref _stopped) != 0;
    public bool WriteFailed => Volatile.Read(ref _writeFailed) != 0;
    public int FrameCount { get { lock (_gate) return _frames.Count; } }

    public void Header(object configuration)
    {
        lock (_gate)
            Write(new { type = "configuration", schemaVersion = 1, startedUtc = DateTimeOffset.UtcNow,
                elapsedMs = _clock.Elapsed.TotalMilliseconds, configuration });
    }

    public void Record(LiveUpdate update, bool includeText)
    {
        lock (_gate)
        {
            if (_finished) return;
            var atMs = _clock.Elapsed.TotalMilliseconds;
            _callbacks++;
            if (update.Stopped) Interlocked.Exchange(ref _stopped, 1);
            if (update.Diagnostics is { } diagnostics) _frames.Add((atMs, diagnostics));
            Write(new { type = "update", elapsedMs = atMs, diagnostics = update.Diagnostics,
                displayedBlocks = update.Blocks?.Count, hide = update.HideOverlay,
                clear = update.ClearOverlay, stopped = update.Stopped });
            if (update.Diagnostics is { } frame)
                Console.WriteLine($"[{atMs / 1000,7:0.000}s] OCR {frame.OcrMs} ms / {frame.RawLines} linii; " +
                    $"capture→update {frame.CaptureToUpdateMs:0.0} ms; bloki {frame.DisplayedBlocks}; " +
                    $"partial={frame.PartialOcr} whiffSuspected={frame.WhiffSuspected}");
            else if (update.HideOverlay || update.ClearOverlay || update.Stopped)
                Console.WriteLine($"[{atMs / 1000,7:0.000}s] hide={update.HideOverlay} clear={update.ClearOverlay} stopped={update.Stopped}");
            if (includeText)
            {
                Console.WriteLine(update.StatusLine);
                if (update.Blocks is { Count: > 0 })
                    foreach (var block in update.Blocks.Take(12))
                    {
                        var text = block.TranslatedText.Length > 60 ? block.TranslatedText[..60] + "…" : block.TranslatedText;
                        Console.WriteLine($"  → ({block.ScreenBox.X},{block.ScreenBox.Y} {block.ScreenBox.Width}×{block.ScreenBox.Height}) \"{text.Replace('\n', '|')}\"");
                    }
            }
        }
    }

    public int Finish(int exitCode, string reason)
    {
        lock (_gate)
        {
            if (_finished) return WriteFailed ? 5 : exitCode;
            _finished = true;
            var frames = _frames.Select(static entry => entry.Frame).ToList();
            var intervals = _frames.Zip(_frames.Skip(1), static (left, right) => right.AtMs - left.AtMs);
            var summary = new
            {
                callbacks = _callbacks,
                mockProviderRequests = Usage.ApiRequests,
                mockProviderCharacters = Usage.ApiCharacters,
                cacheHits = Usage.CacheHits,
                glossaryHits = Usage.GlossaryHits,
                failedProviderRequests = Usage.FailedRequests,
                usageMeaning = "Mock provider counters, not DeepL billing; cache starts empty for each run.",
                completedOcr = frames.Count,
                fullOcr = frames.Count(static frame => !frame.PartialOcr),
                partialOcr = frames.Count(static frame => frame.PartialOcr),
                whiffSuspicions = frames.Count(static frame => frame.WhiffSuspected),
                captureToUpdateMs = Percentiles(frames.Select(static frame => frame.CaptureToUpdateMs)),
                captureMs = Percentiles(frames.Select(static frame => (double)frame.CaptureMs)),
                ocrMs = Percentiles(frames.Select(static frame => (double)frame.OcrMs)),
                ocrOperationMs = Percentiles(frames.Where(static f => f.OcrOperationMs.HasValue).Select(static f => f.OcrOperationMs!.Value)),
                ocrSceneChecks = frames.Sum(static frame => frame.OcrSceneChecks),
                ocrSceneCheckMs = Percentiles(frames.Select(static frame => frame.OcrSceneCheckMs)),
                translationSceneChecks = frames.Sum(static frame => frame.TranslationSceneChecks),
                translationSceneCheckMs = Percentiles(frames.Select(static frame => frame.TranslationSceneCheckMs)),
                translateMs = Percentiles(frames.Select(static frame => (double)frame.TranslateMs)),
                completedOcrCallbackIntervalMs = Percentiles(intervals),
                intervalMeaning = "Time between callbacks of completed OCR passes; not source-to-overlay latency.",
                captureToUpdateMeaning = "Capture through diagnostic update; excludes source appearance and UI presentation.",
            };
            Console.WriteLine("== Podsumowanie (ms, percentyle nearest-rank) ==");
            Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
            if (WriteFailed) { exitCode = 5; reason = "output_write_failed"; }
            Write(new { type = "end", elapsedMs = _clock.Elapsed.TotalMilliseconds, exitCode, reason, summary });
            return WriteFailed ? 5 : exitCode;
        }
    }

    private static object Percentiles(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        double? At(double quantile) => ordered.Length == 0 ? null : ordered[(int)Math.Ceiling(quantile * ordered.Length) - 1];
        return new { count = ordered.Length, p50 = At(0.50), p95 = At(0.95) };
    }

    private void Write(object value)
    {
        if (_writer is null || WriteFailed) return;
        try { _writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions)); }
        catch (IOException) { Interlocked.Exchange(ref _writeFailed, 1); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _finished = true;
            try { _writer?.Dispose(); }
            catch (IOException) { Interlocked.Exchange(ref _writeFailed, 1); }
        }
    }
}
