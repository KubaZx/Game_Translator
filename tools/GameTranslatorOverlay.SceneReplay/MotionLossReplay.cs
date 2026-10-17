using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBlock = System.Windows.Controls.TextBlock;

internal static class MotionLossReplay
{
    public static readonly IReadOnlySet<string> Scenarios = new HashSet<string>(StringComparer.Ordinal) { "motion-occlusion", "motion-blur" };

    private const string SourceText = "The door is locked";
    private const int StepPx = 12;
    private const int StepMs = 50;
    private const int PanSteps = 64;
    private const int GlitchMs = 150;
    private static readonly int[] GlitchAtSteps = [18, 34, 50];
    private const int SettleMs = 1500;
    private const int ProviderDelayMs = 200;
    private const int CheckAfterGlitchMs = 250;
    private const int PanTolerancePx = 2 * StepPx;

    public static int Run(string output, string scenario, bool readLost)
    {
        using var report = new Report(output, scenario, readLost);
        return RunAsync(report, scenario, readLost).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report, string scenario, bool readLost)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-motionloss-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var ocr = new GlyphGeometryOcr(report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromMilliseconds(ProviderDelayMs) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        orchestrator.Initialize();
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            window = await TestWindow.CreateAsync(scenario == "motion-blur");
            await window.PanToAsync(0, glitch: false);
            report.SetTarget(CaptureGroundTruth(window.Handle, report));
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true, ReadLostBlocks = readLost },
                report.Update, logging.CreateLogger("MotionLossReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(SettleMs);
            report.BeginPan();
            var clock = Stopwatch.StartNew();
            var checks = new List<(int Glitch, long DueMs)>();
            for (var step = 1; step <= PanSteps; step++)
            {
                var glitch = Array.IndexOf(GlitchAtSteps, step);
                var glitching = GlitchAtSteps.Any(g => step >= g && step < g + GlitchMs / StepMs);
                await window.PanToAsync(step * StepPx, glitching);
                if (glitch >= 0) report.BeginGlitch(glitch + 1);
                if (GlitchAtSteps.Any(g => step == g + GlitchMs / StepMs))
                {
                    report.EndGlitch();
                    checks.Add((report.Glitches, clock.ElapsedMilliseconds + CheckAfterGlitchMs));
                }
                foreach (var check in checks.Where(c => c.DueMs <= clock.ElapsedMilliseconds).ToList())
                {
                    checks.Remove(check);
                    report.CheckAfterGlitch(check.Glitch, CaptureGroundTruth(window.Handle, report));
                }
                var wait = step * StepMs - clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait);
            }
            report.EndPan();
            await Task.Delay(SettleMs);
            report.SetTarget(CaptureGroundTruth(window.Handle, report));
            report.CheckFinal();
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
            var box = TryFindWhiteBox(frame, CancellationToken.None) ?? throw new InvalidOperationException("Fixture white glyphs were not found.");
            report.Event(new { type = "ground_truth_capture", sourceBoxPx = box, frame.Width, frame.Height });
            return box;
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in MotionLossReplay.");
    }

    private sealed class GlyphGeometryOcr(Report report) : IOcrProvider
    {
        public string Name => "Scripted text with captured white-glyph geometry";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            var box = TryFindWhiteBox(bitmap, cancellationToken);
            report.Ocr(box, bitmap.Width, bitmap.Height);
            IReadOnlyList<OcrLine> lines = box is { } found ? [new OcrLine(SourceText, found, [new OcrWord(SourceText, found)])] : [];
            return Task.FromResult(new OcrResult(lines, languageTag));
        }
    }

    private static RectPx? TryFindWhiteBox(OcrBitmap bitmap, CancellationToken cancellationToken)
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
        return right < left || bottom < top ? null : new RectPx(left, top, right - left + 1, bottom - top + 1);
    }

    private sealed class TestWindow(Window window, Canvas world, UIElement occluder, TextBlock text, bool blur, Thread thread)
    {
        private const double WorldLeft = -1600;

        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;

        public static async Task<TestWindow> CreateAsync(bool blur)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var texture = new Image { Source = Texture(3600, 760, 24), Width = 3600, Height = 760, Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(texture, BitmapScalingMode.NearestNeighbor);
                    var text = new TextBlock { Text = SourceText, FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 40,
                        Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap, SnapsToDevicePixels = true };
                    var sign = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 30, 38)), Padding = new Thickness(22, 12, 22, 14), Child = text };
                    var occluder = new Rectangle { Width = 100, Height = 110, Fill = new SolidColorBrush(Color.FromRgb(70, 52, 40)), Visibility = Visibility.Hidden };
                    var world = new Canvas { Width = 3600, Height = 760 };
                    world.Children.Add(texture);
                    Canvas.SetLeft(sign, 1640); Canvas.SetTop(sign, 300);
                    world.Children.Add(sign);
                    Canvas.SetLeft(occluder, 1772); Canvas.SetTop(occluder, 280);
                    world.Children.Add(occluder);
                    Canvas.SetLeft(world, WorldLeft);
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)), ClipToBounds = true };
                    canvas.Children.Add(world);
                    var window = new Window { Title = "GTO MotionLossReplay - local test", Width = 1200, Height = 700,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, UseLayoutRounding = true, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, world, occluder, text, blur, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static BitmapSource Texture(int width, int height, int cell)
        {
            var random = new Random(20261016);
            var columns = (width + cell - 1) / cell;
            var rows = (height + cell - 1) / cell;
            var levels = new byte[columns * rows];
            for (var i = 0; i < levels.Length; i++) levels[i] = (byte)random.Next(24, 176);
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var level = levels[y / cell * columns + x / cell];
                    var o = (y * width + x) * 4;
                    pixels[o] = (byte)(level * 0.9);
                    pixels[o + 1] = level;
                    pixels[o + 2] = (byte)(level * 0.95);
                    pixels[o + 3] = 255;
                }
            }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            source.Freeze();
            return source;
        }

        public async Task PanToAsync(int physicalOffsetX, bool glitch)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                var dpi = VisualTreeHelper.GetDpi(world);
                Canvas.SetLeft(world, WorldLeft + physicalOffsetX / dpi.DpiScaleX);
                if (blur) text.Effect = glitch ? new BlurEffect { Radius = 4, KernelType = KernelType.Gaussian } : null;
                else occluder.Visibility = glitch ? Visibility.Visible : Visibility.Hidden;
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
        private readonly List<double> _gaps = [];
        private readonly List<double> _errorsAfterGlitch = [];
        private RectPx _target;
        private RectPx? _lastPosition;
        private int _glitch, _glitchesEnded, _removals, _removalUpdates, _clears, _hides, _ocrCount, _ocrEmpty, _ocrDuringGlitch, _visibleAfterGlitch;
        private int _relocationsDuringGlitch;
        private bool _ended, _screenFallback, _stopped, _visible, _panning, _inGlitch;
        private double? _firstRemovalMs, _removedAtMs, _finalError;
        private double _panStartMs;
        private LiveFrameDiagnostics? _lastDiagnostics;
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Glitches { get { lock (_gate) return _glitch; } }

        public bool FixtureValid
        {
            get { lock (_gate) return _glitchesEnded == GlitchAtSteps.Length && !_screenFallback && !_stopped && _ocrCount > 0; }
        }

        public Report(string output, string scenario, bool readLost)
        {
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario, readLostBlocks = readLost, ocrMode = "scripted", provider = "Mock", providerDelayMs = ProviderDelayMs,
                privateMode = true, cache = "fresh-memory", network = "blocked", panTolerancePx = PanTolerancePx, stepPx = StepPx, stepMs = StepMs, panSteps = PanSteps,
                glitchAtSteps = GlitchAtSteps, glitchMs = GlitchMs, glitch = scenario == "motion-blur" ? "BlurEffect radius 4 on the text" : "opaque bar 100 DIP over the middle of the text",
                units = "physical pixels", actualOverlayWindow = false,
                groundTruth = "Independent own-window capture: white-glyph bounding box in window-relative physical pixels",
                scriptedGeometry = "White-glyph bounding box (all channels >= 200) from the supplied full frame or ROI; fixed text; no UI-state input",
                purpose = "Text travels with a continuous pan and is briefly occluded or blurred, so frame-to-frame tracking loses it for 1-2 captures" });
        }

        public void Event(object data) { lock (_gate) Write(data); }

        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        public void ScreenFallback() { lock (_gate) _screenFallback = true; }

        public void SetTarget(RectPx target) { lock (_gate) _target = target; }

        public void BeginPan()
        {
            lock (_gate)
            {
                _panning = true;
                _panStartMs = _clock.Elapsed.TotalMilliseconds;
                Write(new { type = "pan_started" });
            }
        }

        public void EndPan() { lock (_gate) Write(new { type = "pan_ended" }); }

        public void BeginGlitch(int glitch)
        {
            lock (_gate)
            {
                _glitch = glitch;
                _inGlitch = true;
                Write(new { type = "glitch_started", glitch });
            }
        }

        public void EndGlitch()
        {
            lock (_gate)
            {
                _inGlitch = false;
                _glitchesEnded++;
                Write(new { type = "glitch_ended", glitch = _glitch, visible = _visible });
            }
        }

        public void CheckAfterGlitch(int glitch, RectPx target)
        {
            lock (_gate)
            {
                double? error = null;
                if (_visible && _lastPosition is { } position)
                {
                    error = Math.Sqrt(Math.Pow((double)position.X - target.X, 2) + Math.Pow((double)position.Y - target.Y, 2));
                    _errorsAfterGlitch.Add(error.Value);
                    _visibleAfterGlitch++;
                }
                Write(new { type = "after_glitch", glitch, afterMs = CheckAfterGlitchMs, visible = _visible, sourceBoxPx = target,
                    callbackBoxPx = _visible ? _lastPosition : null, positionErrorPx = error });
            }
        }

        public void CheckFinal()
        {
            lock (_gate)
            {
                if (_visible && _lastPosition is { } position)
                    _finalError = Math.Sqrt(Math.Pow((double)position.X - _target.X, 2) + Math.Pow((double)position.Y - _target.Y, 2));
                Write(new { type = "final", visible = _visible, sourceBoxPx = _target, callbackBoxPx = _lastPosition, positionErrorPx = _finalError });
            }
        }

        public void EndMeasurement() { lock (_gate) _ended = true; }

        public void Ocr(RectPx? box, int width, int height)
        {
            lock (_gate)
            {
                _ocrCount++;
                if (box is null) _ocrEmpty++;
                if (_inGlitch) _ocrDuringGlitch++;
                Write(new { type = "ocr_completed", ordinal = _ocrCount, glitch = _inGlitch ? _glitch : 0, glyphBoxInOcrBitmapPx = box, width, height });
            }
        }

        private static bool IsExpected(LiveDisplayBlock block) =>
            TextNormalizer.Normalize(block.TranslatedText).Equals("[PL] " + SourceText, StringComparison.OrdinalIgnoreCase);

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Diagnostics is { } diagnostics) _lastDiagnostics = diagnostics;
                if (_ended) return;
                if (update.Stopped) { _stopped = true; return; }
                var block = update.Blocks?.FirstOrDefault(IsExpected);
                var wasVisible = _visible;
                var before = _lastPosition;
                if (update.ClearOverlay || update.HideOverlay) _visible = false;
                if (update.Blocks is not null) _visible = block is not null;
                if (block is not null)
                {
                    _keys.Add(block.Key);
                    _lastPosition = block.ScreenBox.Offset(-update.WindowBounds.X, -update.WindowBounds.Y);
                    InitialDisplayed.TrySetResult();
                }
                var now = _clock.Elapsed.TotalMilliseconds;
                if (_panning)
                {
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (wasVisible && !_visible)
                    {
                        _removals++;
                        _firstRemovalMs ??= now - _panStartMs;
                        _removedAtMs = now;
                    }
                    if (!wasVisible && _visible && _removedAtMs is { } removed)
                    {
                        _gaps.Add(now - removed);
                        _removedAtMs = null;
                    }
                    if (_inGlitch && _visible && before is { } b && _lastPosition is { } p && b != p) _relocationsDuringGlitch++;
                    if (update.StatusLine.Contains("odjecha", StringComparison.Ordinal)) _removalUpdates++;
                }
                Write(new { type = "update", glitch = _inGlitch ? _glitch : 0, status = update.StatusLine.Length > 60 ? update.StatusLine[..60] : update.StatusLine,
                    callbackBoxWindowRelativePx = block is null ? (RectPx?)null : _lastPosition, containsExpected = block is not null,
                    blocksProvided = update.Blocks is not null, update.ClearOverlay, update.HideOverlay, completedOcr = update.Diagnostics is not null,
                    sceneCut = update.Diagnostics?.SceneCut, partialOcr = update.Diagnostics?.PartialOcr });
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                if (_removedAtMs is { } open) _gaps.Add(_clock.Elapsed.TotalMilliseconds - open);
                var maxError = _errorsAfterGlitch.Count > 0 ? _errorsAfterGlitch.Max() : (double?)null;
                var desired = FixtureValid && _removals == 0 && _clears == 0 && _hides == 0 && _visible
                    && _visibleAfterGlitch == GlitchAtSteps.Length && maxError <= PanTolerancePx && _finalError <= 3;
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, desiredTrackingBehavior = desired,
                    glitches = _glitchesEnded, removalTransitionsDuringPan = _removals, firstRemovalAfterPanStartMs = _firstRemovalMs,
                    trackingLossUpdates = _removalUpdates, missingMsTotal = Math.Round(_gaps.Sum(), 1), longestGapMs = _gaps.Count > 0 ? Math.Round(_gaps.Max(), 1) : 0,
                    visibleAfterGlitch = _visibleAfterGlitch, maxPositionErrorAfterGlitchPx = maxError, finalPositionErrorPx = _finalError,
                    relocationsDuringGlitch = _relocationsDuringGlitch, clearCallbacks = _clears, hideCallbacks = _hides,
                    uniqueBlockKeys = _keys.Count, visibleAtEnd = _visible, observedOcr = _ocrCount, emptyOcr = _ocrEmpty,
                    ocrDuringGlitch = _ocrDuringGlitch, screenFallback = _screenFallback,
                    lostReads = _lastDiagnostics?.LostReads, lostRecovered = _lastDiagnostics?.LostRecovered,
                    lostReadMs = _lastDiagnostics is { } d ? Math.Round(d.LostReadMs, 2) : (double?)null,
                    lostReadTotalMs = _lastDiagnostics is { } t ? Math.Round(t.LostReadTotalMs, 2) : (double?)null,
                    mockProviderRequests = usage.ApiRequests, usage.CacheHits });
                Console.WriteLine($"MotionLossReplay: {reason}; removals={_removals}; missing={_gaps.Sum():F0} ms; visible after glitch {_visibleAfterGlitch}/{GlitchAtSteps.Length}; max error={maxError:F1} px.");
            }
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
