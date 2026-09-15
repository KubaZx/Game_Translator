using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using TextBlock = System.Windows.Controls.TextBlock;

// Separate fixture: it never changes the entire window background or invokes game input.
internal static class LocalReadingReplay
{
    private const string DoorA = "The door is locked";
    private const string DoorB = "The door is open";
    private const string LevelA = "Level 20";
    private const string LevelB = "Level 21";

    public static int Run(string output, string scenario)
    {
        using var report = new Report(output, scenario);
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-localreading-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var scripted = report.Jitter ? new AlternatingOcr(report.Whiff) : null;
        IOcrProvider sourceOcr = scripted is null ? new WindowsOcrProvider() : scripted;
        var ocr = new ObservedOcr(sourceOcr, report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromSeconds(2) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        orchestrator.Initialize();
        if (!ocr.IsLanguageAvailable(settings.SourceLanguage)) throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            window = await TestWindow.CreateAsync(report.Jitter ? LevelA : DoorA);
            await window.AfterRenderingAsync();
            var options = new LiveSessionOptions
            {
                OcrUpscale = 1, EnableDiagnostics = true,
                StaticRescanInterval = TimeSpan.FromMilliseconds(report.Whiff ? 500 : report.Jitter ? 600 : 4000),
            };
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle, options,
                report.Update, logging.CreateLogger("LocalReadingReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (scripted is not null)
            {
                // Physical text stays at Level 20; this is an explicitly synthetic OCR sequence.
                report.BeginMeasurement();
                scripted.Arm();
            }
            else
            {
                await window.ChangePanelAsync(DoorB, report);
            }
            await Task.Delay(12000);
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex is TimeoutException ? "scenario_timeout" : "runtime_error";
            report.Event(new { type = "error", errorType = ex.GetType().Name });
        }
        finally
        {
            if (session is not null)
            {
                report.EndMeasurement();
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

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in LocalReadingReplay.");
    }

    private sealed class ObservedOcr(IOcrProvider inner, Report report) : IOcrProvider
    {
        public string Name => inner.Name;
        public int MaxImageDimension => inner.MaxImageDimension;
        public IReadOnlyList<string> AvailableLanguages => inner.AvailableLanguages;
        public bool IsLanguageAvailable(string languageTag) => inner.IsLanguageAvailable(languageTag);
        public async Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            var result = await inner.RecognizeAsync(bitmap, languageTag, cancellationToken).ConfigureAwait(false);
            report.Ocr(result, bitmap.Width, bitmap.Height);
            return result;
        }
    }

    // Sequence after arming: B, A, B, A, then A forever. The displayed pixels never change.
    // This models an OCR fluctuation, not a real Level 21 screen or OCR accuracy measurement.
    private sealed class AlternatingOcr(bool whiff) : IOcrProvider
    {
        private int _armed;
        private int _readingsAfterArm;
        public void Arm() => Volatile.Write(ref _armed, 1);
        public string Name => whiff ? "Scripted numeric reading with an empty result" : "Scripted alternating numeric reading";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = Volatile.Read(ref _armed) == 0 ? 0 : Interlocked.Increment(ref _readingsAfterArm);
            var box = FindWhiteBox(bitmap, cancellationToken);
            if (whiff && ordinal == 2) return Task.FromResult(OcrResult.Empty(languageTag));
            var text = ordinal is 1 or 3 ? LevelB : LevelA;
            return Task.FromResult(new OcrResult([new OcrLine(text, box, [new OcrWord(text, box)])], languageTag));
        }

        // Only geometry comes from pixels. The returned A/B/empty reading remains
        // explicitly scripted; this helper does not recognize characters or read UI state.
        // The fixture's panel/background/reference stripe have channels below 200,
        // so only the physical white Level 20 glyphs contribute to this box.
        private static RectPx FindWhiteBox(OcrBitmap bitmap, CancellationToken cancellationToken)
        {
            var left = bitmap.Width;
            var top = bitmap.Height;
            var right = -1;
            var bottom = -1;
            for (var y = 0; y < bitmap.Height; y++)
            {
                if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = y * bitmap.Stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = row + x * 4;
                    if (bitmap.PixelsBgra32[offset] < 200 || bitmap.PixelsBgra32[offset + 1] < 200
                        || bitmap.PixelsBgra32[offset + 2] < 200) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right < left || bottom < top)
                throw new InvalidOperationException("Synthetic reading fixture could not locate its physical white glyphs.");
            // Coordinates are relative to this captured bitmap, whether full frame
            // or ROI. LiveTranslationSession already applies crop offset and scale.
            return new RectPx(left, top, right - left + 1, bottom - top + 1);
        }
    }

    private sealed class TestWindow(Window window, Border panel, TextBlock text, Thread thread)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;
        public static async Task<TestWindow> CreateAsync(string initialText)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var text = new TextBlock { Text = initialText, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                        Foreground = Brushes.White, Margin = new Thickness(10, 18, 10, 0), TextWrapping = TextWrapping.NoWrap };
                    var panel = new Border { Width = 340, Height = 70, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)), Child = text };
                    Canvas.SetLeft(panel, 80); Canvas.SetTop(panel, 150);
                    var reference = new Border { Width = 50, Height = 560, Background = Brushes.Gray };
                    Canvas.SetLeft(reference, 890);
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    canvas.Children.Add(reference); canvas.Children.Add(panel);
                    var window = new Window { Title = "GTO LocalReadingReplay - local test", Width = 940, Height = 560,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, panel, text, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public Task AfterRenderingAsync() => RenderAsync(null);
        public Task ChangePanelAsync(string newText, Report report) => RenderAsync(() =>
        {
            text.Text = newText;
            panel.Background = new SolidColorBrush(Color.FromRgb(58, 58, 58));
            report.Event(new { type = "local_panel_mutated" });
        }, report.BeginMeasurement);
        private async Task RenderAsync(Action? change, Action? renderedCallback = null)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                change?.Invoke();
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    renderedCallback?.Invoke();
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
        private double? _startedAt, _firstBReadyAt;
        private bool _ended, _screenFallback, _sessionStopped, _visibleA, _visibleB;
        private int _observedA, _observedB, _observedEmpty, _callbacksA, _callbacksB, _clears, _hides, _sceneCuts, _reused;
        public bool Jitter { get; }
        public bool Whiff { get; }
        private string A => Jitter ? LevelA : DoorA;
        private string B => Jitter ? LevelB : DoorB;
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FixtureValid
        {
            get { lock (_gate) return !_screenFallback && !_sessionStopped && _startedAt is not null && _sceneCuts == 0
                && (Jitter ? _observedA >= 2 && _observedB >= 2 && (!Whiff || _observedEmpty >= 1) : _observedB >= 3); }
        }
        public Report(string output, string scenario)
        {
            Jitter = scenario != "local-reading";
            Whiff = scenario == "reading-whiff";
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario,
                ocrMode = Jitter ? "scripted" : "windows", provider = "Mock", providerDelayMs = 2000, privateMode = true,
                cache = "fresh-memory", network = "blocked", observationMs = 12000, staticRescanIntervalMs = Whiff ? 500 : Jitter ? 600 : 4000,
                panelFraction = 340.0 * 70 / (940 * 560), entireBackgroundChanges = false, actualOverlayWindow = false,
                scriptedSequence = Whiff ? "Initial A; after arming B,empty,B,A,then A"
                    : Jitter ? "Initial A; after arming B,A,B,A,then A" : null,
                scriptedGeometry = Jitter ? "Bounding box of captured white glyph pixels; same method for full frame and ROI" : null,
                physicalTextChanges = !Jitter, qualityA = ReadingQuality.Score(A), qualityB = ReadingQuality.Score(B),
                similarity = TextSimilarity.Ratio(A, B), sourceLengthA = A.Length, sourceLengthB = B.Length,
                timingMeaning = Jitter ? "From arming synthetic OCR to session callbacks; physical text stays A"
                    : "From WPF Rendering after local panel change to session callbacks; not physical overlay presentation latency" });
        }
        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));
        public void BeginMeasurement()
        {
            lock (_gate)
            {
                _startedAt = _clock.Elapsed.TotalMilliseconds;
                Write(new { type = "measurement_started", physicalTextIsB = !Jitter });
            }
        }
        public void EndMeasurement() { lock (_gate) _ended = true; }
        private static bool IsText(string observed, string expected) =>
            TextNormalizer.Normalize(observed).Equals(expected, StringComparison.OrdinalIgnoreCase);
        public void Ocr(OcrResult result, int width, int height)
        {
            var combined = string.Join(" ", result.Lines.Select(line => line.Text));
            var isA = IsText(combined, A);
            var isB = IsText(combined, B);
            lock (_gate)
            {
                var measured = _startedAt is not null && !_ended;
                if (measured && isA) _observedA++;
                if (measured && isB) _observedB++;
                if (measured && result.Lines.Count == 0) _observedEmpty++;
                Write(new { type = "ocr_completed", observedA = isA, observedB = isB, observedEmpty = result.Lines.Count == 0, measured,
                    quality = ReadingQuality.Score(TextNormalizer.Normalize(combined)), rawLines = result.Lines.Count, width, height });
            }
        }
        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Stopped)
                {
                    _sessionStopped = true;
                    Write(new { type = "session_stopped", update.ClearOverlay, update.HideOverlay });
                    return;
                }
                if (_ended) return; // Stop or shutdown cannot count as fixing a stale display.
                var hasA = update.Blocks?.Any(block => IsText(block.TranslatedText, "[PL] " + A)) ?? false;
                var hasB = update.Blocks?.Any(block => IsText(block.TranslatedText, "[PL] " + B)) ?? false;
                if (update.ClearOverlay || update.HideOverlay) { _visibleA = false; _visibleB = false; }
                if (update.Blocks is not null) { _visibleA = hasA; _visibleB = hasB; }
                if (_startedAt is null && hasA) InitialDisplayed.TrySetResult();
                if (_startedAt is not null)
                {
                    if (hasA) _callbacksA++;
                    if (hasB) { _callbacksB++; _firstBReadyAt ??= _clock.Elapsed.TotalMilliseconds; }
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (update.Diagnostics?.SceneCut == true) _sceneCuts++;
                    _reused += update.Diagnostics?.ReusedBlocks ?? 0;
                }
                Write(new { type = "update", measured = _startedAt is not null, blocksProvided = update.Blocks is not null,
                    blockCount = update.Blocks?.Count, containsA = hasA, containsB = hasB, visibleA = _visibleA, visibleB = _visibleB,
                    update.ClearOverlay, update.HideOverlay, completedOcr = update.Diagnostics is not null,
                    sceneCut = update.Diagnostics?.SceneCut, reusedBlocks = update.Diagnostics?.ReusedBlocks,
                    retainedBlocks = update.Diagnostics?.RetainedBlocks, partialOcr = update.Diagnostics?.PartialOcr });
            }
        }
        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, screenFallback = _screenFallback,
                    observedA = _observedA, observedB = _observedB, observedEmpty = _observedEmpty, callbacksAAfterMeasurementStart = _callbacksA,
                    callbacksBAfterMeasurementStart = _callbacksB, firstBReadyMs = _firstBReadyAt - _startedAt,
                    aVisibleAtEnd = _visibleA, bVisibleAtEnd = _visibleB, sceneCutFramesAfterMeasurementStart = _sceneCuts,
                    clearCallbacksAfterMeasurementStart = _clears, hideCallbacksAfterMeasurementStart = _hides,
                    reusedBlocksAfterMeasurementStart = _reused,
                    desiredReadingPublished = Jitter ? _callbacksB == 0 && _visibleA : _firstBReadyAt is not null && _visibleB,
                    mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters,
                    usage.CacheHits, usage.GlossaryHits });
            }
            Console.WriteLine($"LocalReadingReplay: {reason}; OCR A={_observedA}, B={_observedB}; first B={_firstBReadyAt - _startedAt:F0} ms.");
            return exitCode;
        }
        public void Dispose() => _writer.Dispose();
    }
}