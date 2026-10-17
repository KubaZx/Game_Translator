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

internal static class ReactionReplay
{
    public static readonly string[] Scenarios = ["settings-change", "manual-correction"];

    private const string LabelA = "Unlock level 20 door";
    private const string LabelB = "Garden clue opens the next door";
    private const string Flagged = "Otwórz drzwi poziomu";
    private const string CorrectedText = "Otwórz drzwi poziomu 20 (poprawka)";
    private static readonly Color ColorA = Color.FromRgb(255, 255, 255);
    private static readonly Color ColorB = Color.FromRgb(255, 255, 150);
    private static readonly TimeSpan MockDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ChangeAfterOcr = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BurstStep = TimeSpan.FromMilliseconds(40);
    private const int BurstChanges = 8;
    private static readonly TimeSpan CorrectionAfterDisplay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CorrectionObservation = TimeSpan.FromMilliseconds(4500);

    public static int Run(string output, string scenario)
    {
        using var report = new Report(output, scenario);
        return RunAsync(report, scenario).GetAwaiter().GetResult();
    }

    private static int ClassifyText(string text) =>
        text.Contains("Garden", StringComparison.OrdinalIgnoreCase) ? 2
        : text.Contains("Unlock", StringComparison.OrdinalIgnoreCase) || text.Contains("drzwi", StringComparison.OrdinalIgnoreCase) ? 1
        : 0;

    private static async Task<int> RunAsync(Report report, string scenario)
    {
        var correction = scenario == "manual-correction";
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-reaction-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings
        {
            Provider = correction ? ScriptedProvider.ProviderName : MockTranslationProvider.ProviderName,
            PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1,
        };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetwork());
        var ocr = new ScriptedOcr(report);
        var usage = new UsageTracker();
        var provider = new ScriptedProvider(Flagged);
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = MockDelay }, new DeepLTranslationProvider(http, static () => null),
            ocr, usage, logging, [provider]);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            orchestrator.Initialize();
            window = await TestWindow.CreateAsync(LabelA, ColorA);
            await window.AfterRenderingAsync();
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true },
                report.Update, logging.CreateLogger("ReactionReplay"));
            session.Start();
            if (correction) await RunCorrectionAsync(report, orchestrator, usage, provider);
            else await RunSettingsAsync(report, orchestrator, window, usage);
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex is TimeoutException ? "scenario_timeout" : "runtime_error";
            report.Event(new { type = "error", errorType = ex.GetType().Name });
        }
        finally
        {
            report.EndObservation();
            if (session is not null)
            {
                session.Stop();
                try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
                catch (Exception) { exitCode = 4; reason = "session_shutdown_failed"; }
                session.Dispose();
            }
            if (window is not null)
            {
                try { await window.CloseAsync(); }
                catch (Exception) { exitCode = 4; reason = "window_shutdown_failed"; }
            }
        }
        if (exitCode == 0 && !report.FixtureValid) { exitCode = 4; reason = "fixture_preconditions_failed"; }
        return report.Finish(exitCode, reason, usage, provider.Requests);
    }

    private static async Task WaitForPendingRequestAsync(UsageTracker usage)
    {
        var waited = Stopwatch.StartNew();
        while (usage.ReservedApiCharacters == 0)
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Brak oczekującego zapytania do Mock.");
            await Task.Delay(10);
        }
    }

    private static async Task RunSettingsAsync(Report report, TranslationOrchestrator orchestrator, TestWindow window, UsageTracker usage)
    {
        await report.FirstOcr(1).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForPendingRequestAsync(usage);
        await Task.Delay(ChangeAfterOcr);
        report.SettingsChange("single", usage);
        orchestrator.RebuildPipeline();
        await report.Displayed(1).WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(300);
        await report.ArmNextFullOcr().WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(100);
        await window.ChangeAsync(LabelB, ColorB, report);
        await report.FirstOcr(2).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForPendingRequestAsync(usage);
        await Task.Delay(ChangeAfterOcr);
        for (var i = 0; i < BurstChanges; i++)
        {
            report.SettingsChange(i == 0 ? "burst-first" : i == BurstChanges - 1 ? "burst-last" : "burst", usage);
            orchestrator.RebuildPipeline();
            if (i < BurstChanges - 1) await Task.Delay(BurstStep);
        }
        await report.Displayed(2).WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(1500);
    }

    private static async Task RunCorrectionAsync(Report report, TranslationOrchestrator orchestrator, UsageTracker usage, ScriptedProvider provider)
    {
        await report.Displayed(1).WaitAsync(TimeSpan.FromSeconds(20));
        var waited = Stopwatch.StartNew();
        while (orchestrator.QualityWarnings.Recent.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        var entry = orchestrator.QualityWarnings.Recent.FirstOrDefault()
            ?? throw new TimeoutException("Brak ostrzeżenia jakości do poprawienia.");
        await Task.Delay(CorrectionAfterDisplay);
        report.BeginCorrection(usage, provider.Requests);
        await orchestrator.SaveManualCorrectionAsync(entry, CorrectedText);
        report.CorrectionSaved();
        await report.CorrectionShown.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(CorrectionObservation);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in ReactionReplay.");
    }

    private sealed class ScriptedProvider(string translation) : ITranslationProvider
    {
        public const string ProviderName = "Scripted";
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string Name => ProviderName;
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(_ => translation).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ReactionReplay"));
    }

    private sealed class ScriptedOcr(Report report) : IOcrProvider
    {
        public string Name => "Scripted reading of the fixture label";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = report.OcrStarted(bitmap.Width, bitmap.Height);
            var (box, scene) = FindLabel(bitmap, cancellationToken);
            report.OcrCompleted(started, scene, bitmap.Width, bitmap.Height);
            if (scene == 0) return Task.FromResult(new OcrResult([], languageTag));
            var text = scene == 2 ? LabelB : LabelA;
            return Task.FromResult(new OcrResult([new OcrLine(text, box, [new OcrWord(text, box)])], languageTag));
        }

        private static (RectPx Box, int Scene) FindLabel(OcrBitmap bitmap, CancellationToken cancellationToken)
        {
            var left = bitmap.Width;
            var top = bitmap.Height;
            var right = -1;
            var bottom = -1;
            long blue = 0;
            var count = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = y * bitmap.Stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = row + x * 4;
                    if (bitmap.PixelsBgra32[offset + 1] < 240 || bitmap.PixelsBgra32[offset + 2] < 240) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                    blue += bitmap.PixelsBgra32[offset];
                    count++;
                }
            }
            if (count == 0) return (default, 0);
            var scene = blue / count >= 200 ? 1 : 2;
            return (new RectPx(left, top, right - left + 1, bottom - top + 1), scene);
        }
    }

    private sealed class TestWindow(Window window, TextBlock label, Thread thread)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;

        public static async Task<TestWindow> CreateAsync(string text, Color color)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var label = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                        Foreground = new SolidColorBrush(color), Margin = new Thickness(14, 18, 14, 0), TextWrapping = TextWrapping.NoWrap };
                    TextOptions.SetTextRenderingMode(label, TextRenderingMode.Grayscale);
                    var panel = new Border { Width = 400, Height = 64, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)), Child = label };
                    Canvas.SetLeft(panel, 30);
                    Canvas.SetTop(panel, 110);
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    canvas.Children.Add(panel);
                    var stripe = new Border { Width = 30, Height = 300, Background = Brushes.Gray };
                    Canvas.SetLeft(stripe, 450);
                    canvas.Children.Add(stripe);
                    var window = new Window { Title = "GTO ReactionReplay - local test", Width = 500, Height = 300,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, label, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task AfterRenderingAsync() => OnRenderingAsync(null);

        public Task ChangeAsync(string text, Color color, Report report) => OnRenderingAsync(() =>
        {
            label.Text = text;
            label.Foreground = new SolidColorBrush(color);
            report.Event(new { type = "label_mutated" });
        }, report.LabelRendered);

        private async Task OnRenderingAsync(Action? mutate, Action? rendered = null)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                mutate?.Invoke();
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    rendered?.Invoke();
                    done.TrySetResult();
                };
                CompositionTarget.Rendering += handler;
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        private readonly Lock _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _scenario;
        private readonly Dictionary<int, TaskCompletionSource> _firstOcr = new() { [1] = New(), [2] = New() };
        private readonly Dictionary<int, TaskCompletionSource> _displayed = new() { [1] = New(), [2] = New() };
        private readonly List<double> _ocrStarts = [];
        private readonly List<(double At, bool Full)> _ocrStartsFull = [];
        private readonly List<(string Kind, double At)> _changes = [];
        private TaskCompletionSource? _nextFullOcr;
        private (int Width, int Height)? _fullSize;
        private bool _ended, _screenFallback, _sessionStopped;
        private int _clears, _hides, _cancelledFrames;
        private double? _singleChangeAt, _singleRereadAt, _singleRereadDoneAt, _singleDisplayedAt;
        private bool? _singleRereadFull;
        private double? _burstFirstAt, _burstLastAt, _burstRereadAt, _burstDisplayedAt, _labelRenderedAt;
        private bool? _burstRereadFull;
        private bool _singlePending, _burstPending;
        private int _ocrDuringBurst;
        private double? _correctionStartedAt, _correctionSavedAt, _correctionShownAt, _subtitleCorrectedAt, _lastFullOcrBeforeCorrection;
        private int _ocrBeforeCorrectionShown, _oldAfterCorrection, _correctionCallbacks;
        private string? _keyBefore, _keyAfter;
        private RectPx? _boxBefore, _boxAfter;
        private object? _coverBefore, _coverAfter;
        private bool _correctionKeptAtEnd;
        private int _providerRequestsAtCorrection;
        private long _apiRequestsAtCorrection;

        public TaskCompletionSource CorrectionShown { get; } = New();

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Report(string output, string scenario)
        {
            _scenario = scenario;
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new
            {
                type = "header", scenario, ocrMode = "scripted",
                provider = scenario == "manual-correction" ? "Scripted (instant, drops the number)" : "Mock",
                providerDelayMs = scenario == "manual-correction" ? 0 : MockDelay.TotalMilliseconds,
                privateMode = true, cache = "fresh-memory", network = "blocked", staticRescanIntervalMs = 4000,
                changeAfterOcrMs = ChangeAfterOcr.TotalMilliseconds, burstChanges = BurstChanges, burstStepMs = BurstStep.TotalMilliseconds,
                correctionAfterDisplayMs = CorrectionAfterDisplay.TotalMilliseconds, correctionObservationMs = CorrectionObservation.TotalMilliseconds,
                settingsChangeMeaning = "TranslationOrchestrator.RebuildPipeline() with unchanged settings, as after a pipeline-relevant settings change",
                timingMeaning = "From the call to session callbacks or OCR calls; not physical overlay presentation",
            });
        }

        public bool FixtureValid
        {
            get
            {
                lock (_gate)
                {
                    if (_screenFallback || _sessionStopped) return false;
                    return _scenario == "manual-correction"
                        ? _correctionSavedAt is not null && _keyBefore is not null
                        : _singlePending && _burstPending && _burstLastAt is not null && _cancelledFrames >= 2;
                }
            }
        }

        public Task FirstOcr(int scene) => _firstOcr[scene].Task;

        public Task Displayed(int scene) => _displayed[scene].Task;

        public void Event(object data) { lock (_gate) Write(data); }

        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        private double Now => _clock.Elapsed.TotalMilliseconds;

        public void EndObservation() { lock (_gate) _ended = true; }

        public Task ArmNextFullOcr()
        {
            lock (_gate)
            {
                _nextFullOcr = New();
                return _nextFullOcr.Task;
            }
        }

        public void LabelRendered()
        {
            lock (_gate)
            {
                _labelRenderedAt = Now;
                Write(new { type = "label_rendering" });
            }
        }

        public void SettingsChange(string kind, UsageTracker usage)
        {
            lock (_gate)
            {
                var now = Now;
                _changes.Add((kind, now));
                if (kind == "single")
                {
                    _singleChangeAt = now;
                    _singlePending = usage.ReservedApiCharacters > 0;
                }
                if (kind == "burst-first")
                {
                    _burstFirstAt = now;
                    _burstPending = usage.ReservedApiCharacters > 0;
                }
                if (kind == "burst-last") _burstLastAt = now;
                Write(new { type = "settings_change", kind, reservedCharacters = usage.ReservedApiCharacters, completedMockRequests = usage.ApiRequests });
            }
        }

        public double OcrStarted(int width, int height)
        {
            lock (_gate)
            {
                var now = Now;
                _fullSize ??= (width, height);
                var full = _fullSize == (width, height);
                _ocrStarts.Add(now);
                _ocrStartsFull.Add((now, full));
                if (_singleChangeAt is not null && _singleRereadAt is null && now > _singleChangeAt)
                {
                    _singleRereadAt = now;
                    _singleRereadFull = full;
                }
                if (_burstFirstAt is not null && _burstLastAt is null && now > _burstFirstAt) _ocrDuringBurst++;
                if (_burstLastAt is not null && _burstRereadAt is null && now > _burstLastAt)
                {
                    _burstRereadAt = now;
                    _burstRereadFull = full;
                }
                if (_correctionSavedAt is not null && _correctionShownAt is null) _ocrBeforeCorrectionShown++;
                Write(new { type = "ocr_started", width, height, full });
                return now;
            }
        }

        public void OcrCompleted(double startedAt, int scene, int width, int height)
        {
            lock (_gate)
            {
                var now = Now;
                if (scene > 0) _firstOcr[scene].TrySetResult();
                if (_singleRereadAt == startedAt) _singleRereadDoneAt = now;
                if (_fullSize == (width, height)) _nextFullOcr?.TrySetResult();
                Write(new { type = "ocr_completed", scene, durationMs = now - startedAt });
            }
        }

        public void BeginCorrection(UsageTracker usage, int providerRequests)
        {
            lock (_gate)
            {
                _correctionStartedAt = Now;
                _providerRequestsAtCorrection = providerRequests;
                _apiRequestsAtCorrection = usage.ApiRequests;
                _lastFullOcrBeforeCorrection = _ocrStartsFull.LastOrDefault(static s => s.Full).At;
                Write(new { type = "correction_started", msSinceLastFullOcr = _correctionStartedAt - _lastFullOcrBeforeCorrection });
            }
        }

        public void CorrectionSaved()
        {
            lock (_gate)
            {
                _correctionSavedAt = Now;
                Write(new { type = "correction_saved", saveMs = _correctionSavedAt - _correctionStartedAt });
            }
        }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                var now = Now;
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Stopped)
                {
                    _sessionStopped = !_ended;
                    return;
                }
                if (_ended) return;
                if (update.ClearOverlay) _clears++;
                if (update.HideOverlay) _hides++;
                if (update.StatusLine.Contains("ustawienia zmienione", StringComparison.OrdinalIgnoreCase)) _cancelledFrames++;
                var blocks = update.Blocks ?? [];
                var scenes = blocks.Select(b => ClassifyText(b.TranslatedText)).Where(static s => s > 0).Distinct().ToArray();
                foreach (var scene in scenes)
                {
                    if (_scenario == "manual-correction")
                    {
                        if (scene == 1) _displayed[1].TrySetResult();
                        continue;
                    }
                    if (scene == 1 && _singleChangeAt is not null)
                    {
                        _singleDisplayedAt ??= now;
                        _displayed[1].TrySetResult();
                    }
                    if (scene == 2 && _burstLastAt is not null)
                    {
                        _burstDisplayedAt ??= now;
                        _displayed[2].TrySetResult();
                    }
                }
                var corrected = blocks.FirstOrDefault(static b => b.TranslatedText == CorrectedText);
                var flagged = blocks.FirstOrDefault(static b => b.TranslatedText == Flagged);
                if (_scenario == "manual-correction")
                {
                    if (_correctionStartedAt is null && flagged is not null)
                    {
                        _keyBefore = flagged.Key;
                        _boxBefore = flagged.ScreenBox;
                        _coverBefore = flagged.Cover;
                    }
                    if (_correctionStartedAt is not null && update.Blocks is not null)
                    {
                        _correctionCallbacks++;
                        _correctionKeptAtEnd = corrected is not null;
                        if (corrected is not null && _correctionShownAt is null)
                        {
                            _correctionShownAt = now;
                            _keyAfter = corrected.Key;
                            _boxAfter = corrected.ScreenBox;
                            _coverAfter = corrected.Cover;
                            CorrectionShown.TrySetResult();
                        }
                        if (_correctionShownAt is not null && flagged is not null) _oldAfterCorrection++;
                    }
                    if (_correctionStartedAt is not null && update.SubtitleText?.Contains(CorrectedText, StringComparison.Ordinal) == true)
                        _subtitleCorrectedAt ??= now;
                }
                Write(new
                {
                    type = "update", blockCount = update.Blocks?.Count, scenes, update.ClearOverlay, update.HideOverlay,
                    corrected = corrected is not null, flagged = flagged is not null,
                    subtitle = update.SubtitleText is { Length: > 0 }, completedOcr = update.Diagnostics is not null,
                    partialOcr = update.Diagnostics?.PartialOcr, cancelledFrame = update.StatusLine.Contains("ustawienia zmienione", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage, int providerRequests)
        {
            bool expected;
            lock (_gate)
            {
                if (_scenario == "manual-correction")
                {
                    var shownMs = _correctionShownAt - _correctionSavedAt;
                    expected = shownMs is < 500 && _ocrBeforeCorrectionShown == 0 && _keyAfter is not null && _keyAfter == _keyBefore
                        && _boxAfter == _boxBefore && _coverBefore is not null && ReferenceEquals(_coverAfter, _coverBefore) && _clears == 0 && _hides == 0 && _oldAfterCorrection == 0
                        && _correctionKeptAtEnd && providerRequests == _providerRequestsAtCorrection && _subtitleCorrectedAt is not null;
                    Write(new
                    {
                        type = "summary", exitCode, reason, fixtureValid = FixtureValid, screenFallback = _screenFallback,
                        correctionSaveMs = _correctionSavedAt - _correctionStartedAt,
                        msSinceLastFullOcrAtCorrection = _correctionStartedAt - _lastFullOcrBeforeCorrection,
                        correctedShownAfterSaveMs = shownMs,
                        correctedShownAfterCallMs = _correctionShownAt - _correctionStartedAt,
                        ocrBeforeCorrectionShown = _ocrBeforeCorrectionShown,
                        subtitleCorrectedAfterSaveMs = _subtitleCorrectedAt - _correctionSavedAt,
                        sameKey = _keyAfter is not null && _keyAfter == _keyBefore, sameBox = _keyAfter is not null && _boxAfter == _boxBefore,
                        coverPreserved = _coverBefore is not null && ReferenceEquals(_coverAfter, _coverBefore),
                        coverPresent = _coverBefore is not null,
                        oldCallbacksAfterCorrection = _oldAfterCorrection, correctionKeptAtEnd = _correctionKeptAtEnd,
                        callbacksAfterCorrection = _correctionCallbacks, clearCallbacks = _clears, hideCallbacks = _hides,
                        providerRequests, providerRequestsAfterCorrection = providerRequests - _providerRequestsAtCorrection,
                        apiRequestsAfterCorrection = usage.ApiRequests - _apiRequestsAtCorrection,
                        ocrCalls = _ocrStarts.Count, expectedBehavior = expected,
                    });
                }
                else
                {
                    var singleReread = _singleRereadAt - _singleChangeAt;
                    var burstReread = _burstRereadAt - _burstLastAt;
                    expected = singleReread is < 500 && _singleRereadFull == true && burstReread is < 500 && _burstRereadFull == true
                        && _ocrDuringBurst == 0 && _singleDisplayedAt is not null && _burstDisplayedAt is not null && _clears == 0 && _hides == 0;
                    Write(new
                    {
                        type = "summary", exitCode, reason, fixtureValid = FixtureValid, screenFallback = _screenFallback,
                        cancelledFrames = _cancelledFrames, providerPendingAtSingleChange = _singlePending,
                        providerPendingAtBurstStart = _burstPending,
                        singleRereadStartMs = singleReread, singleRereadFullFrame = _singleRereadFull,
                        singleRereadCompletedMs = _singleRereadDoneAt - _singleChangeAt,
                        singleDisplayedMs = _singleDisplayedAt - _singleChangeAt,
                        burstDurationMs = _burstLastAt - _burstFirstAt, ocrDuringBurst = _ocrDuringBurst,
                        burstRereadStartAfterLastMs = burstReread, burstRereadFullFrame = _burstRereadFull,
                        burstDisplayedAfterLastMs = _burstDisplayedAt - _burstLastAt,
                        labelBToBurstMs = _burstFirstAt - _labelRenderedAt,
                        ocrCalls = _ocrStarts.Count, mockProviderRequests = usage.ApiRequests,
                        reservedCharactersAtEnd = usage.ReservedApiCharacters, clearCallbacks = _clears, hideCallbacks = _hides,
                        expectedBehavior = expected,
                    });
                }
            }
            Console.WriteLine($"ReactionReplay {_scenario}: {reason}; oczekiwane {expected}.");
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
