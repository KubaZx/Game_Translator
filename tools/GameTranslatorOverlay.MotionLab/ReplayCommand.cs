using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Vision;
using Microsoft.Extensions.Logging;

internal sealed record ReplayOptions(
    string Out, double Speed, int ProviderDelayMs, string? Cache, string Corpus, string Profile, string Placement,
    string Live, double Opacity, double FontSize, string FontFamily, int RenderEvery, double CompositeScale,
    bool ShowOverlay, bool Analyze, int TailMs, int MaxFrames, bool KeepWork, bool AngleProbe, int Lookahead);

internal static class ReplayCommand
{
    public static int Run(string recordingName, ReplayOptions options)
    {
        var recording = Recording.Load(recordingName);
        if (Directory.Exists(options.Out) && Directory.EnumerateFileSystemEntries(options.Out).Any())
            throw new ArgumentException($"Katalog wyjściowy musi być pusty: {options.Out}");
        Directory.CreateDirectory(options.Out);
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var runner = new ReplayRunner(recording, options);
        var frame = new DispatcherFrame();
        var exit = 0;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                exit = await runner.RunAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"replay: błąd {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exit = 5;
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        if (exit == 0 && options.Analyze)
        {
            exit = AnalyzeCommand.Run(options.Out, new AnalyzeOptions(null, 15));
        }
        return exit;
    }
}

internal sealed class ShownBuilder
{
    public int Index;
    public double Due;
    public double Set = -1;
    public double Render = -1;
    public double DecodeWait;
    public double Snap = -1;
    public bool Visible;
    public int Layer;
    public int Seq;
    public IReadOnlyList<ElementDto> Elements = [];

    public ShownDto ToDto() => new(Index, Json.R(Due), Json.R(Set), Json.R(Render), Json.R(DecodeWait), Json.R(Snap),
        Visible, Layer, Seq, Elements);
}

internal sealed class ReplayRunner(Recording recording, ReplayOptions options)
{
    private static readonly FieldInfo? DisplayedField =
        typeof(LiveTranslationSession).GetField("_displayed", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Stopwatch _clock = new();
    private readonly ConditionalWeakTable<byte[], StrongBox<int>> _patchIds = new();
    private readonly OverlayNoticeEcho _echo = new();
    private int _nextPatchId;
    private int _seq;
    private int _compositesWritten;
    private int _compositesDropped;
    private int _layerCount;
    private LiveTranslationSession? _session;
    private OverlayHost? _host;
    private ReplayWindow? _replay;
    private Dispatcher? _overlayDispatcher;
    private JsonlWriter? _updates;
    private JsonlWriter? _layers;
    private JsonlWriter? _patches;
    private StaPool? _compositePool;
    private StaPool? _ioPool;
    private ShownBuilder[] _shown = [];
    private int _frameCount;

    private double Now => _clock.Elapsed.TotalMilliseconds;

    private string OutPath(params string[] parts) => Path.Combine([options.Out, .. parts]);

    public async Task<int> RunAsync()
    {
        var wall = Stopwatch.StartNew();
        _frameCount = options.MaxFrames > 0 ? Math.Min(options.MaxFrames, recording.Frames.Count) : recording.Frames.Count;
        Directory.CreateDirectory(OutPath("frames"));
        Directory.CreateDirectory(OutPath("layers"));
        Directory.CreateDirectory(OutPath("patches"));
        _overlayDispatcher = Dispatcher.CurrentDispatcher;
        _shown = Enumerable.Range(0, _frameCount)
            .Select(i => new ShownBuilder { Index = i, Due = recording.RelativeMs(i) / options.Speed })
            .ToArray();

        using var loggerProvider = new FileLoggerProvider(OutPath("session.log"));
        using var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));
        using var ocrWriter = new JsonlWriter(OutPath("ocr.jsonl"));
        _updates = new JsonlWriter(OutPath("updates.jsonl"));
        _layers = new JsonlWriter(OutPath("layers.jsonl"));
        _patches = new JsonlWriter(OutPath("patches.jsonl"));
        _compositePool = new StaPool(3, 24, "composite", ThreadPriority.BelowNormal);
        _ioPool = new StaPool(2, 512, "io", ThreadPriority.BelowNormal);
        using var decodePool = new StaPool(3, options.Lookahead + 6, "decode", ThreadPriority.AboveNormal);
        var feed = new FrameFeed(recording, decodePool, options.Lookahead, _frameCount);

        var windowsOcr = new WindowsOcrProvider();
        var ocr = new LoggingOcr(windowsOcr, () => Now, ocrWriter, options.AngleProbe);
        var labOptions = new LabOptions(options.Cache, options.Corpus, options.Profile, options.ProviderDelayMs,
            options.Placement, options.Live, options.Opacity, options.FontSize, options.FontFamily);
        using var environment = LabEnvironment.Create(OutPath("app-data"), labOptions, ocr, loggers, options.KeepWork);
        var settings = environment.Settings;
        var orchestrator = environment.Orchestrator;
        var profile = orchestrator.ActiveProfile;

        string? error = null;
        var stoppedCleanly = false;
        double stopMs = 0;
        FidelityDto? fidelity = null;
        ReplayHeaderDto? header = null;
        try
        {
            if (!windowsOcr.IsLanguageAvailable(settings.SourceLanguage))
                throw new InvalidOperationException("Brak pakietu Windows OCR dla „en”.");

            _replay = await ReplayWindow.CreateAsync(recording.Width, recording.Height, () => Now, OnRendered);
            var bounds = ScreenCapture.GetWindowBounds(_replay.Handle);
            var monitor = Displays.FromRect(bounds);
            _host = new OverlayHost(settings, orchestrator, _echo, () => ScreenCapture.GetWindowBounds(_replay.Handle),
                options.ShowOverlay, monitor.Bounds.Width, monitor.Bounds.Height)
            {
                Scale = monitor.Scale,
            };
            _host.Window.EnsureHandleCreated();
            _host.Window.ProfileFontFamily = profile?.Overlay?.FontFamily;

            var coverMode = settings.OverlayPlacement == "cover";
            if (coverMode)
            {
                OverlayFonts.WarmUp(OverlayFonts.ResolveFamilyName(settings, _host.Window.ProfileFontFamily));
                await Task.Run(GlyphCoverBuilder.WarmUp);
            }
            orchestrator.WarmUpActiveProvider();
            await Task.Run(() => WarmUpOcrAsync(windowsOcr));

            var first = await feed.Get(0);
            await _replay.ShowAsync(first, -1);
            await Task.Delay(150);
            fidelity = await Task.Run(() => MeasureFidelity(_replay.Handle, first));
            Console.WriteLine($"replay: okno {bounds}, monitor {monitor.Bounds.Width}×{monitor.Bounds.Height} ×{monitor.Scale:0.##}, " +
                $"wierność przechwycenia: {fidelity.CapturedW}×{fidelity.CapturedH}, średnia różnica {fidelity.MeanAbsDiff:0.00}, " +
                $"inne piksele {fidelity.DiffFraction:P2}{(fidelity.Fallback ? ", ZRZUT EKRANU" : string.Empty)}");
            if (fidelity.CapturedW != recording.Width || fidelity.CapturedH != recording.Height)
                Console.Error.WriteLine("replay: UWAGA — przechwycone okno ma inny rozmiar niż klatki nagrania.");

            var upscale = OcrScaling.ResolvePreference(profile?.Ocr?.Upscale, settings.OcrUpscale);
            var sessionOptions = new LiveSessionOptions
            {
                Fps = profile?.ChangeDetection?.Fps ?? 6,
                ChangeThreshold = profile?.ChangeDetection?.Threshold ?? 0.0,
                OcrUpscale = upscale.Preferred,
                AllowAutoUpscale = upscale.AllowAuto,
                NoticeEcho = _echo,
                BuildGlyphCovers = () => settings.OverlayPlacement == "cover" && settings.LiveDisplayMode != "subtitle",
                HoldTypingPrefixes = () => settings.OverlayPlacement == "cover" && settings.LiveDisplayMode != "subtitle",
                IdentityEchoSafe = () => (OverlayBlockRenderer.HidesIdenticalText(settings) && settings.LiveDisplayMode != "subtitle")
                    || _host.Window.IsCaptureExclusionActive,
                IgnoreRegions = profile?.Live?.IgnoreRegions ?? [],
                EnableDiagnostics = true,
            };
            header = new ReplayHeaderDto(recording.Directory, recording.Name, _frameCount, recording.Width, recording.Height,
                options.Speed, options.ProviderDelayMs, options.Cache, options.Corpus, options.Profile, options.Placement,
                options.Live, options.Opacity, options.FontSize, options.FontFamily, options.RenderEvery, options.CompositeScale,
                options.ShowOverlay, DateTime.Now, Box.From(monitor.Bounds), monitor.Scale, windowsOcr.MaxImageDimension, fidelity,
                new SessionOptionsDto(sessionOptions.Fps, sessionOptions.ChangeThreshold, sessionOptions.MotionThreshold,
                    sessionOptions.MaxMotionPause.TotalMilliseconds, sessionOptions.SceneCutThreshold,
                    sessionOptions.StabilityDelay.TotalMilliseconds, sessionOptions.ForcedProcessInterval.TotalMilliseconds,
                    sessionOptions.StaticRescanInterval.TotalMilliseconds, sessionOptions.BlockMissGrace, sessionOptions.OcrUpscale,
                    sessionOptions.AllowAutoUpscale, sessionOptions.BuildGlyphCovers(), sessionOptions.HoldTypingPrefixes(),
                    sessionOptions.IdentityEchoSafe(), _host.Window.IsCaptureExclusionActive),
                DisplayedField is not null, _host.ElementsReflection,
                typeof(ReplayRunner).Assembly.GetName().Version?.ToString() ?? "?");
            Json.Write(OutPath("replay.json"), header);

            _session = new LiveTranslationSession(orchestrator, ocr, _replay.Handle, sessionOptions, OnUpdate,
                loggers.CreateLogger("LiveTranslationSession"));
            var pacerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pacer = new Thread(() => Pace(feed, pacerDone))
            {
                IsBackground = true,
                Name = "pacer",
                Priority = ThreadPriority.Highest,
            };
            Console.WriteLine($"replay: {recording.Name} — {_frameCount} klatek, {recording.RelativeMs(_frameCount - 1) / options.Speed / 1000:0.0} s, " +
                $"Mock {options.ProviderDelayMs} ms, {settings.OverlayPlacement}/{settings.LiveDisplayMode}");
            _clock.Start();
            _session.Start();
            pacer.Start();
            await pacerDone.Task;

            var stopWatch = Stopwatch.StartNew();
            _session.Stop();
            try
            {
                await _session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
                stoppedCleanly = true;
            }
            catch (TimeoutException)
            {
                error = "Sesja nie zakończyła się w 20 s.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = $"Sesja zakończyła się błędem {ex.GetType().Name}.";
            }
            stopMs = stopWatch.Elapsed.TotalMilliseconds;
            _session.Dispose();
            await _overlayDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            if (_host.Stopped && error is null) error = "Sesja zatrzymała się sama (Stopped).";
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"replay: {error}");
            _session?.Stop();
            _session?.Dispose();
        }
        finally
        {
            if (_replay is not null)
            {
                try { await _replay.CloseAsync(); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { error ??= "Okno odtwarzania nie zamknęło się."; }
            }
            _host?.Close();
            _compositePool.Complete(TimeSpan.FromMinutes(3));
            _ioPool.Complete(TimeSpan.FromMinutes(3));
            decodePool.Complete(TimeSpan.FromSeconds(30));
            _updates.Dispose();
            _layers.Dispose();
            _patches.Dispose();
        }

        using (var shownWriter = new JsonlWriter(OutPath("shown.jsonl")))
        {
            foreach (var shown in _shown) shownWriter.Write(shown.ToDto());
        }
        var late = _shown.Where(static s => s.Set >= 0).Select(static s => s.Set - s.Due).ToList();
        var lateRender = _shown.Where(static s => s.Render >= 0).Select(static s => s.Render - s.Due).ToList();
        var lateSummary = Stats.Summary(late);
        var renderSummary = Stats.Summary(lateRender);
        var stalls = _shown.Where(static s => s.DecodeWait > 1).ToList();
        if (header is not null)
        {
            var summary = new ReplaySummaryDto(header, Json.R(Now), _shown.Count(static s => s.Set >= 0),
                Json.R(lateSummary.Median), Json.R(lateSummary.P90), Json.R(lateSummary.Max),
                Json.R(renderSummary.Median), Json.R(renderSummary.P90), Json.R(renderSummary.Max),
                stalls.Count, Json.R(stalls.Count == 0 ? 0 : stalls.Max(static s => s.DecodeWait)),
                _updates.Count, ocr.Calls, ocr.Probes, _layerCount, _nextPatchId, _compositesWritten, _compositesDropped,
                Json.R(stopMs), stoppedCleanly, Math.Round(wall.Elapsed.TotalSeconds, 1), error);
            Json.Write(OutPath("replay.json"), summary);
            Console.WriteLine($"replay: koniec — {summary.FramesShown} klatek, spóźnienie podmiany mediana {summary.LateSetMedianMs} ms (p90 {summary.LateSetP90Ms}, max {summary.LateSetMaxMs}), " +
                $"aktualizacji {summary.Updates}, OCR {summary.OcrCalls}, kompozytów {summary.CompositesWritten} (pominiętych {summary.CompositesDropped}), " +
                $"{summary.WallSeconds:0} s{(error is null ? string.Empty : ", BŁĄD: " + error)}");
        }
        return error is null ? 0 : 4;
    }

    private static async Task WarmUpOcrAsync(IOcrProvider ocr)
    {
        const int width = 320;
        const int height = 80;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 20;
            pixels[i + 3] = 255;
        }
        await ocr.RecognizeAsync(new OcrBitmap(pixels, width, height, width * 4), "en");
    }

    private static FidelityDto MeasureFidelity(IntPtr handle, BitmapSource frame)
    {
        var (bitmap, fallback) = ScreenCapture.CaptureWindowEx(handle);
        if (bitmap is null) return new FidelityDto(0, 0, fallback, double.NaN, double.NaN);
        using (bitmap)
        {
            var captured = ScreenCapture.ToOcrBitmap(bitmap);
            var expected = Imaging.ToOcrBitmap(frame);
            if (captured.Width != expected.Width || captured.Height != expected.Height)
                return new FidelityDto(captured.Width, captured.Height, fallback, double.NaN, double.NaN);
            double sum = 0;
            long samples = 0;
            long different = 0;
            for (var y = 0; y < captured.Height; y += 3)
            {
                for (var x = 0; x < captured.Width; x += 3)
                {
                    var p = y * captured.Stride + x * 4;
                    var q = y * expected.Stride + x * 4;
                    var max = 0;
                    for (var c = 0; c < 3; c++)
                    {
                        var d = Math.Abs(captured.PixelsBgra32[p + c] - expected.PixelsBgra32[q + c]);
                        sum += d;
                        max = Math.Max(max, d);
                    }
                    samples++;
                    if (max > 3) different++;
                }
            }
            return new FidelityDto(captured.Width, captured.Height, fallback, Math.Round(sum / (samples * 3.0), 3),
                Math.Round((double)different / samples, 5));
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private void Pace(FrameFeed feed, TaskCompletionSource done)
    {
        timeBeginPeriod(1);
        try
        {
            for (var i = 0; i < _frameCount; i++)
            {
                var shown = _shown[i];
                WaitUntil(shown.Due);
                var task = feed.Get(i);
                if (!task.IsCompleted)
                {
                    var waitStart = Now;
                    task.Wait();
                    shown.DecodeWait = Now - waitStart;
                }
                var frame = task.Result;
                var index = i;
                _replay!.Dispatcher.InvokeAsync(() =>
                {
                    _replay.SetFrame(frame, index);
                    shown.Set = Now;
                    _overlayDispatcher!.InvokeAsync(() => Snapshot(shown, frame));
                }, DispatcherPriority.Send);
                feed.Release(i);
            }
            var last = _shown[_frameCount - 1].Due;
            WaitUntil(last + Math.Max(options.TailMs, recording.NominalIntervalMs));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"replay: błąd odtwarzania {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            timeEndPeriod(1);
            done.TrySetResult();
        }
    }

    private void WaitUntil(double dueMs)
    {
        while (true)
        {
            var left = dueMs - Now;
            if (left <= 0) return;
            if (left > 3) Thread.Sleep((int)(left - 2));
            else Thread.SpinWait(200);
        }
    }

    private void OnRendered(int index, double ms)
    {
        if (index >= 0 && index < _shown.Length && _shown[index].Render < 0) _shown[index].Render = ms;
    }

    private void Snapshot(ShownBuilder shown, BitmapSource frame)
    {
        if (_host is null) return;
        shown.Snap = Now;
        shown.Seq = _host.LastSeq;
        var (visible, layer, elements, _) = _host.Snapshot(SaveLayer);
        shown.Visible = visible;
        shown.Layer = layer?.Id ?? 0;
        shown.Elements = elements;
        if (shown.Index % options.RenderEvery != 0) return;
        var path = OutPath("frames", $"c{shown.Index:D5}.jpg");
        var scale = options.CompositeScale;
        if (_compositePool!.TryPost(() =>
            {
                Composite(frame, layer, scale, path);
                Interlocked.Increment(ref _compositesWritten);
            }))
            return;
        Interlocked.Increment(ref _compositesDropped);
    }

    private void SaveLayer(OverlayLayer layer)
    {
        Interlocked.Increment(ref _layerCount);
        var file = $"L{layer.Id:D5}.png";
        _layers!.Write(new LayerDto(layer.Id, layer.Box, Json.R(Now), file));
        _ioPool!.Post(() => Imaging.SavePng(layer.Bitmap, OutPath("layers", file)));
    }

    private static void Composite(BitmapSource frame, OverlayLayer? layer, double scale, string path)
    {
        BitmapSource scaled = Math.Abs(scale - 1.0) < 1e-6 ? frame : new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        if (layer is not null)
        {
            BitmapSource layerScaled = Math.Abs(scale - 1.0) < 1e-6
                ? layer.Bitmap
                : new TransformedBitmap(layer.Bitmap, new ScaleTransform(scale, scale));
            var lw = layerScaled.PixelWidth;
            var lh = layerScaled.PixelHeight;
            var layerPixels = new byte[lw * lh * 4];
            layerScaled.CopyPixels(layerPixels, lw * 4, 0);
            var ox = (int)Math.Round(layer.Box.X * scale);
            var oy = (int)Math.Round(layer.Box.Y * scale);
            Blend(pixels, width, height, layerPixels, lw, lh, ox, oy);
        }
        Imaging.SaveJpeg(Imaging.FromBgra(pixels, width, height, stride), path, 85);
    }

    public static void Blend(byte[] target, int width, int height, byte[] premultiplied, int lw, int lh, int ox, int oy)
    {
        for (var y = 0; y < lh; y++)
        {
            var ty = oy + y;
            if (ty < 0 || ty >= height) continue;
            for (var x = 0; x < lw; x++)
            {
                var tx = ox + x;
                if (tx < 0 || tx >= width) continue;
                var s = (y * lw + x) * 4;
                var alpha = premultiplied[s + 3];
                if (alpha == 0) continue;
                var t = (ty * width + tx) * 4;
                var inverse = 255 - alpha;
                target[t] = (byte)Math.Min(255, premultiplied[s] + target[t] * inverse / 255);
                target[t + 1] = (byte)Math.Min(255, premultiplied[s + 1] + target[t + 1] * inverse / 255);
                target[t + 2] = (byte)Math.Min(255, premultiplied[s + 2] + target[t + 2] * inverse / 255);
            }
        }
    }

    private void OnUpdate(LiveUpdate update)
    {
        var emit = Now;
        var seq = Interlocked.Increment(ref _seq);
        var displayed = _session is not null ? DisplayedField?.GetValue(_session) as Dictionary<string, LiveOverlayBlock> : null;
        var bounds = update.WindowBounds;
        var diag = update.Diagnostics is { } d
            ? new DiagDto(Json.R(d.CaptureToUpdateMs), d.CaptureMs, d.OcrMs, d.OcrOperationMs is { } op ? Json.R(op) : null, d.TranslateMs,
                d.OcrWidth, d.OcrHeight, d.RawLines, d.RecognizedBlocks, d.ReusedBlocks, d.RetainedBlocks, d.DisplayedBlocks,
                d.PartialOcr, d.SceneCut, d.WhiffSuspected, d.UsedScreenFallback, d.OcrSceneChecks, Json.R(d.OcrSceneCheckMs),
                d.TranslationSceneChecks, Json.R(d.TranslationSceneCheckMs), Json.R(d.GlyphCoverMs), Json.R(d.GlyphCoverWaitMs))
            : null;
        double? captureAt = update.Diagnostics is { } diagnostics ? Json.R(emit - diagnostics.CaptureToUpdateMs) : null;
        List<BlockDto>? blocks = null;
        if (update.Blocks is { } liveBlocks)
        {
            blocks = new List<BlockDto>(liveBlocks.Count);
            foreach (var block in liveBlocks)
            {
                LiveOverlayBlock? source = null;
                displayed?.TryGetValue(block.Key, out source);
                CoverDto? cover = null;
                if (block.Cover is { } c)
                {
                    var isNew = false;
                    var id = _patchIds.GetValue(c.PatchPbgra, _ =>
                    {
                        isNew = true;
                        return new StrongBox<int>(Interlocked.Increment(ref _nextPatchId));
                    }).Value;
                    if (isNew) RegisterPatch(id, c, seq, emit, captureAt);
                    cover = new CoverDto(id, isNew, Json.R(c.PatchX), Json.R(c.PatchY), Json.R(c.PatchWidth), Json.R(c.PatchHeight),
                        c.PatchPixelWidth, c.PatchPixelHeight, c.Soft, Box.From(c.Anchor).Offset(-bounds.X, -bounds.Y),
                        Json.R(c.BuildMs), Json.R3(c.MaskFraction));
                }
                blocks.Add(new BlockDto(block.Key, Box.From(block.ScreenBox).Offset(-bounds.X, -bounds.Y), block.TranslatedText,
                    block.SameAsSource, source?.SourceText, source?.Misses ?? -1, block.LineHeight, cover));
            }
        }
        var dto = new UpdateDto(seq, Json.R(emit), -1, update.StatusLine, update.ClearOverlay, update.HideOverlay, update.Stopped,
            update.ClearSubtitle, update.SubtitleText, update.Notice?.Text, Box.From(bounds), diag, blocks);
        _overlayDispatcher!.InvokeAsync(() =>
        {
            var applied = Now;
            _host?.Apply(update);
            if (_host is not null) _host.LastSeq = seq;
            _updates!.Write(dto with { AppliedMs = Json.R(applied) });
        });
    }

    private void RegisterPatch(int id, GlyphCover cover, int seq, double emit, double? captureAt)
    {
        var file = $"p{id:D5}.png";
        _patches!.Write(new PatchDto(id, cover.PatchPixelWidth, cover.PatchPixelHeight, Json.R(cover.PatchWidth), Json.R(cover.PatchHeight),
            cover.Soft, Json.R(cover.BuildMs), Json.R3(cover.MaskFraction), seq, Json.R(emit), captureAt, file));
        var bytes = cover.PatchPbgra;
        var width = cover.PatchPixelWidth;
        var height = cover.PatchPixelHeight;
        _ioPool!.Post(() => Imaging.SavePng(Imaging.FromPbgra(bytes, width, height), OutPath("patches", file)));
    }
}

internal sealed class FrameFeed(Recording recording, StaPool pool, int lookahead, int count)
{
    private readonly Task<BitmapSource>?[] _tasks = new Task<BitmapSource>?[count];
    private int _queuedUpTo = -1;

    public Task<BitmapSource> Get(int index)
    {
        EnsureQueued(Math.Min(count - 1, index + lookahead));
        return _tasks[index] ?? throw new InvalidOperationException($"Klatka {index} została już zwolniona.");
    }

    private void EnsureQueued(int upTo)
    {
        while (_queuedUpTo < upTo)
        {
            var next = ++_queuedUpTo;
            var path = recording.FramePath(next);
            _tasks[next] = pool.Run(() => Imaging.DecodeJpeg(path));
        }
    }

    public void Release(int index) => _tasks[index] = null;
}
