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

internal static class BlackFrameReplay
{
    private const string Before = "Inspect this gate";
    private const string After = "Garden clue opens the next door";
    private const int BlackPhaseMs = 2000;

    public static int Run(string output)
    {
        using var report = new Report(output);
        return RunAsync(report).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-blackframe-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetwork());
        var ocr = new WindowsOcrProvider();
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
            window = await TestWindow.CreateAsync();
            await window.RenderAsync();
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { RequireGameVisible = false, OcrUpscale = 1, EnableDiagnostics = true },
                report.Update, logging.CreateLogger("BlackFrameReplay"));
            session.Start();
            await report.BeforeShown.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await window.ShowBlackAsync(report);
            await Task.Delay(BlackPhaseMs);
            await window.ShowAfterAsync(report);
            await report.AfterShown.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
            throw new InvalidOperationException("HTTP disabled in BlackFrameReplay.");
    }

    private sealed class TestWindow(Window window, Canvas world, Border panel, TextBlock label, Thread thread)
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
                    var label = new TextBlock
                    {
                        Text = Before, FontFamily = new FontFamily("Segoe UI"), FontSize = 30,
                        Foreground = Brushes.White, Margin = new Thickness(30, 120, 0, 0),
                    };
                    var panel = new Border { Width = 520, Height = 340, Background = new SolidColorBrush(Color.FromRgb(70, 70, 70)), Child = new Canvas { Children = { label } } };
                    Canvas.SetLeft(panel, 80);
                    Canvas.SetTop(panel, 100);
                    var world = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    world.Children.Add(panel);
                    var window = new Window
                    {
                        Title = "GTO BlackFrameReplay - local test", Width = 1200, Height = 760,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = world,
                    };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, world, panel, label, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task ShowBlackAsync(Report report) => RenderAsync(() =>
        {
            panel.Visibility = Visibility.Collapsed;
            world.Background = Brushes.Black;
        }, () => report.Phase(1));

        public Task ShowAfterAsync(Report report) => RenderAsync(() =>
        {
            label.Text = After;
            panel.Visibility = Visibility.Visible;
            world.Background = new SolidColorBrush(Color.FromRgb(20, 20, 20));
        }, () => report.Phase(2));

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
        private int _phase, _updates, _screenFallbackFrames, _readingScreenUpdates, _warningTexts, _blackPhaseUpdates;
        private bool _ended, _stopped;
        public TaskCompletionSource BeforeShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AfterShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Report(string output)
        {
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Write(new { type = "configuration", scenario = "black-frame", ocr = "windows", provider = "Mock", providerDelayMs = 200,
                privateCache = true, httpBlocked = true, blackPhaseMs = BlackPhaseMs,
                scope = "Owned window; text screen, about 2 s of a uniform black frame, another text screen" });
        }

        private void Write(object data) { _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data })); _writer.Flush(); }
        public void Event(object value) { lock (_gate) Write(value); }
        public void Phase(int phase) { lock (_gate) { _phase = phase; Write(new { type = "phase", phase }); } }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                if (_ended) return;
                if (update.Stopped) { _stopped = true; return; }
                _updates++;
                var usedScreen = update.Diagnostics?.UsedScreenFallback ?? false;
                if (usedScreen) _screenFallbackFrames++;
                if (update.ReadingScreen) _readingScreenUpdates++;
                if (update.StatusLine.Contains("przechwytywania ekranu", StringComparison.Ordinal)
                    || update.Notice?.Text == NoticeTexts.ReadingScreen || update.Notice?.Text == NoticeTexts.ExclusiveFullscreen)
                    _warningTexts++;
                if (_phase == 1) _blackPhaseUpdates++;
                if (update.Blocks is { } blocks)
                {
                    if (_phase == 0 && blocks.Any(static b => b.TranslatedText.Contains(Before))) BeforeShown.TrySetResult();
                    if (_phase == 2 && blocks.Any(static b => b.TranslatedText.Contains(After))) AfterShown.TrySetResult();
                }
                Write(new { type = "update", phase = _phase, usedScreen, update.ReadingScreen, hasBlocks = update.Blocks is not null,
                    blockCount = update.Blocks?.Count, update.ClearOverlay, update.HideOverlay });
            }
        }

        public bool Valid => BeforeShown.Task.IsCompletedSuccessfully && AfterShown.Task.IsCompletedSuccessfully && !_stopped;

        public void End() { lock (_gate) _ended = true; }

        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var warned = _readingScreenUpdates > 0 || _warningTexts > 0;
                var desired = Valid && _screenFallbackFrames == 0 && !warned;
                Write(new { type = "summary", exitCode, reason, fixtureValid = Valid, expectedBehavior = desired,
                    updates = _updates, screenFallbackFrames = _screenFallbackFrames, screenReadingWarning = warned,
                    readingScreenUpdates = _readingScreenUpdates, warningTexts = _warningTexts, blackPhaseUpdates = _blackPhaseUpdates,
                    mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters });
                Console.WriteLine($"BlackFrameReplay: {reason}; valid={Valid}; expected={desired}; screenFallbackFrames={_screenFallbackFrames}; warning={warned}.");
            }
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
