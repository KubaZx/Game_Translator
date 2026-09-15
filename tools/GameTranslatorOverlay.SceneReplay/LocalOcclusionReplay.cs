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

// Real OCR over an owned window. Only the small right-hand panel changes; its new
// text sits above the old Inspect box, so a new translated box cannot evict it by overlap.
internal static class LocalOcclusionReplay
{
    private const string Inventory = "Inventory panel";
    private const string Journal = "Journal panel";
    private const string Inspect = "Inspect";
    private const string Description = "Ancient records";
    private const int ObservationMs = 12000;
    private const int WindowWidth = 1000;
    private const int WindowHeight = 600;
    private const int PanelWidth = 260;
    private const int PanelHeight = 150;

    public static int Run(string output, string scenario)
    {
        if (scenario is not ("local-occlusion" or "local-occlusion-hover" or "local-occlusion-inflight"))
            throw new ArgumentException("Unknown local occlusion scenario.", nameof(scenario));
        using var report = new Report(output, scenario);
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-localocclusion-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings
        {
            Provider = MockTranslationProvider.ProviderName, PrivateMode = true,
            ActiveProfileId = null, OcrUpscale = 1,
        };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var ocr = new ObservedOcr(new WindowsOcrProvider(), report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromSeconds(2) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            // Fresh tool-owned paths and private memory only; no settings load or SQLite initialization.
            orchestrator.Initialize();
            if (!ocr.IsLanguageAvailable(settings.SourceLanguage))
                throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
            window = await TestWindow.CreateAsync();
            await window.AfterRenderingAsync();
            var options = new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true };
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle, options,
                report.Update, logging.CreateLogger("LocalOcclusionReplay"));
            session.Start();
            if (report.Inflight)
            {
                await report.InitialOcrCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(250);
            }
            else
            {
                await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            await window.ChangePanelAsync(report, usage);
            await Task.Delay(ObservationMs);
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
        {
            exitCode = 4;
            reason = "fixture_preconditions_failed";
        }
        return report.Finish(exitCode, reason, usage);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in LocalOcclusionReplay.");
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

    private sealed class TestWindow(Window window, Border panel, TextBlock inspect, TextBlock description, Thread thread)
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
                    static TextBlock Label(string text, double x, double y) => new()
                    {
                        Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                        Foreground = Brushes.White, Margin = new Thickness(x, y, 0, 0),
                        TextWrapping = TextWrapping.NoWrap,
                    };
                    var inventory = Label(Inventory, 80, 65);
                    var journal = Label(Journal, 80, 150);
                    var inspect = Label(Inspect, 18, 100);
                    var description = Label(Description, 18, 16);
                    description.Visibility = Visibility.Collapsed;
                    var panelContents = new Canvas();
                    panelContents.Children.Add(inspect);
                    panelContents.Children.Add(description);
                    var panel = new Border
                    {
                        Width = PanelWidth, Height = PanelHeight,
                        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)), Child = panelContents,
                    };
                    Canvas.SetLeft(panel, 440);
                    Canvas.SetTop(panel, 260);
                    // A fixed gray stripe prevents PrintWindow's empty-image heuristic
                    // from rejecting a mostly dark, sparse synthetic screen.
                    var reference = new Border { Width = 50, Height = WindowHeight, Background = Brushes.Gray };
                    Canvas.SetLeft(reference, WindowWidth - 50);
                    var content = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    content.Children.Add(reference);
                    content.Children.Add(inventory);
                    content.Children.Add(journal);
                    content.Children.Add(panel);
                    var window = new Window
                    {
                        Title = "GTO LocalOcclusionReplay - local test", Width = WindowWidth, Height = WindowHeight,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = content,
                    };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, panel, inspect, description, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task AfterRenderingAsync() => RenderAsync(null);

        public Task ChangePanelAsync(Report report, UsageTracker usage) => RenderAsync(() =>
        {
            panel.Background = new SolidColorBrush(Color.FromRgb(90, 90, 90));
            if (!report.Hover)
            {
                inspect.Visibility = Visibility.Collapsed;
                description.Visibility = Visibility.Visible;
            }
            report.Event(new { type = "local_panel_mutated", sourceInspectRemains = report.Hover });
        }, () => report.BeginMeasurement(usage));

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
        private double? _startedAt, _endedAt, _firstInspectRemovedAt, _firstDescriptionAt, _firstDescriptionOcrAt;
        private double? _firstInitialOcrAt, _initialOcrToChangeMs;
        private bool? _inflightPreconditionMet;
        private long? _requestsAtChange, _charactersAtChange, _reservedAtChange;
        private int _oldInspectCallbacksAfterChange;
        private int _ocrOrdinal, _firstInitialOcrOrdinal, _diagnosticCount, _initialPreChangeSceneCuts;
        private bool _ended, _screenFallback, _stoppedDuringMeasurement;
        private bool _visibleInspect, _visibleInventory, _visibleJournal, _visibleDescription;
        private bool _inspectPresentWhenDescriptionFirstShown;
        private int _observedInspect, _observedDescription, _observedEmpty;
        private int _visualUpdates, _menuLossUpdates, _hoverInspectLossUpdates, _inspectReturnUpdates;
        private int _clears, _hides, _sceneCuts, _completedOcr, _retained, _reused;

        public bool Hover { get; }
        public bool Inflight { get; }
        public TaskCompletionSource InitialOcrCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Report(string output, string scenario)
        {
            Hover = scenario == "local-occlusion-hover";
            Inflight = scenario == "local-occlusion-inflight";
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            Event(new
            {
                type = "header", scenario, ocrMode = "windows", provider = "Mock", providerDelayMs = 2000,
                privateMode = true, cache = "fresh-memory", network = "blocked", capture = "own-window-only",
                observationMs = ObservationMs, staticRescanIntervalMs = 4000, fps = 6, ocrUpscale = 1,
                panelFraction = (double)PanelWidth * PanelHeight / (WindowWidth * WindowHeight),
                panelX = 440, panelY = 260, panelWidth = PanelWidth, panelHeight = PanelHeight,
                panelGrayBefore = 40, panelGrayAfter = 90, sourceInspectRemains = Hover,
                changeDuringInitialTranslation = Inflight, initialOcrToMutationDelayMs = Inflight ? 250 : (int?)null,
                descriptionAndInspectSeparatedVerticallyDip = 84, stableMenuLabels = 2,
                entireBackgroundChanges = false, actualOverlayWindow = false,
                sourceLengthInspect = Inspect.Length, sourceLengthDescription = Description.Length,
                timingMeaning = "From WPF Rendering after local panel mutation to session callbacks; not physical overlay presentation",
                validityMeaning = "Real target OCR at least twice, no screen fallback, no global scene cut, and at least 3 seconds after the target result",
                initialSceneCutMeaning = "Inflight excludes only the first diagnostic for OCR 1 completed before the panel change; its initialization SceneCut flag remains logged separately",
                resultMeaning = Hover
                    ? "The unchanged Inspect and both menu labels remain visible through every visual update"
                    : Inflight
                        ? "No old Inspect callback after the panel changes during pending translation; the new description and both menu labels are shown"
                        : "Only Inspect disappears before the new description is ready; both menu labels remain and Inspect never returns",
            });
        }

        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        public void BeginMeasurement(UsageTracker usage)
        {
            lock (_gate)
            {
                _startedAt = _clock.Elapsed.TotalMilliseconds;
                if (Inflight)
                {
                    _initialOcrToChangeMs = _startedAt - _firstInitialOcrAt;
                    _requestsAtChange = usage.ApiRequests;
                    _charactersAtChange = usage.ApiCharacters;
                    _reservedAtChange = usage.ReservedApiCharacters;
                    _inflightPreconditionMet = _initialOcrToChangeMs is >= 250 and < 2000
                        && _requestsAtChange == 0 && _charactersAtChange == 0 && _reservedAtChange > 0;
                }
                Write(new
                {
                    type = "measurement_started", sourceInspectRemains = Hover,
                    inflightPreconditionMet = _inflightPreconditionMet,
                    initialOcrToPanelChangeMs = _initialOcrToChangeMs,
                    mockRequestsAtPanelChange = _requestsAtChange,
                    mockCharactersAtPanelChange = _charactersAtChange,
                    reservedCharactersAtPanelChange = _reservedAtChange,
                });
            }
        }

        public void EndMeasurement()
        {
            lock (_gate)
            {
                _ended = true;
                _endedAt = _clock.Elapsed.TotalMilliseconds;
            }
        }

        private static bool Contains(string text, string phrase) =>
            (" " + TextNormalizer.Normalize(text) + " ").Contains(" " + phrase + " ", StringComparison.OrdinalIgnoreCase);

        public void Ocr(OcrResult result, int width, int height)
        {
            var combined = string.Join(" ", result.Lines.Select(static line => line.Text));
            var hasInspect = Contains(combined, Inspect);
            var hasDescription = Contains(combined, Description);
            lock (_gate)
            {
                _ocrOrdinal++;
                if (_firstInitialOcrAt is null && hasInspect && Contains(combined, Inventory) && Contains(combined, Journal))
                {
                    _firstInitialOcrAt = _clock.Elapsed.TotalMilliseconds;
                    _firstInitialOcrOrdinal = _ocrOrdinal;
                    InitialOcrCompleted.TrySetResult();
                }
                var measured = _startedAt is not null && !_ended;
                if (measured)
                {
                    if (hasInspect) _observedInspect++;
                    if (hasDescription)
                    {
                        _observedDescription++;
                        _firstDescriptionOcrAt ??= _clock.Elapsed.TotalMilliseconds;
                    }
                    if (result.Lines.Count == 0) _observedEmpty++;
                }
                Write(new
                {
                    type = "ocr_completed", ocrOrdinal = _ocrOrdinal, measured, hasInspect, hasDescription,
                    hasInventory = Contains(combined, Inventory), hasJournal = Contains(combined, Journal),
                    rawLines = result.Lines.Count, width, height,
                });
            }
        }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Stopped)
                {
                    _stoppedDuringMeasurement |= _startedAt is not null && !_ended;
                    Write(new { type = "session_stopped", measured = _startedAt is not null && !_ended });
                    return;
                }
                if (_ended) return; // Session shutdown is never counted as successful removal.
                // Session OCR is serial. A later OCR completion prevents this exemption,
                // including when the initial translation was discarded without a callback.
                // Never ignore the first measured SceneCut unconditionally: it may be real.
                var initialPreChangeSceneCut = update.Diagnostics is { SceneCut: true, PartialOcr: false }
                    && Inflight && _inflightPreconditionMet == true
                    && _startedAt is { } started && _firstInitialOcrAt is { } initial && initial < started
                    && _firstInitialOcrOrdinal == 1 && _ocrOrdinal == 1 && _diagnosticCount == 0;
                if (update.Diagnostics is not null) _diagnosticCount++;
                var visualUpdate = update.Blocks is not null || update.ClearOverlay || update.HideOverlay;
                if (update.ClearOverlay || update.HideOverlay)
                {
                    _visibleInspect = _visibleInventory = _visibleJournal = _visibleDescription = false;
                }
                if (update.Blocks is { } blocks)
                {
                    _visibleInspect = blocks.Any(static block => Contains(block.TranslatedText, Inspect));
                    _visibleInventory = blocks.Any(static block => Contains(block.TranslatedText, Inventory));
                    _visibleJournal = blocks.Any(static block => Contains(block.TranslatedText, Journal));
                    _visibleDescription = blocks.Any(static block => Contains(block.TranslatedText, Description));
                }
                if (_startedAt is null && _visibleInspect && _visibleInventory && _visibleJournal)
                    InitialDisplayed.TrySetResult();
                if (_startedAt is not null)
                {
                    if (visualUpdate)
                    {
                        _visualUpdates++;
                        // Inflight starts before any initial translation is shown.
                        // Its stable menu requirement begins with the new result.
                        var menuRequired = !Inflight || _firstDescriptionAt is not null || _visibleDescription;
                        if (menuRequired && (!_visibleInventory || !_visibleJournal)) _menuLossUpdates++;
                        if (_visibleInspect) _oldInspectCallbacksAfterChange++;
                        if (Hover && !_visibleInspect) _hoverInspectLossUpdates++;
                        if (!Hover && !Inflight && !_visibleInspect) _firstInspectRemovedAt ??= _clock.Elapsed.TotalMilliseconds;
                        if (!Hover && _firstInspectRemovedAt is not null && _visibleInspect) _inspectReturnUpdates++;
                        if (_visibleDescription && _firstDescriptionAt is null)
                        {
                            _firstDescriptionAt = _clock.Elapsed.TotalMilliseconds;
                            _inspectPresentWhenDescriptionFirstShown = _visibleInspect;
                        }
                    }
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (update.Diagnostics is { } frame)
                    {
                        _completedOcr++;
                        if (initialPreChangeSceneCut) _initialPreChangeSceneCuts++;
                        else if (frame.SceneCut) _sceneCuts++;
                        _retained += frame.RetainedBlocks;
                        _reused += frame.ReusedBlocks;
                    }
                }
                Write(new
                {
                    type = "update", measured = _startedAt is not null, visualUpdate, blockCount = update.Blocks?.Count,
                    visibleInspect = _visibleInspect, visibleInventory = _visibleInventory,
                    visibleJournal = _visibleJournal, visibleDescription = _visibleDescription,
                    update.ClearOverlay, update.HideOverlay, completedOcr = update.Diagnostics is not null,
                    sceneCut = update.Diagnostics?.SceneCut, partialOcr = update.Diagnostics?.PartialOcr,
                    latestCompletedOcrOrdinal = _ocrOrdinal, initialPreChangeSceneCut,
                    retainedBlocks = update.Diagnostics?.RetainedBlocks, reusedBlocks = update.Diagnostics?.ReusedBlocks,
                });
            }
        }

        public bool FixtureValid
        {
            get
            {
                lock (_gate)
                {
                    var targetObserved = Hover ? _observedInspect >= 2 : _observedDescription >= 2;
                    var watchedLongEnough = Hover || (_firstDescriptionAt is { } ready && _endedAt is { } end && end - ready >= 3000);
                    return _startedAt is not null && !_screenFallback && !_stoppedDuringMeasurement
                        && _sceneCuts == 0 && targetObserved && watchedLongEnough && (!Inflight || _inflightPreconditionMet == true);
                }
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var removedBeforeDescription = _firstInspectRemovedAt is { } removed && _firstDescriptionAt is { } ready && removed < ready;
                var menuPreserved = _menuLossUpdates == 0 && _visibleInventory && _visibleJournal;
                var expected = FixtureValid && menuPreserved && _clears == 0 && _hides == 0
                    && (Hover
                        ? _hoverInspectLossUpdates == 0 && _visibleInspect && !_visibleDescription
                        : Inflight
                            ? _oldInspectCallbacksAfterChange == 0 && !_visibleInspect && _visibleDescription
                            : removedBeforeDescription && !_visibleInspect && _visibleDescription && _inspectReturnUpdates == 0);
                Write(new
                {
                    type = "summary", exitCode, reason, fixtureValid = FixtureValid, screenFallback = _screenFallback,
                    observedInspect = _observedInspect, observedDescription = _observedDescription, observedEmpty = _observedEmpty,
                    inflightPreconditionMet = _inflightPreconditionMet,
                    initialOcrToPanelChangeMs = _initialOcrToChangeMs,
                    mockRequestsAtPanelChange = _requestsAtChange,
                    mockCharactersAtPanelChange = _charactersAtChange,
                    reservedCharactersAtPanelChange = _reservedAtChange,
                    oldInspectCallbacksAfterChange = _oldInspectCallbacksAfterChange,
                    completedOcr = _completedOcr, visualUpdates = _visualUpdates,
                    firstDescriptionOcrMs = _firstDescriptionOcrAt - _startedAt,
                    firstInspectRemovedMs = _firstInspectRemovedAt - _startedAt,
                    firstDescriptionReadyMs = _firstDescriptionAt - _startedAt,
                    observationAfterDescriptionMs = _endedAt - _firstDescriptionAt,
                    inspectRemovedBeforeDescription = removedBeforeDescription,
                    inspectPresentWhenDescriptionFirstShown = _inspectPresentWhenDescriptionFirstShown,
                    inspectReturnUpdates = _inspectReturnUpdates, hoverInspectLossUpdates = _hoverInspectLossUpdates,
                    menuLossUpdates = _menuLossUpdates, stableMenuPreserved = menuPreserved,
                    inspectVisibleAtEnd = _visibleInspect, descriptionVisibleAtEnd = _visibleDescription,
                    sceneCutFrames = _sceneCuts, initialPreChangeSceneCutFrames = _initialPreChangeSceneCuts,
                    clearCallbacks = _clears, hideCallbacks = _hides,
                    retainedBlocks = _retained, reusedBlocks = _reused, expectedBehavior = expected,
                    mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters,
                    usage.CacheHits, usage.GlossaryHits,
                });
                Console.WriteLine($"LocalOcclusionReplay: {reason}; fixture={FixtureValid}; expected={expected}; menu losses={_menuLossUpdates}.");
            }
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}