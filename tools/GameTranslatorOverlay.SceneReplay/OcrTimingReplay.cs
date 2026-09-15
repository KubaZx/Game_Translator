using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using TextBlock = System.Windows.Controls.TextBlock;

// Static own-window fixture for measuring waiting and scene-check overhead around OCR.
// Synthetic OCR delay is deliberate: this is not a Windows OCR accuracy benchmark.
internal static class OcrTimingReplay
{
    private static readonly string[] Lines =
    [
        "Copper key unlocks the hidden chest",
        "Library map reveals the secret room",
        "Garden clue opens the next door",
    ];
    private const int MeasuredSamples = 8;

    public static int Run(string output, int delayMs)
    {
        if (delayMs is < 100 or > 600)
            throw new ArgumentOutOfRangeException(nameof(delayMs), "OCR timing delay must be between 100 and 600 ms.");
        using var report = new Report(output, delayMs);
        return RunAsync(report, delayMs).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report, int delayMs)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-ocrtiming-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var ocr = new TimedOcr(delayMs, report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.Zero },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        orchestrator.Initialize();
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            window = await TestWindow.CreateAsync();
            await window.AfterRenderingAsync();
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true, StaticRescanInterval = TimeSpan.FromMilliseconds(500) },
                report.Update, logging.CreateLogger("OcrTimingReplay"));
            session.Start();
            await report.WarmedUp.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await report.SamplesReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex is TimeoutException ? "scenario_timeout" : "runtime_error";
            report.Event(new { type = "error", errorType = ex.GetType().Name });
        }
        finally
        {
            report.EndMeasurement();
            if (session is not null)
            {
                session.Stop();
                try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(10)); session.Dispose(); }
                catch (OperationCanceledException) { session.Dispose(); }
                catch (Exception) { exitCode = 4; reason = "session_shutdown_failed"; }
            }
            if (window is not null)
            {
                try { await window.CloseAsync(); }
                catch (Exception) { exitCode = 4; reason = "window_shutdown_failed"; }
            }
        }
        if (exitCode == 0 && !report.FixtureValid)
        { exitCode = 4; reason = "fixture_preconditions_failed"; }
        return report.Finish(exitCode, reason, usage);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in OcrTimingReplay.");
    }

    private sealed class TimedOcr(int delayMs, Report report) : IOcrProvider
    {
        public string Name => "Static captured-pixel fixture with a synthetic OCR delay";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public async Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            var clock = Stopwatch.StartNew();
            var boxes = LocateStaticScene(bitmap, cancellationToken);
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            var lines = boxes.Select((box, index) => new OcrLine(Lines[index], box, [new OcrWord(Lines[index], box)])).ToArray();
            report.Ocr(clock.Elapsed.TotalMilliseconds, bitmap.Width, bitmap.Height, lines.Length);
            return new OcrResult(lines, languageTag);
        }

        // Static-scene recognition is explicitly scripted from captured background and
        // white glyph bands, never a window property. It rejects missing/clipped bands.
        private static IReadOnlyList<RectPx> LocateStaticScene(OcrBitmap bitmap, CancellationToken ct)
        {
            if (bitmap.Width < 100 || bitmap.Height < 100 || bitmap.PixelsBgra32.Length < bitmap.Stride * bitmap.Height)
                throw new InvalidOperationException("Invalid fixture bitmap.");
            for (var channel = 0; channel < 3; channel++)
                if (bitmap.PixelsBgra32[channel] is < 15 or > 25)
                    throw new InvalidOperationException("Static fixture background is missing.");
            var boxes = new List<RectPx>();
            var current = default(RectPx);
            var lastWhiteRow = -1;
            for (var y = 0; y < bitmap.Height; y++)
            {
                if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
                var left = bitmap.Width;
                var right = -1;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var p = y * bitmap.Stride + x * 4;
                    if (bitmap.PixelsBgra32[p] < 200 || bitmap.PixelsBgra32[p + 1] < 200 || bitmap.PixelsBgra32[p + 2] < 200) continue;
                    left = Math.Min(left, x);
                    right = x;
                }
                if (right < left) continue;
                if (lastWhiteRow >= 0 && y - lastWhiteRow > 12)
                { boxes.Add(current); current = default; }
                current = current.Union(new RectPx(left, y, right - left + 1, 1));
                lastWhiteRow = y;
            }
            if (!current.IsEmpty) boxes.Add(current);
            if (boxes.Count != Lines.Length || boxes.Any(box => box.Width < 100 || box.Height < 10))
                throw new InvalidOperationException("Static fixture must contain three complete white text bands.");
            return boxes;
        }
    }

    private sealed class TestWindow(Window window, Thread thread)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;
        public static async Task<TestWindow> CreateAsync()
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var canvas = new Canvas { Width = 940, Height = 560, Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    var reference = new Border { Width = 50, Height = 560, Background = Brushes.Gray };
                    Canvas.SetLeft(reference, 890);
                    canvas.Children.Add(reference); // Fixed gray stripe prevents the blank-capture fallback heuristic.
                    for (var i = 0; i < Lines.Length; i++)
                    {
                        var text = new TextBlock { Text = Lines[i], FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                            Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap };
                        Canvas.SetLeft(text, 80); Canvas.SetTop(text, 120 + i * 120);
                        canvas.Children.Add(text);
                    }
                    var window = new Window { Title = "GTO OCR timing - local test", Width = 940, Height = 560,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = new Viewbox { Stretch = Stretch.Fill, Child = canvas } };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    window.Width = 1920 / dpi.DpiScaleX;
                    window.Height = 1080 / dpi.DpiScaleY;
                    ready.TrySetResult(new TestWindow(window, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public async Task AfterRenderingAsync()
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                EventHandler? handler = null;
                handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(); };
                CompositionTarget.Rendering += handler;
            });
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async Task CloseAsync()
        {
            await window.Dispatcher.InvokeAsync(window.Close).Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!thread.Join(TimeSpan.FromSeconds(3))) throw new TimeoutException();
        }
    }

    private sealed class Report : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<LiveFrameDiagnostics> _samples = [];
        private int _completedOcr, _diagnostics, _clears, _hides, _cuts, _whiffs, _partial;
        private bool _ended, _fallback, _unexpectedStop, _invalidBlocks;
        public TaskCompletionSource WarmedUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SamplesReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FixtureValid
        {
            get { lock (_gate) return _samples.Count == MeasuredSamples && _completedOcr >= MeasuredSamples + 1
                && !_fallback && !_unexpectedStop && !_invalidBlocks && _clears == 0 && _hides == 0 && _cuts == 0 && _whiffs == 0 && _partial == 0
                && _samples.All(sample => sample.OcrOperationMs is > 0 && sample.RawLines == Lines.Length && sample.DisplayedBlocks == Lines.Length); }
        }
        public Report(string output, int delayMs)
        {
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario = "ocr-timing", ocrMode = "scripted-captured-pixels", ocrDelayMs = delayMs,
                provider = "Mock", providerDelayMs = 0, privateMode = true, cache = "fresh-memory", network = "blocked",
                warmupSamples = 1, measuredSamples = MeasuredSamples, staticRescanIntervalMs = 500, physicalTextChanges = false,
                actualOverlayWindow = false, requestedWindowPixelWidth = 1920, requestedWindowPixelHeight = 1080, timingMeaning = "Per-frame OCR operation, wrapped OCR wait and capture-to-callback; not physical overlay presentation latency" });
        }
        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));
        public void EndMeasurement() { lock (_gate) _ended = true; }
        public void Ocr(double operationMs, int width, int height, int lineCount)
        {
            lock (_gate)
            {
                _completedOcr++;
                Write(new { type = "ocr_completed", ordinal = _completedOcr, syntheticOperationMs = operationMs, width, height, lineCount });
            }
        }
        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                if (_ended) return;
                _fallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                _unexpectedStop |= update.Stopped;
                var measured = WarmedUp.Task.IsCompleted;
                if (measured)
                {
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (update.Blocks is not null && update.Blocks.Count != Lines.Length) _invalidBlocks = true;
                }
                if (update.Diagnostics is not { } d)
                {
                    Write(new { type = "update", measured, hasDiagnostics = false, update.ClearOverlay, update.HideOverlay, update.Stopped });
                    return;
                }
                _diagnostics++;
                if (measured)
                {
                    _samples.Add(d);
                    if (d.SceneCut) _cuts++;
                    if (d.WhiffSuspected) _whiffs++;
                    if (d.PartialOcr) _partial++;
                }
                Write(new { type = "sample", ordinal = _diagnostics, measured, d.CaptureToUpdateMs, d.CaptureMs, d.OcrMs,
                    d.OcrOperationMs, d.OcrSceneChecks, d.OcrSceneCheckMs, d.TranslationSceneChecks, d.TranslationSceneCheckMs,
                    d.TranslateMs, d.OcrWidth, d.OcrHeight, d.RawLines, d.RecognizedBlocks, d.ReusedBlocks, d.RetainedBlocks, d.DisplayedBlocks,
                    d.PartialOcr, d.SceneCut, d.WhiffSuspected, d.UsedScreenFallback, update.ClearOverlay, update.HideOverlay, update.Stopped });
                if (!measured) WarmedUp.TrySetResult();
                if (_samples.Count == MeasuredSamples) { _ended = true; SamplesReady.TrySetResult(); }
            }
        }
        private static double? Median(IEnumerable<double> source)
        {
            var values = source.Order().ToArray();
            return values.Length == 0 ? null : values.Length % 2 == 0
                ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
        }
        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, measuredSamples = _samples.Count,
                    completedOcr = _completedOcr, completedDiagnostics = _diagnostics, screenFallback = _fallback, unexpectedStop = _unexpectedStop,
                    clearCallbacks = _clears, hideCallbacks = _hides, sceneCutFrames = _cuts, whiffFrames = _whiffs, partialFrames = _partial,
                    captureToUpdateMedianMs = Median(_samples.Select(d => d.CaptureToUpdateMs)),
                    captureMedianMs = Median(_samples.Select(d => (double)d.CaptureMs)),
                    ocrWrappedMedianMs = Median(_samples.Select(d => (double)d.OcrMs)),
                    ocrOperationMedianMs = Median(_samples.Where(d => d.OcrOperationMs.HasValue).Select(d => d.OcrOperationMs!.Value)),
                    ocrOverheadMedianMs = Median(_samples.Where(d => d.OcrOperationMs.HasValue).Select(d => d.OcrMs - d.OcrOperationMs!.Value)),
                    ocrSceneChecksMedian = Median(_samples.Select(d => (double)d.OcrSceneChecks)),
                    ocrSceneCheckMedianMs = Median(_samples.Select(d => d.OcrSceneCheckMs)),
                    translationSceneChecksMedian = Median(_samples.Select(d => (double)d.TranslationSceneChecks)),
                    translationSceneCheckMedianMs = Median(_samples.Select(d => d.TranslationSceneCheckMs)),
                    mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters, usage.CacheHits, usage.GlossaryHits });
            }
            Console.WriteLine($"OcrTimingReplay: {reason}; measured samples={_samples.Count}; median capture-to-update={Median(_samples.Select(d => d.CaptureToUpdateMs)):F1} ms.");
            return exitCode;
        }
        public void Dispose() => _writer.Dispose();
    }
}