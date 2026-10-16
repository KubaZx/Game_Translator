using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
using TextBlock = System.Windows.Controls.TextBlock;

internal static class PanCutReplay
{
    private const string SourceText = "The door is locked";
    private static readonly int[] Jumps = [40, 110, 110, 110, 110, 110, 110];
    private const int DwellMs = 450;
    private const int SettleMs = 1500;
    private const int ProviderDelayMs = 200;

    public static int Run(string output)
    {
        using var report = new Report(output);
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-pancut-" + Guid.NewGuid().ToString("N")));
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
            window = await TestWindow.CreateAsync();
            await window.PanToAsync(0);
            report.SetInitialTarget(CaptureGroundTruth(window.Handle, report));
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true },
                report.Update, logging.CreateLogger("PanCutReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(SettleMs);
            var offset = 0;
            for (var step = 0; step < Jumps.Length; step++)
            {
                offset += Jumps[step];
                await window.PanToAsync(offset);
                report.BeginJump(step + 1, Jumps[step], CaptureGroundTruth(window.Handle, report));
                await Task.Delay(DwellMs);
                report.EndJump();
            }
            await Task.Delay(SettleMs);
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
            throw new InvalidOperationException("HTTP disabled in PanCutReplay.");
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

    private static RectPx FindWhiteBox(OcrBitmap bitmap, CancellationToken cancellationToken) =>
        TryFindWhiteBox(bitmap, cancellationToken) ?? throw new InvalidOperationException("Fixture white glyphs were not found.");

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

    private sealed class TestWindow(Window window, Canvas world, Thread thread)
    {
        private const double WorldLeft = -1200;

        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;

        public static async Task<TestWindow> CreateAsync()
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
                    var world = new Canvas { Width = 3600, Height = 760 };
                    world.Children.Add(texture);
                    Canvas.SetLeft(sign, 1300); Canvas.SetTop(sign, 300);
                    world.Children.Add(sign);
                    Canvas.SetLeft(world, WorldLeft);
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)), ClipToBounds = true };
                    canvas.Children.Add(world);
                    var window = new Window { Title = "GTO PanCutReplay - local test", Width = 1200, Height = 700,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, UseLayoutRounding = true, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, world, thread!));
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

        public async Task PanToAsync(int physicalOffsetX)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                var dpi = VisualTreeHelper.GetDpi(world);
                Canvas.SetLeft(world, WorldLeft + physicalOffsetX / dpi.DpiScaleX);
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    rendered.TrySetResult();
                };
                CompositionTarget.Rendering += handler;
            });
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
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
        private RectPx _initialTarget, _target, _previousTarget;
        private RectPx? _lastPosition;
        private int _jump, _jumpsDisplayed, _jumpsMissing, _removals, _removalUpdates, _clears, _hides, _ocrCount, _ocrEmpty, _geometryErrors;
        private double _maxError;
        private bool _ended, _screenFallback, _stopped, _visible, _panning;
        private double? _firstRemovalMs;
        private double _panStartMs;
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FixtureValid
        {
            get { lock (_gate) return _jump == Jumps.Length && !_screenFallback && !_stopped && _geometryErrors == 0; }
        }

        public Report(string output)
        {
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario = "pan-cut", ocrMode = "scripted", provider = "Mock", providerDelayMs = ProviderDelayMs,
                privateMode = true, cache = "fresh-memory", network = "blocked", jumpsPx = Jumps, dwellMs = DwellMs,
                textureCellDip = 24, textureRange = "24..175", units = "physical pixels", actualOverlayWindow = false,
                groundTruth = "Independent own-window capture: white-glyph bounding box in window-relative physical pixels",
                scriptedGeometry = "White-glyph bounding box from the supplied full frame or ROI; fixed text; no UI-state input",
                purpose = "Whole textured view jumps sideways with rests between jumps, so each jump starts as a global scene cut while the text moves with it" });
        }

        public void Event(object data) { lock (_gate) Write(data); }

        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        public void ScreenFallback() { lock (_gate) _screenFallback = true; }

        public void SetInitialTarget(RectPx target)
        {
            lock (_gate) _initialTarget = _previousTarget = _target = target;
        }

        public void BeginJump(int jump, int pixels, RectPx target)
        {
            lock (_gate)
            {
                if (!_panning) _panStartMs = _clock.Elapsed.TotalMilliseconds;
                _panning = true;
                _jump = jump;
                _target = target;
                if (Math.Abs(target.X - _previousTarget.X - pixels) > 1 || target.Y != _previousTarget.Y) _geometryErrors++;
                _previousTarget = target;
                Write(new { type = "jump", jump, requestedPx = pixels, sourceBoxPx = target });
            }
        }

        public void EndJump()
        {
            lock (_gate)
            {
                double? error = null;
                if (_visible && _lastPosition is { } position)
                {
                    error = Math.Sqrt(Math.Pow((double)position.X - _target.X, 2) + Math.Pow((double)position.Y - _target.Y, 2));
                    _maxError = Math.Max(_maxError, error.Value);
                    _jumpsDisplayed++;
                }
                else _jumpsMissing++;
                Write(new { type = "jump_end", jump = _jump, visible = _visible, callbackBoxPx = _visible ? _lastPosition : null, positionErrorPx = error });
            }
        }

        public void EndMeasurement() { lock (_gate) _ended = true; }

        public void Ocr(RectPx? box, int width, int height)
        {
            lock (_gate)
            {
                _ocrCount++;
                if (box is null) _ocrEmpty++;
                Write(new { type = "ocr_completed", ordinal = _ocrCount, jump = _jump, glyphBoxInOcrBitmapPx = box, width, height });
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
                var wasVisible = _visible;
                if (update.ClearOverlay || update.HideOverlay) _visible = false;
                if (update.Blocks is not null) _visible = block is not null;
                if (block is not null)
                {
                    _keys.Add(block.Key);
                    _lastPosition = block.ScreenBox.Offset(-update.WindowBounds.X, -update.WindowBounds.Y);
                    InitialDisplayed.TrySetResult();
                }
                if (_panning)
                {
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (wasVisible && !_visible)
                    {
                        _removals++;
                        _firstRemovalMs ??= _clock.Elapsed.TotalMilliseconds - _panStartMs;
                    }
                    if (update.StatusLine.Contains("odjecha", StringComparison.Ordinal)) _removalUpdates++;
                }
                Write(new { type = "update", jump = _jump, status = update.StatusLine.Length > 60 ? update.StatusLine[..60] : update.StatusLine,
                    callbackBoxWindowRelativePx = block is null ? (RectPx?)null : _lastPosition, containsExpected = block is not null,
                    blocksProvided = update.Blocks is not null, update.ClearOverlay, update.HideOverlay, completedOcr = update.Diagnostics is not null,
                    sceneCut = update.Diagnostics?.SceneCut, partialOcr = update.Diagnostics?.PartialOcr });
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var desired = FixtureValid && _removals == 0 && _jumpsMissing == 0 && _clears == 0 && _hides == 0 && _visible && _maxError <= 3;
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, desiredTrackingBehavior = desired,
                    jumps = _jump, jumpsDisplayedAtEnd = _jumpsDisplayed, jumpsMissingAtEnd = _jumpsMissing,
                    removalTransitionsDuringPan = _removals, firstRemovalAfterPanStartMs = _firstRemovalMs,
                    trackingLossUpdates = _removalUpdates, maxPositionErrorAtJumpEndPx = _jumpsDisplayed > 0 ? (double?)_maxError : null,
                    clearCallbacks = _clears, hideCallbacks = _hides, uniqueBlockKeys = _keys.Count, visibleAtEnd = _visible,
                    observedOcr = _ocrCount, emptyOcr = _ocrEmpty, geometryErrors = _geometryErrors, screenFallback = _screenFallback,
                    initialSourceBoxPx = _initialTarget, finalSourceBoxPx = _target,
                    mockProviderRequests = usage.ApiRequests, usage.CacheHits });
                Console.WriteLine($"PanCutReplay: {reason}; displayed {_jumpsDisplayed}/{_jump}; removals={_removals}; max error={_maxError:F1} px.");
            }
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
