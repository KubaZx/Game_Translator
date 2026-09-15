using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using TextBlock = System.Windows.Controls.TextBlock;

// Real window capture and session callbacks; character recognition is explicitly scripted.
// No game input, overlay rendering, network, persisted user settings, or screenshot output.
internal static class MovingTextReplay
{
    private const string SourceText = "The library door is open";
    private const int Steps = 12;
    private const int StepPixels = 3;

    public static int Run(string output, string scenario)
    {
        if (scenario is not ("moving-text" or "position-jitter"))
            throw new ArgumentException("MovingTextReplay supports moving-text or position-jitter.", nameof(scenario));
        using var report = new Report(output, scenario == "position-jitter");
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-movingtext-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var ocr = new GlyphGeometryOcr(report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromSeconds(2) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        orchestrator.Initialize();
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            window = await TestWindow.CreateAsync();
            await window.MoveToAsync(0);
            var initial = CaptureGroundTruth(window.Handle, report);
            report.SetInitialTarget(initial);
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true, StaticRescanInterval = TimeSpan.FromMilliseconds(600) },
                report.Update, logging.CreateLogger("MovingTextReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            ocr.Arm();
            for (var step = 1; step <= Steps; step++)
            {
                if (!report.Jitter) await window.MoveToAsync(step * StepPixels);
                // Independent capture gives window-relative physical-pixel ground truth.
                // OCR itself still only reads the bitmap supplied by LiveTranslationSession.
                var target = CaptureGroundTruth(window.Handle, report);
                await report.BeginStep(step, target).WaitAsync(TimeSpan.FromSeconds(6));
            }
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
                try
                {
                    await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                    session.Dispose();
                }
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

    private static RectPx CaptureGroundTruth(IntPtr handle, Report report)
    {
        var (bitmap, fallback) = ScreenCapture.CaptureWindowEx(handle);
        using (bitmap)
        {
            if (fallback) report.ScreenFallback();
            if (bitmap is null || fallback) throw new InvalidOperationException("Own-window capture failed or required screen fallback.");
            var frame = ScreenCapture.ToOcrBitmap(bitmap);
            var box = FindWhiteBox(frame, CancellationToken.None);
            report.Event(new { type = "ground_truth_capture", sourceBoxPx = box, frame.Width, frame.Height });
            return box;
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in MovingTextReplay.");
    }

    private sealed class GlyphGeometryOcr(Report report) : IOcrProvider
    {
        private int _armed, _ordinal;
        public void Arm() => Volatile.Write(ref _armed, 1);
        public string Name => "Scripted text with captured white-glyph geometry";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            var box = FindWhiteBox(bitmap, cancellationToken);
            var ordinal = Volatile.Read(ref _armed) == 0 ? 0 : Interlocked.Increment(ref _ordinal);
            var dx = report.Jitter && ordinal > 0 ? ordinal % 2 == 1 ? 1 : -1 : 0;
            var dy = -dx;
            report.Ocr(box, dx, dy, bitmap.Width, bitmap.Height);
            var observed = box.Offset(dx, dy);
            return Task.FromResult(new OcrResult([new OcrLine(SourceText, observed, [new OcrWord(SourceText, observed)])], languageTag));
        }
    }

    // Geometry only: full captures and session ROIs use exactly the same pixel rule.
    // Every background/reference channel is <200; only white physical glyphs qualify.
    private static RectPx FindWhiteBox(OcrBitmap bitmap, CancellationToken cancellationToken)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.Width; x++)
            {
                var offset = y * bitmap.Stride + x * 4;
                if (bitmap.PixelsBgra32[offset] < 200 || bitmap.PixelsBgra32[offset + 1] < 200 || bitmap.PixelsBgra32[offset + 2] < 200) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        }
        if (right < left || bottom < top) throw new InvalidOperationException("Fixture white glyphs were not found.");
        return new RectPx(left, top, right - left + 1, bottom - top + 1);
    }

    private sealed class TestWindow(Window window, TextBlock text, Thread thread)
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
                    var text = new TextBlock { Text = SourceText, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                        Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap, SnapsToDevicePixels = true };
                    Canvas.SetLeft(text, 12); Canvas.SetTop(text, 10);
                    var panel = new Canvas { Width = 440, Height = 55, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
                    panel.Children.Add(text);
                    Canvas.SetLeft(panel, 80); Canvas.SetTop(panel, 150);
                    var reference = new Border { Width = 50, Height = 560, Background = Brushes.Gray };
                    Canvas.SetLeft(reference, 890);
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    canvas.Children.Add(reference); canvas.Children.Add(panel);
                    var window = new Window { Title = "GTO MovingTextReplay - local test", Width = 940, Height = 560,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, UseLayoutRounding = true, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, text, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public async Task MoveToAsync(int physicalOffsetX)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                var dpi = VisualTreeHelper.GetDpi(text);
                Canvas.SetLeft(text, 12 + physicalOffsetX / dpi.DpiScaleX);
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    rendered.TrySetResult();
                };
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
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private readonly HashSet<(int, int)> _sourcePositions = [];
        private RectPx _initialTarget, _target, _previousTarget, _initialCallback, _previousCallback, _lastAnyCallback;
        private TaskCompletionSource? _stepReady;
        private int _step, _callbacksThisStep, _samples, _emptySamples, _clears, _hides, _sceneCuts, _whiffs;
        private int _heldSteps, _heldRun, _longestHeldRun, _restPositionChanges, _ocrCount, _positiveNoise, _negativeNoise;
        private int _maxAxisError, _maxRestDrift;
        private double _maxPositionError, _maxCallbackJump, _maxSourceStep;
        private bool _ended, _screenFallback, _stopped, _geometryValid = true, _visible;
        public bool Jitter { get; }
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FixtureValid
        {
            get { lock (_gate) return _samples == Steps && !_screenFallback && !_stopped && _geometryValid && _sceneCuts == 0
                && (Jitter ? _sourcePositions.Count == 1 && _positiveNoise > 0 && _negativeNoise > 0 : _sourcePositions.Count == Steps + 1); }
        }
        public Report(string output, bool jitter)
        {
            Jitter = jitter;
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario = jitter ? "position-jitter" : "moving-text", ocrMode = "scripted",
                provider = "Mock", providerDelayMs = 2000, privateMode = true, cache = "fresh-memory", network = "blocked",
                steps = Steps, requestedMotionPerStepPx = jitter ? 0 : StepPixels, syntheticPositionNoisePx = jitter ? 1 : 0,
                staticRescanIntervalMs = 600, fps = 6, panelFraction = 440.0 * 55 / (940 * 560), entireBackgroundChanges = false,
                actualOverlayWindow = false, units = "physical pixels", sourceTextChanges = false,
                groundTruth = "Independent own-window capture: white-glyph bounding box in window-relative physical pixels",
                scriptedGeometry = "White-glyph bounding box from the supplied full frame or ROI; fixed text; no UI-state input",
                sampling = "Hold each step until two diagnostic callbacks; first callback discarded to exclude a frame already in flight",
                timingMeaning = "Position in session callbacks after a held step; neither continuous-motion latency nor WPF overlay rendering" });
        }
        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));
        public void ScreenFallback() { lock (_gate) _screenFallback = true; }
        public void SetInitialTarget(RectPx target)
        {
            lock (_gate) { _initialTarget = _previousTarget = _target = target; _sourcePositions.Add((target.X, target.Y)); }
        }
        public Task BeginStep(int step, RectPx target)
        {
            lock (_gate)
            {
                _step = step; _target = target; _callbacksThisStep = 0;
                _stepReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _sourcePositions.Add((target.X, target.Y));
                if (Jitter) _geometryValid &= target == _initialTarget;
                else _geometryValid &= Math.Abs(target.X - _previousTarget.X - StepPixels) <= 1 && target.Y == _previousTarget.Y;
                Write(new { type = "step_started", step, sourceBoxPx = target });
                return _stepReady.Task;
            }
        }
        public void EndMeasurement() { lock (_gate) _ended = true; }
        public void Ocr(RectPx box, int dx, int dy, int width, int height)
        {
            lock (_gate)
            {
                _ocrCount++;
                if (dx > 0) _positiveNoise++;
                if (dx < 0) _negativeNoise++;
                Write(new { type = "ocr_completed", ordinal = _ocrCount, glyphBoxInOcrBitmapPx = box, syntheticDxPx = dx, syntheticDyPx = dy, width, height });
            }
        }
        private static bool IsExpected(LiveDisplayBlock block) =>
            TextNormalizer.Normalize(block.TranslatedText).Equals("[PL] " + SourceText, StringComparison.OrdinalIgnoreCase);
        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (_ended) return;
                if (update.Stopped) { _stopped = true; return; }
                var block = update.Blocks?.FirstOrDefault(IsExpected);
                RectPx? position = block?.ScreenBox.Offset(-update.WindowBounds.X, -update.WindowBounds.Y);
                if (update.ClearOverlay || update.HideOverlay) _visible = false;
                if (update.Blocks is not null) _visible = block is not null;
                if (block is not null) _keys.Add(block.Key);
                if (_step == 0 && position is { } first)
                {
                    _initialCallback = _previousCallback = _lastAnyCallback = first;
                    InitialDisplayed.TrySetResult();
                }
                if (_step > 0)
                {
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (update.Diagnostics?.SceneCut == true) _sceneCuts++;
                    if (update.Diagnostics?.WhiffSuspected == true) _whiffs++;
                    if (Jitter && position is { } atRest)
                    {
                        if (atRest.X != _lastAnyCallback.X || atRest.Y != _lastAnyCallback.Y) _restPositionChanges++;
                        _maxRestDrift = Math.Max(_maxRestDrift, AxisDistance(atRest, _initialCallback));
                        _lastAnyCallback = atRest;
                    }
                }
                Write(new { type = "update", step = _step, callbackBoxWindowRelativePx = position,
                    blocksProvided = update.Blocks is not null, blockCount = update.Blocks?.Count, containsExpected = block is not null,
                    update.ClearOverlay, update.HideOverlay, completedOcr = update.Diagnostics is not null,
                    sceneCut = update.Diagnostics?.SceneCut, partialOcr = update.Diagnostics?.PartialOcr,
                    captureMs = update.Diagnostics?.CaptureMs, ocrMs = update.Diagnostics?.OcrMs, translateMs = update.Diagnostics?.TranslateMs });
                if (_stepReady is null || _stepReady.Task.IsCompleted || update.Diagnostics is null || ++_callbacksThisStep < 2) return;
                _samples++;
                double? error = null, jump = null;
                if (position is { } actual)
                {
                    error = Distance(actual, _target);
                    jump = Distance(actual, _previousCallback);
                    _maxPositionError = Math.Max(_maxPositionError, error.Value);
                    _maxAxisError = Math.Max(_maxAxisError, AxisDistance(actual, _target));
                    _maxCallbackJump = Math.Max(_maxCallbackJump, jump.Value);
                    var sourceMoved = _target.X != _previousTarget.X || _target.Y != _previousTarget.Y;
                    var held = sourceMoved && actual.X == _previousCallback.X && actual.Y == _previousCallback.Y;
                    if (held) { _heldSteps++; _heldRun++; _longestHeldRun = Math.Max(_longestHeldRun, _heldRun); }
                    else _heldRun = 0;
                    _previousCallback = actual;
                }
                else { _emptySamples++; _heldRun = 0; }
                _maxSourceStep = Math.Max(_maxSourceStep, Distance(_target, _previousTarget));
                Write(new { type = "position_sample", step = _step, sourceBoxPx = _target, callbackBoxPx = position,
                    positionErrorPx = error, callbackJumpPx = jump, sourceStepPx = Distance(_target, _previousTarget), visible = _visible });
                _previousTarget = _target;
                _stepReady.TrySetResult();
            }
        }
        private static int AxisDistance(RectPx a, RectPx b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        private static double Distance(RectPx a, RectPx b) => Math.Sqrt(Math.Pow((double)a.X - b.X, 2) + Math.Pow((double)a.Y - b.Y, 2));
        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var desired = FixtureValid && _emptySamples == 0 && _clears == 0 && _hides == 0 && _visible && _keys.Count == 1
                    && (Jitter ? _restPositionChanges == 0 : _maxAxisError <= 2);
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, desiredPositionBehavior = desired,
                    samples = _samples, emptySamples = _emptySamples, uniqueSourcePositions = _sourcePositions.Count, uniqueBlockKeys = _keys.Count,
                    maxPositionErrorPx = _samples > _emptySamples ? (double?)_maxPositionError : null,
                    maxAxisPositionErrorPx = _samples > _emptySamples ? (int?)_maxAxisError : null,
                    maxCallbackJumpPx = _maxCallbackJump, maxSourceStepPx = _maxSourceStep,
                    heldPositionStepsWhileSourceMoved = _heldSteps, longestHeldPositionRun = _longestHeldRun,
                    positionChangesAtRest = _restPositionChanges, maxCallbackDriftAtRestPx = _maxRestDrift,
                    clearCallbacks = _clears, hideCallbacks = _hides, sceneCutFrames = _sceneCuts, whiffFrames = _whiffs,
                    observedOcr = _ocrCount, positiveNoiseReadings = _positiveNoise, negativeNoiseReadings = _negativeNoise,
                    screenFallback = _screenFallback, visibleAtEnd = _visible,
                    mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters, usage.CacheHits, usage.GlossaryHits });
                Console.WriteLine($"MovingTextReplay: {reason}; samples={_samples}; max position error={_maxPositionError:F1} px; held={_heldSteps}; clears={_clears}; hides={_hides}.");
            }
            return exitCode;
        }
        public void Dispose() => _writer.Dispose();
    }
}