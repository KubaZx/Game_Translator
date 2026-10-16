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

internal static class QualityMarkReplay
{
    private const string Source = "Unlock level 20 door";
    private const string Flagged = "Otwórz drzwi poziomu";
    private const string Clean = "Otwórz drzwi poziomu 20";
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(4);

    public static int Run(string output, bool dropNumber)
    {
        using var report = new Report(output, dropNumber);
        return RunAsync(report, dropNumber).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report, bool dropNumber)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-qualitymark-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = ScriptedProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetwork());
        var ocr = new ScriptedOcr(report);
        var usage = new UsageTracker();
        var provider = new ScriptedProvider(dropNumber ? Flagged : Clean);
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider(), new DeepLTranslationProvider(http, static () => null),
            ocr, usage, logging, [provider]);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            orchestrator.Initialize();
            window = await TestWindow.CreateAsync(Source);
            await window.AfterRenderingAsync();
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true, StaticRescanInterval = TimeSpan.FromMilliseconds(500) },
                report.Update, logging.CreateLogger("QualityMarkReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            report.BeginObservation();
            await Task.Delay(Observation);
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
        return report.Finish(exitCode, reason, usage, provider.Requests, orchestrator.QualityWarnings);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in QualityMarkReplay.");
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
            Task.FromResult(new ProviderStatus(true, "QualityMarkReplay"));
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
            report.Ocr();
            var box = FindWhiteBox(bitmap, cancellationToken);
            return Task.FromResult(new OcrResult([new OcrLine(Source, box, [new OcrWord(Source, box)])], languageTag));
        }

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
                throw new InvalidOperationException("QualityMarkReplay could not locate its white label glyphs.");
            return new RectPx(left, top, right - left + 1, bottom - top + 1);
        }
    }

    private sealed class TestWindow(Window window, Thread thread)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;

        public static async Task<TestWindow> CreateAsync(string label)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var text = new TextBlock { Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 26,
                        Foreground = Brushes.White, Margin = new Thickness(14, 18, 14, 0), TextWrapping = TextWrapping.NoWrap };
                    var panel = new Border { Width = 360, Height = 70, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)), Child = text };
                    Canvas.SetLeft(panel, 80);
                    Canvas.SetTop(panel, 150);
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    canvas.Children.Add(panel);
                    var window = new Window { Title = "GTO QualityMarkReplay - local test", Width = 640, Height = 400,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
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
        private readonly Lock _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly bool _dropNumber;
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _boxes = new(StringComparer.Ordinal);
        private bool _observing, _ended, _screenFallback, _sessionStopped;
        private int _ocrReads, _blockCallbacks, _flaggedBlocks, _cleanBlocks, _subtitleUpdates, _flaggedSubtitles, _clears, _hides;

        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FixtureValid
        {
            get { lock (_gate) return !_screenFallback && !_sessionStopped && _ocrReads >= 4 && _blockCallbacks >= 3; }
        }

        public Report(string output, bool dropNumber)
        {
            _dropNumber = dropNumber;
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario = dropNumber ? "quality-mark" : "quality-mark-clean", ocrMode = "scripted",
                provider = "Scripted (instant)", providerDropsNumber = dropNumber, privateMode = true, cache = "fresh-memory",
                network = "blocked", observationMs = Observation.TotalMilliseconds, staticRescanIntervalMs = 500,
                expectedIssues = TranslationQualityGate.Check(Source, dropNumber ? Flagged : Clean).ToString() });
        }

        public void Event(object data) { lock (_gate) Write(data); }

        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        public void BeginObservation() { lock (_gate) _observing = true; }

        public void EndObservation() { lock (_gate) _ended = true; }

        public void Ocr() { lock (_gate) if (_observing && !_ended) _ocrReads++; }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Stopped)
                {
                    _sessionStopped = !_ended;
                    return;
                }
                if (_ended) return;
                if (update.Blocks is { Count: > 0 }) InitialDisplayed.TrySetResult();
                if (update.SubtitleText is { Length: > 0 })
                {
                    _subtitleUpdates++;
                    if (update.SubtitleQualityWarning) _flaggedSubtitles++;
                }
                if (!_observing) return;
                if (update.ClearOverlay) _clears++;
                if (update.HideOverlay) _hides++;
                if (update.Blocks is { } blocks)
                {
                    _blockCallbacks++;
                    foreach (var block in blocks)
                    {
                        _keys.Add(block.Key);
                        _boxes.Add($"{block.ScreenBox.X},{block.ScreenBox.Y},{block.ScreenBox.Width},{block.ScreenBox.Height}");
                        if (block.QualityIssues != TranslationQualityFlags.None) _flaggedBlocks++;
                        else _cleanBlocks++;
                    }
                }
                Write(new { type = "update", blockCount = update.Blocks?.Count,
                    blocks = update.Blocks?.Select(static b => new { b.Key, issues = b.QualityIssues.ToString(), box = new { b.ScreenBox.X, b.ScreenBox.Y, b.ScreenBox.Width, b.ScreenBox.Height } }),
                    subtitle = update.SubtitleText is { Length: > 0 }, update.SubtitleQualityWarning,
                    completedOcr = update.Diagnostics is not null, reused = update.Diagnostics?.ReusedBlocks });
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage, int providerRequests, QualityWarningLog warnings)
        {
            var recent = warnings.Recent;
            bool expected;
            lock (_gate)
            {
                expected = _dropNumber
                    ? _flaggedBlocks > 0 && _cleanBlocks == 0 && warnings.Total == 1 && _keys.Count == 1
                        && _subtitleUpdates > 0 && _flaggedSubtitles == _subtitleUpdates
                    : _flaggedBlocks == 0 && warnings.Total == 0 && _keys.Count == 1 && _flaggedSubtitles == 0;
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, screenFallback = _screenFallback,
                    ocrReadsDuringObservation = _ocrReads, blockCallbacks = _blockCallbacks, flaggedBlocks = _flaggedBlocks,
                    cleanBlocks = _cleanBlocks, subtitleUpdates = _subtitleUpdates, flaggedSubtitles = _flaggedSubtitles,
                    clearCallbacks = _clears, hideCallbacks = _hides, distinctKeys = _keys.Count, keys = _keys.Order(),
                    distinctBoxes = _boxes.Count, boxes = _boxes.Order(), warningsTotal = warnings.Total, warningsListed = recent.Count,
                    warningReason = recent.FirstOrDefault()?.Reason, providerRequests, usage.CacheHits,
                    qualityIssues = usage.QualityIssues, expectedBehavior = expected });
            }
            Console.WriteLine($"QualityMarkReplay: {reason}; bloki z flagą {_flaggedBlocks}, bez {_cleanBlocks}; klucze {_keys.Count}; " +
                              $"ostrzeżenia {warnings.Total}; zapytania {providerRequests}; oczekiwane {expected}.");
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
