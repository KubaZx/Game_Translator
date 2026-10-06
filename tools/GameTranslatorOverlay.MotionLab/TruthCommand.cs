using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;
using Microsoft.Extensions.Logging;

internal sealed record TruthOptions(int Every, int Workers, bool Force, string Corpus, string Profile);

internal static class TruthCommand
{
    public const int FormatVersion = 3;

    private sealed record RawFrame(int Index, double? Angle, double OcrMs, float[] Grid, IReadOnlyList<TextBlock> Blocks);

    public static int Run(string recordingName, TruthOptions options)
    {
        var recording = Recording.Load(recordingName);
        var corpusInfo = new FileInfo(options.Corpus);
        if (!corpusInfo.Exists) throw new ArgumentException($"Nie ma korpusu: {options.Corpus}");
        var language = AngleAwareOcr.FindLanguage("en") ?? throw new InvalidOperationException("Brak pakietu Windows OCR dla „en”.");
        var maxDimension = AngleAwareOcr.MaxImageDimension;
        var downscale = OcrScaling.ComputeDownscale(recording.Width, recording.Height, maxDimension);
        var expected = new TruthHeaderDto("header", FormatVersion, recording.Frames.Count, options.Every, options.Profile,
            corpusInfo.Name, corpusInfo.Length, language.LanguageTag, maxDimension, downscale, true, DateTime.Now);

        if (!options.Force && IsComplete(recording, expected))
        {
            Console.WriteLine($"truth: używam gotowego {recording.TruthPath}");
            return 0;
        }

        var watch = Stopwatch.StartNew();
        var indices = Enumerable.Range(0, recording.Frames.Count).Where(i => i % options.Every == 0).ToList();
        Console.WriteLine($"truth: {recording.Name} — {indices.Count} klatek {recording.Width}×{recording.Height}, OCR {language.LanguageTag}, " +
            $"limit silnika {maxDimension} px, skala {downscale:0.###}, {options.Workers} wątki");

        var raw = new RawFrame?[recording.Frames.Count];
        var engines = new ConcurrentBag<AngleAwareOcr>();
        using (var decodePool = new StaPool(options.Workers, options.Workers * 2, "truth-decode"))
        {
            using var gate = new SemaphoreSlim(options.Workers);
            var done = 0;
            var tasks = indices.Select(async index =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (bitmap, grid) = await decodePool.Run(() => Prepare(recording.FramePath(index), downscale)).ConfigureAwait(false);
                    if (!engines.TryTake(out var engine)) engine = new AngleAwareOcr(language.LanguageTag);
                    try
                    {
                        var ocrWatch = Stopwatch.StartNew();
                        var (lines, angle) = await engine.RecognizeAsync(bitmap).ConfigureAwait(false);
                        var ocrMs = ocrWatch.Elapsed.TotalMilliseconds;
                        if (Math.Abs(downscale - 1.0) > 0.001)
                        {
                            var back = 1.0 / downscale;
                            lines = lines.Select(l => new OcrLine(l.Text, l.Box.Scale(back),
                                l.Words.Select(w => new OcrWord(w.Text, w.Box.Scale(back))).ToList())).ToList();
                        }
                        raw[index] = new RawFrame(index, angle, ocrMs, grid, TextBlockGrouper.Group(lines));
                    }
                    finally
                    {
                        engines.Add(engine);
                    }
                    var count = Interlocked.Increment(ref done);
                    if (count % 50 == 0 || count == indices.Count)
                        Console.WriteLine($"  OCR {count}/{indices.Count} ({watch.Elapsed.TotalSeconds:0} s)");
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }

        var root = Path.Combine(Path.GetTempPath(), "gto-motionlab-truth-" + Guid.NewGuid().ToString("N"));
        using var loggers = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var environment = LabEnvironment.Create(root,
            new LabOptions(null, options.Corpus, options.Profile, 0, "cover", "at-source", 0.4, 0, "auto"),
            new WindowsOcrProvider(), loggers, keep: false);
        var orchestrator = environment.Orchestrator;

        var temp = recording.TruthPath + ".tmp";
        using (var writer = new JsonlWriter(temp))
        {
            writer.Write(expected);
            float[]? previous = null;
            var angled = 0;
            foreach (var index in indices)
            {
                var frame = raw[index] ?? throw new InvalidOperationException($"Brak wyniku OCR klatki {index}.");
                double changed = 0, strong = 0, meanDiff = 0;
                if (previous is not null)
                {
                    var c = 0;
                    var s = 0;
                    double sum = 0;
                    for (var k = 0; k < frame.Grid.Length; k++)
                    {
                        var delta = Math.Abs(frame.Grid[k] - previous[k]);
                        sum += delta;
                        if (delta > 10) c++;
                        if (delta > 25) s++;
                    }
                    changed = (double)c / frame.Grid.Length;
                    strong = (double)s / frame.Grid.Length;
                    meanDiff = sum / frame.Grid.Length;
                }
                previous = frame.Grid;
                if (frame.Angle is { } a && Math.Abs(a) > 0.05) angled++;
                var blocks = frame.Blocks.Select(block =>
                {
                    var norm = TextNormalizer.Normalize(block.Text);
                    var identity = orchestrator.CorpusIdentity(norm);
                    return new TruthBlockDto(block.Text, norm, Box.From(block.Box), orchestrator.ShouldTranslateLive(block.Text),
                        identity, TextHasher.Sha256Hex(identity ?? norm)[..16], block.Lines.Count, Rows(block));
                }).ToList();
                writer.Write(new TruthFrameDto(index, Json.R(recording.RelativeMs(index)), frame.Angle is { } angle ? Math.Round(angle, 2) : null,
                    Json.R3(changed), Json.R3(strong), Json.R(meanDiff), Json.R(frame.OcrMs), blocks));
            }
            Console.WriteLine($"truth: {indices.Count} klatek, z kątem tekstu {angled}, {watch.Elapsed.TotalSeconds:0} s → {recording.TruthPath}");
        }
        File.Move(temp, recording.TruthPath, overwrite: true);
        return 0;
    }

    public static IReadOnlyList<TruthRowDto> Rows(TextBlock block)
    {
        var byTop = block.Lines.OrderBy(static l => l.Box.Y).ThenBy(static l => l.Box.X).ToList();
        var rows = new List<List<OcrLine>>();
        var rowTop = 0;
        var rowBottom = 0;
        foreach (var line in byTop)
        {
            if (rows.Count > 0)
            {
                var overlap = Math.Min(rowBottom, line.Box.Bottom) - Math.Max(rowTop, line.Box.Y);
                var reference = Math.Max(1, Math.Min(rowBottom - rowTop, line.Box.Height));
                if (overlap >= reference * 0.5)
                {
                    rows[^1].Add(line);
                    rowTop = Math.Min(rowTop, line.Box.Y);
                    rowBottom = Math.Max(rowBottom, line.Box.Bottom);
                    continue;
                }
            }
            rows.Add([line]);
            rowTop = line.Box.Y;
            rowBottom = line.Box.Bottom;
        }
        return rows.Select(static row =>
        {
            var ordered = row.OrderBy(static l => l.Box.X).ToList();
            var text = string.Join(' ', ordered.Select(static l => l.Text));
            var box = ordered.Aggregate(default(RectPx), static (acc, l) => acc.Union(l.Box));
            return new TruthRowDto(text, TextNormalizer.Normalize(text), Box.From(box));
        }).ToList();
    }

    private static (OcrBitmap Bitmap, float[] Grid) Prepare(string path, double downscale)
    {
        var decoded = Imaging.DecodeJpeg(path);
        var full = Imaging.ToOcrBitmap(decoded);
        var grid = LuminanceGrid.FromBgra32(full.PixelsBgra32, full.Width, full.Height, full.Stride).Cells;
        if (Math.Abs(downscale - 1.0) <= 0.001) return (full, grid);
        var scaled = new TransformedBitmap(decoded, new ScaleTransform(downscale, downscale));
        scaled.Freeze();
        return (Imaging.ToOcrBitmap(scaled), grid);
    }

    public static bool IsComplete(Recording recording, TruthHeaderDto expected)
    {
        if (!File.Exists(recording.TruthPath)) return false;
        try
        {
            using var reader = new StreamReader(recording.TruthPath);
            var first = reader.ReadLine();
            if (first is null) return false;
            var header = System.Text.Json.JsonSerializer.Deserialize<TruthHeaderDto>(first, Json.Options);
            if (header is null || header.Version != expected.Version || header.Frames != expected.Frames || header.Every != expected.Every
                || header.Profile != expected.Profile || header.CorpusFile != expected.CorpusFile || header.CorpusLength != expected.CorpusLength
                || !header.AngleCorrected)
                return false;
            var lines = 0;
            while (reader.ReadLine() is { Length: > 0 }) lines++;
            return lines == (expected.Frames + expected.Every - 1) / expected.Every;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static (TruthHeaderDto Header, IReadOnlyList<TruthFrameDto> Frames) Load(string path)
    {
        using var reader = new StreamReader(path);
        var first = reader.ReadLine() ?? throw new InvalidDataException($"Pusty plik prawdy: {path}");
        var header = System.Text.Json.JsonSerializer.Deserialize<TruthHeaderDto>(first, Json.Options)
            ?? throw new InvalidDataException($"Brak nagłówka prawdy: {path}");
        var frames = new List<TruthFrameDto>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            frames.Add(System.Text.Json.JsonSerializer.Deserialize<TruthFrameDto>(line, Json.Options)
                ?? throw new InvalidDataException($"Pusty wiersz prawdy: {path}"));
        }
        return (header, frames);
    }
}
