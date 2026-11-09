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

internal static class ProviderFailureReplay
{
    private const string Source = "Unlock level 20 door";
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(8);

    public static int Run(string output)
    {
        using var report = new Report(output);
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-providerfailure-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = RejectingProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Error));
        using var http = new HttpClient(new NoNetwork());
        var ocr = new ScriptedOcr(report);
        var usage = new UsageTracker();
        var provider = new RejectingProvider();
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
                new LiveSessionOptions { RequireGameVisible = false, OcrUpscale = 1, EnableDiagnostics = true, StaticRescanInterval = TimeSpan.FromMilliseconds(500) },
                report.Update, logging.CreateLogger("ProviderFailureReplay"));
            session.Start();
            var waited = Stopwatch.StartNew();
            while (provider.Requests == 0)
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
                await Task.Delay(20);
            }
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
        return report.Finish(exitCode, reason, usage, provider.Requests, orchestrator.ProviderPause);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in ProviderFailureReplay.");
    }

    private sealed class RejectingProvider : ITranslationProvider
    {
        public const string ProviderName = "Rejecting";
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string Name => ProviderName;
        public bool RequiresApiKey => true;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            throw new TranslationException(TranslationFailureKind.InvalidApiKey, "Klucz odrzucony przez atrapę.");
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(false, "ProviderFailureReplay"));
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
                throw new InvalidOperationException("ProviderFailureReplay could not locate its white label glyphs.");
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
                    var window = new Window { Title = "GTO ProviderFailureReplay - local test", Width = 640, Height = 400,
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
        private bool _observing, _ended, _screenFallback, _sessionStopped;
        private int _ocrReads, _blockCallbacks, _translatedBlocks, _noticeUpdates;

        public bool FixtureValid
        {
            get { lock (_gate) return !_screenFallback && !_sessionStopped && _ocrReads >= 4; }
        }

        public Report(string output)
        {
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario = "provider-failure", ocrMode = "scripted",
                provider = "Rejecting (InvalidApiKey on every call)", privateMode = true, cache = "fresh-memory",
                network = "blocked", observationMs = Observation.TotalMilliseconds, staticRescanIntervalMs = 500 });
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
                if (_ended || !_observing) return;
                if (update.Blocks is { } blocks)
                {
                    _blockCallbacks++;
                    _translatedBlocks += blocks.Count;
                }
                if (update.Notice is not null) _noticeUpdates++;
                Write(new { type = "update", blockCount = update.Blocks?.Count, notice = update.Notice is not null,
                    completedOcr = update.Diagnostics is not null });
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage, int providerRequests, ProviderPauseStatus pause)
        {
            bool expected;
            lock (_gate)
            {
                expected = providerRequests == 1 && _translatedBlocks == 0 && pause.Kind == ProviderPauseKind.Permanent
                    && pause.Failure == TranslationFailureKind.InvalidApiKey && usage.FailedRequests == 1;
                Write(new { type = "summary", exitCode, reason, fixtureValid = FixtureValid, screenFallback = _screenFallback,
                    ocrReadsDuringObservation = _ocrReads, blockCallbacks = _blockCallbacks, translatedBlocks = _translatedBlocks,
                    noticeUpdates = _noticeUpdates, providerRequests, failedRequests = usage.FailedRequests,
                    pauseKind = pause.Kind.ToString(), pauseFailure = pause.Failure.ToString(), expectedBehavior = expected });
            }
            Console.WriteLine($"ProviderFailureReplay: {reason}; odczyty OCR {_ocrReads}; zapytania do dostawcy {providerRequests}; " +
                              $"wstrzymanie {pause.Kind}; oczekiwane {expected}.");
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
