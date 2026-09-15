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
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using TextBlock = System.Windows.Controls.TextBlock;

// Only this owned window is captured. A fixed opaque panel stays unchanged while
// the surrounding scene changes; later its own labels change and disappear.
internal static class StaticHudReplay
{
    private const string Inventory = "Inventory panel";
    private const string Journal = "Journal panel";
    private const string Inspect = "Inspect this gate";
    private const string Final = "New room description";

    public static int Run(string output, bool forceWhiffs = false, bool smallMotion = false)
    {
        using var report = new Report(output, forceWhiffs, smallMotion);
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-statichud-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetwork());
        var ocr = new ObservedOcr(new WindowsOcrProvider(), report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromMilliseconds(200) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            orchestrator.Initialize();
            if (!ocr.IsLanguageAvailable(settings.SourceLanguage)) throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
            window = await TestWindow.CreateAsync(report);
            await window.RenderAsync();
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true }, report.Update, logging.CreateLogger("StaticHudReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await window.BeginMotionAsync();
            await Task.Delay(report.StablePhaseMs);
            await window.ReplaceJournalAsync();
            await Task.Delay(2200);
            await window.RemoveHudAsync();
            await Task.Delay(1400);
            await window.FinishMotionAsync();
            await report.FinalDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(1400);
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex is TimeoutException ? "scenario_timeout" : "runtime_error";
            report.Event(new { type = "error", errorType = ex.GetType().Name });
        }
        finally
        {
            report.End();
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
        if (exitCode == 0 && !report.Valid) { exitCode = 4; reason = "fixture_preconditions_failed"; }
        return report.Finish(exitCode, reason, usage);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in StaticHudReplay.");
    }

    private sealed class ObservedOcr(IOcrProvider inner, Report report) : IOcrProvider
    {
        public string Name => inner.Name;
        public int MaxImageDimension => inner.MaxImageDimension;
        public IReadOnlyList<string> AvailableLanguages => inner.AvailableLanguages;
        public bool IsLanguageAvailable(string languageTag) => inner.IsLanguageAvailable(languageTag);
        public async Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            if (report.ShouldForceWhiff)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var empty = OcrResult.Empty(languageTag);
                report.Ocr(empty, forced: true);
                return empty;
            }
            var result = await inner.RecognizeAsync(bitmap, languageTag, cancellationToken).ConfigureAwait(false);
            report.Ocr(result);
            return result;
        }
    }

    private sealed class TestWindow(Window window, Canvas world, TextBlock inventory, TextBlock journal,
        TextBlock inspect, DispatcherTimer timer, Thread thread, Report report)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;
        public static async Task<TestWindow> CreateAsync(Report report)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    static TextBlock Label(string text, double x, double y) => new()
                    {
                        Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                        Foreground = Brushes.White, Margin = new Thickness(x, y, 0, 0),
                    };
                    var inventory = Label(Inventory, 22, 24);
                    var journal = Label(Journal, 22, 135);
                    var panelContent = new Canvas();
                    panelContent.Children.Add(inventory);
                    panelContent.Children.Add(journal);
                    var panel = new Border { Width = 360, Height = 240, Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)), Child = panelContent };
                    Canvas.SetLeft(panel, 65);
                    Canvas.SetTop(panel, 60);
                    var inspect = Label(Inspect, 620, 410);
                    var world = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    var movingBackground = new Border { Width = 400, Height = 760, Background = world.Background };
                    Canvas.SetLeft(movingBackground, 600);
                    if (report.SmallMotion) world.Children.Add(movingBackground);
                    world.Children.Add(panel);
                    world.Children.Add(inspect);
                    var stripe = new Border { Width = 45, Height = 760, Background = Brushes.Gray };
                    Canvas.SetLeft(stripe, 1155);
                    world.Children.Add(stripe);
                    var window = new Window
                    {
                        Title = "GTO StaticHudReplay - local test", Width = 1200, Height = 760,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = world,
                    };
                    var tick = 0;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(71) };
                    timer.Tick += (_, _) =>
                    {
                        tick++;
                        var value = (byte)(20 + tick % 5 * 30);
                        if (report.SmallMotion) movingBackground.Background = new SolidColorBrush(Color.FromRgb(value, value, value));
                        else world.Background = new SolidColorBrush(Color.FromRgb(value, value, value));
                        inspect.Margin = new Thickness(620 + tick % 4 * 12, 410, 0, 0);
                        report.Tick();
                    };
                    window.Closed += (_, _) => { timer.Stop(); window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, world, inventory, journal, inspect, timer, thread!, report));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task BeginMotionAsync() => RenderAsync(timer.Start, () => report.Phase(1));
        public Task ReplaceJournalAsync() => RenderAsync(() => journal.Text = "Journal closed", () => report.Phase(2));
        public Task RemoveHudAsync() => RenderAsync(() =>
        {
            inventory.Visibility = Visibility.Collapsed;
            journal.Visibility = Visibility.Collapsed;
        }, () => report.Phase(3));
        public Task FinishMotionAsync() => RenderAsync(() =>
        {
            timer.Stop();
            world.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));
            inspect.Text = Final;
            inspect.Margin = new Thickness(620, 410, 0, 0);
        }, () => report.Phase(4));

        public async Task RenderAsync(Action? change = null, Action? afterRendering = null)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                change?.Invoke();
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    afterRendering?.Invoke();
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
        private int _phase, _ticks, _ocr, _fullOcr, _sceneCuts, _clears, _hides;
        private int _stableSamples, _inventoryLosses, _journalLosses, _journalStale, _hudStale;
        private int _stablePolls, _missingHudPolls, _forcedWhiffs;
        private readonly bool _forceWhiffs;
        private int _stableOcrFrames, _stableSceneCuts;
        public bool SmallMotion { get; }
        public int StablePhaseMs => SmallMotion ? 10500 : 4200;
        public bool ShouldForceWhiff { get { lock (_gate) return _forceWhiffs && _phase is >= 1 and <= 3; } }
        private bool _ended, _fallback, _stopped, _inventory, _journal, _final;
        private double _phaseAt, _finalAt;
        private double? _journalRemoved, _inventoryRemoved;
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Report(string output, bool forceWhiffs, bool smallMotion)
        {
            _forceWhiffs = forceWhiffs;
            SmallMotion = smallMotion;
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Write(new { type = "configuration", scenario = smallMotion ? "hud-motion-small-whiff" : forceWhiffs ? "hud-motion-whiff" : "hud-motion", ocr = forceWhiffs ? "windows with forced empty results during motion" : "windows", provider = "Mock", providerDelayMs = 200,
                fps = 6, stabilityMs = 250, motionPauseMs = 2500, privateCache = true, httpBlocked = true,
                scope = "Owned window; callback state, not physical overlay presentation", phaseDurationsMs = new[] { StablePhaseMs, 2200, 1400 } });
        }
        private void Write(object data) { _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data })); _writer.Flush(); }
        public void Event(object value) { lock (_gate) Write(value); }
        public void Tick()
        {
            lock (_gate)
            {
                _ticks++;
                if (_phase == 1)
                {
                    _stablePolls++;
                    if (!_inventory || !_journal) _missingHudPolls++;
                }
                if (_phase == 2 && !_inventory) _missingHudPolls++;
            }
        }
        public void Phase(int phase) { lock (_gate) { _phase = phase; _phaseAt = _clock.Elapsed.TotalMilliseconds; Write(new { type = "phase", phase, motionTicks = _ticks }); } }
        public void Ocr(OcrResult result, bool forced = false)
        {
            lock (_gate)
            {
                _ocr++;
                if (forced) _forcedWhiffs++;
                var combined = string.Join(" ", result.Lines.Select(static line => line.Text));
                Write(new { type = "ocr", phase = _phase, forced, hasInventory = combined.Contains(Inventory), hasJournal = combined.Contains(Journal),
                    hasFinal = combined.Contains(Final), lines = result.Lines.Count });
            }
        }
        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _fallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (_ended) return;
                if (update.Stopped) { _stopped = true; return; }
                if (update.ClearOverlay || update.HideOverlay) _inventory = _journal = _final = false;
                if (update.Blocks is { } blocks)
                {
                    _inventory = blocks.Any(static b => b.TranslatedText.Contains(Inventory));
                    _journal = blocks.Any(static b => b.TranslatedText.Contains(Journal));
                    _final = blocks.Any(static b => b.TranslatedText.Contains(Final));
                    if (_phase == 0 && _inventory && _journal && blocks.Any(static b => b.TranslatedText.Contains(Inspect))) InitialDisplayed.TrySetResult();
                }
                var phaseElapsed = _clock.Elapsed.TotalMilliseconds - _phaseAt;
                if (_phase > 0)
                {
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (update.Diagnostics is { } diag)
                    {
                        if (!diag.PartialOcr) _fullOcr++;
                        if (diag.SceneCut) _sceneCuts++;
                        if (_phase == 1) { _stableOcrFrames++; if (diag.SceneCut) _stableSceneCuts++; }
                    }
                    if (_phase == 1) { _stableSamples++; if (!_inventory) _inventoryLosses++; if (!_journal) _journalLosses++; }
                    if (_phase == 2)
                    {
                        if (!_inventory) _inventoryLosses++;
                        if (!_journal) _journalRemoved ??= phaseElapsed;
                        if (phaseElapsed > 700 && _journal) _journalStale++;
                    }
                    if (_phase == 3)
                    {
                        if (!_inventory) _inventoryRemoved ??= phaseElapsed;
                        if (phaseElapsed > 700 && (_inventory || _journal)) _hudStale++;
                    }
                    if (_phase == 4 && _final && !FinalDisplayed.Task.IsCompleted)
                    {
                        _finalAt = _clock.Elapsed.TotalMilliseconds;
                        FinalDisplayed.TrySetResult();
                    }
                }
                Write(new { type = "update", phase = _phase, phaseElapsedMs = phaseElapsed, hasInventory = _inventory, hasJournal = _journal,
                    hasFinal = _final, update.ClearOverlay, update.HideOverlay, sceneCut = update.Diagnostics?.SceneCut,
                    partialOcr = update.Diagnostics?.PartialOcr, hasBlocks = update.Blocks is not null, blockCount = update.Blocks?.Count });
            }
        }
        public bool Valid => InitialDisplayed.Task.IsCompletedSuccessfully && FinalDisplayed.Task.IsCompletedSuccessfully
            && !_fallback && !_stopped && _ticks >= 50 && _ocr >= 3 && _fullOcr >= 1 && (SmallMotion ? _stableOcrFrames >= 3 && _stableSceneCuts == 0 : _sceneCuts >= 1)
            && _stablePolls >= 30 && (!_forceWhiffs || _forcedWhiffs >= 1) && _clock.Elapsed.TotalMilliseconds - _finalAt >= 1000;
        public void End() { lock (_gate) _ended = true; }
        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var desired = Valid && _missingHudPolls == 0 && _inventoryLosses == 0 && _journalLosses == 0 && _journalStale == 0 && _hudStale == 0
                    && _journalRemoved is <= 700 && _inventoryRemoved is <= 700;
                Write(new { type = "summary", exitCode, reason, fixtureValid = Valid, expectedBehavior = desired,
                    motionTicks = _ticks, observedOcr = _ocr, stableOcrFrames = _stableOcrFrames, stableSceneCuts = _stableSceneCuts, measuredFullOcr = _fullOcr, measuredSceneCutFrames = _sceneCuts,
                    stableSamples = _stableSamples, stablePolls = _stablePolls, missingHudPolls = _missingHudPolls, forcedWhiffs = _forcedWhiffs, inventoryLossUpdates = _inventoryLosses, journalLossUpdates = _journalLosses,
                    journalRemovedMs = _journalRemoved, inventoryRemovedMs = _inventoryRemoved,
                    staleJournalUpdates = _journalStale, staleHudUpdates = _hudStale, clearCallbacks = _clears, hideCallbacks = _hides,
                    finalFreshPublished = _final, screenFallback = _fallback, mockProviderRequests = usage.ApiRequests,
                    mockProviderCharacters = usage.ApiCharacters, usage.CacheHits, usage.GlossaryHits });
                Console.WriteLine($"StaticHudReplay: {reason}; valid={Valid}; expected={desired}; HUD losses={_inventoryLosses + _journalLosses}.");
            }
            return exitCode;
        }
        public void Dispose() => _writer.Dispose();
    }
}