using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using GameTranslatorOverlay.LiveDiag;
using Microsoft.Extensions.Logging;

internal static class Program
{
    private const string Help = """
        LiveDiag [sekundy]
        LiveDiag "fragment tytułu istniejącego okna" [sekundy]
          --profile ID|none   Jawny profil; domyślnie none, jak aplikacja bez profilu.
          --upscale 0..4      Ustawienie OCR bez profilu; 0=auto, separator: kropka.
          --output PLIK      Nowy lokalny plik JSONL; wyłącznie metryki, bez tekstów.
          --dump-frames      Zgoda na zapis wybranych klatek z podejrzeniem whiffa.
          --include-text     Zgoda na wypisywanie tekstów w konsoli (nigdy w JSONL).
          --list-windows     Tylko wypisz dostępne okna, bez capture.
          --help             Tylko pomoc, bez dostępu do pulpitu.
        Czas: 1–86400 s; domyślnie 36 s dla sceny testowej, 25 s dla istniejącego okna.
        Mock, cache w pamięci, bez ustawień i sekretów użytkownika, HTTP zablokowane.
        Nie mierzy chwili pojawienia się tekstu ani faktycznej prezentacji nakładki.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        LiveDiagOptions cli;
        try { cli = LiveDiagOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message + "\nUżyj --help."); return 2; }
        if (cli.Help) { Console.WriteLine(Help); return 0; }
        if (cli.ListWindows)
        {
            try
            {
                foreach (var window in WindowEnumerator.GetOpenWindows())
                    Console.WriteLine(window.DisplayName);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"Nie można odczytać listy okien ({ex.GetType().Name})."); return 3; }
        }

        LiveDiagReport report;
        try { report = new LiveDiagReport(cli.OutputPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Nie można utworzyć nowego pliku metryk ({ex.GetType().Name}). Istniejący plik nie jest nadpisywany.");
            return 5;
        }
        using (report)
        {
            var exitCode = Run(cli, report);
            return report.WriteFailed ? 5 : exitCode;
        }
    }

    private static int Run(LiveDiagOptions cli, LiveDiagReport report)
    {
        // This isolated path is never the application's user directory. No database is opened.
        var runId = Guid.NewGuid().ToString("N");
        var diagnosticRoot = Path.Combine(Path.GetTempPath(), "gto-livediag-" + runId);
        var paths = new AppPaths(diagnosticRoot);
        var settings = new AppSettings
        {
            Provider = MockTranslationProvider.ProviderName,
            PrivateMode = true,
            ActiveProfileId = cli.ProfileId,
            OcrUpscale = cli.Upscale,
        };
        using var loggerFactory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        LiveTranslationSession? session = null;
        Window? testWindow = null;
        Thread? testThread = null;
        var exitCode = 3;
        var reason = "setup_failed";
        var shutdownComplete = true;

        try
        {
            var ocr = new WindowsOcrProvider();
            var orchestrator = new TranslationOrchestrator(
                settings, new SqliteTranslationCache(paths.DatabasePath), new GlossaryService(),
                GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths), new UserGlossaryStore(paths),
                new MockTranslationProvider(), new DeepLTranslationProvider(http, static () => null),
                ocr, report.Usage, loggerFactory);
            orchestrator.Initialize();
            if (cli.ProfileId is not null && orchestrator.ActiveProfile is null)
            {
                reason = "unknown_profile";
                Console.Error.WriteLine("Nie znaleziono profilu. Dostępne ID: " +
                    string.Join(", ", orchestrator.Profiles.Select(static p => p.Id)) + "; none.");
            }
            else if (orchestrator.ContentWarnings.Count > 0)
            {
                reason = "profile_content_unavailable";
                Console.Error.WriteLine("Nie można załadować słownika wybranego profilu.");
            }
            else if (!ocr.IsLanguageAvailable(settings.SourceLanguage))
            {
                exitCode = 4;
                reason = "ocr_language_unavailable";
                Console.Error.WriteLine($"Brak OCR Windows dla języka {settings.SourceLanguage}.");
            }
            else
            {
                var profile = orchestrator.ActiveProfile;
                var options = new LiveSessionOptions
                {
                    Fps = profile?.ChangeDetection?.Fps ?? 6,
                    ChangeThreshold = profile?.ChangeDetection?.Threshold ?? 0.0,
                    OcrUpscale = profile?.Ocr?.Upscale ?? settings.OcrUpscale,
                    DebugFrameDumpDir = cli.DumpFrames ? Path.Combine(diagnosticRoot, "frames") : null,
                    EnableDiagnostics = true,
                };
                report.Header(new
                {
                    runId, mode = cli.AttachTitle is null ? "synthetic" : "attached-window",
                    requestedSeconds = cli.Seconds, provider = settings.Provider,
                    settings.PrivateMode, cache = "fresh-in-memory", profile = profile?.Id ?? "none",
                    settings.SourceLanguage, settings.TargetLanguage,
                    options.Fps, options.ChangeThreshold, options.OcrUpscale,
                    stabilityDelayMs = options.StabilityDelay.TotalMilliseconds,
                    forcedProcessIntervalMs = options.ForcedProcessInterval.TotalMilliseconds,
                    options.MotionThreshold, maxMotionPauseMs = options.MaxMotionPause.TotalMilliseconds,
                    options.SceneCutThreshold, options.BlockMissGrace, options.MaxWhiffRetries,
                    staticRescanIntervalMs = options.StaticRescanInterval.TotalMilliseconds,
                    frameDumpsEnabled = cli.DumpFrames, textInConsole = cli.IncludeText,
                    frameDumpDirectory = options.DebugFrameDumpDir,
                    applicationVersion = typeof(LiveTranslationSession).Assembly.GetName().Version?.ToString(),
                    stopwatchFrequency = Stopwatch.Frequency,
                });
                IntPtr hwnd;
                if (cli.AttachTitle is { } title)
                {
                    var matches = WindowEnumerator.GetOpenWindows()
                        .Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count != 1)
                    {
                        exitCode = 2;
                        reason = matches.Count == 0 ? "window_not_found" : "ambiguous_window";
                        Console.Error.WriteLine(matches.Count == 0
                            ? "Nie znaleziono okna. Użyj --list-windows."
                            : "Fragment tytułu pasuje do kilku okien. Użyj dokładniejszego tytułu i --list-windows.");
                        return report.Finish(exitCode, reason);
                    }
                    hwnd = matches[0].Handle;
                }
                else
                {
                    var ready = new TaskCompletionSource<(Window Window, IntPtr Handle)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    testThread = new Thread(() =>
                    {
                        try
                        {
                            var window = BuildTestWindow();
                            window.Show();
                            ready.TrySetResult((window, new System.Windows.Interop.WindowInteropHelper(window).Handle));
                            Dispatcher.Run();
                        }
                        catch (Exception ex) { ready.TrySetException(ex); }
                    }) { IsBackground = true };
                    testThread.SetApartmentState(ApartmentState.STA);
                    testThread.Start();
                    var result = ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    testWindow = result.Window;
                    hwnd = result.Handle;
                    Console.WriteLine("Scena testowa: 0–8s tekst, 8–24s zmiany, 24–30s ruch, potem spokój.");
                }

                Console.WriteLine($"LiveDiag: {cli.Seconds}s, Mock, profil {profile?.Id ?? "none"}, " +
                    $"FPS {options.Fps}, próg {options.ChangeThreshold}, upscale {options.OcrUpscale}.");
                if (options.DebugFrameDumpDir is { } dumpDirectory)
                    Console.WriteLine("Jawnie włączone zrzuty diagnostyczne: " + dumpDirectory);
                session = new LiveTranslationSession(orchestrator, ocr, hwnd, options,
                    update => report.Record(update, cli.IncludeText), loggerFactory.CreateLogger("LiveDiag"));
                session.Start();
                shutdownComplete = false;
                Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(cli.Seconds), stopping.Token))
                    .GetAwaiter().GetResult();
                exitCode = stopping.IsCancellationRequested ? 130 : report.SessionStopped ? 4 : 0;
                reason = stopping.IsCancellationRequested ? "cancelled" : report.SessionStopped ? "session_stopped" : "duration_elapsed";
            }
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = "runtime_error";
            Console.Error.WriteLine($"Diagnostyka nie mogła zakończyć pomiaru ({ex.GetType().Name}).");
        }
        finally
        {
            if (session is not null)
            {
                session.Stop();
                try
                {
                    session.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    shutdownComplete = true;
                }
                catch (TimeoutException) { exitCode = 6; reason = "shutdown_timeout"; }
                catch (OperationCanceledException) { shutdownComplete = true; }
                catch (Exception) { shutdownComplete = true; exitCode = 4; reason = "session_failed"; }
                // Never dispose the session's cancellation source while its loop is still running.
                if (shutdownComplete) session.Dispose();
                if (exitCode == 0 && report.SessionStopped) { exitCode = 4; reason = "session_stopped"; }
                if (exitCode == 0 && report.FrameCount == 0) { exitCode = 4; reason = "no_completed_ocr"; }
            }
            if (testWindow is not null)
            {
                try
                {
                    testWindow.Dispatcher.InvokeAsync(testWindow.Close).Task.WaitAsync(TimeSpan.FromSeconds(3))
                        .GetAwaiter().GetResult();
                    if (testThread is not null && !testThread.Join(TimeSpan.FromSeconds(3)))
                    { exitCode = 6; reason = "test_window_shutdown_timeout"; }
                }
                catch (Exception) { exitCode = 6; reason = "test_window_shutdown_failed"; }
            }
            Console.CancelKeyPress -= cancelHandler;
            // Explicitly requested frame dumps are retained in the unique run directory.
        }
        return report.Finish(exitCode, reason);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP jest wyłączone w LiveDiag.");
    }

    private static Window BuildTestWindow()
    {
        var headline = new TextBlock
        {
            Text = "Fireball deals 25% increased damage",
            FontSize = 34,
            Foreground = Brushes.White,
        };
        Canvas.SetLeft(headline, 60);
        Canvas.SetTop(headline, 80);

        var body = new TextBlock
        {
            Text = "Level 20   Strength 15   Energy Shield 120",
            FontSize = 22,
            Foreground = Brushes.Gold,
        };
        Canvas.SetLeft(body, 60);
        Canvas.SetTop(body, 170);

        // Malutkie pole tekstu (kilka komórek siatki) — test czułości na statycznej
        // scenie: krótka zmiana, której dawny próg 2% siatki w ogóle nie zauważał.
        var tiny = new TextBlock
        {
            Text = "Gold 15",
            FontSize = 18,
            Foreground = Brushes.LightGreen,
        };
        Canvas.SetLeft(tiny, 700);
        Canvas.SetTop(tiny, 80);

        // Jednolita plama nie ruszała detektora (wnętrze ma identyczną jasność klatka
        // w klatkę — zmieniały się tylko krawędzie, ~2,5% komórek). Gradient sprawia,
        // że przesuw zmienia jasność KAŻDEJ komórki pod prostokątem, jak prawdziwy
        // przesuw świata — dopiero to ćwiczy ścieżkę ruchu (próg 12% mocnych zmian).
        var mover = new Border
        {
            Width = 460,
            Height = 300,
            Background = new LinearGradientBrush(
                Color.FromRgb(250, 240, 220), Color.FromRgb(20, 10, 5), 0.0),
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetTop(mover, 280);

        var ambient = new System.Windows.Shapes.Rectangle
        {
            Width = 2000,
            Height = 1200,
            Fill = Brushes.White,
            Opacity = 0.0,
            IsHitTestVisible = false,
        };

        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1C)) };
        canvas.Children.Add(ambient);
        canvas.Children.Add(headline);
        canvas.Children.Add(body);
        canvas.Children.Add(tiny);
        canvas.Children.Add(mover);

        var window = new Window
        {
            Title = "GTO LiveDiag Test Window",
            Width = 900,
            Height = 600,
            Left = 60,
            Top = 60,
            Content = canvas,
        };

        var phrases = new[]
        {
            "Fireball deals 25% increased damage",
            "The Miller wants to talk to you",
            "Buy or Sell items at the vendor",
            "Quest complete: Clearfell Encampment",
        };
        var phraseIndex = 0;
        var sceneClock = Stopwatch.StartNew();
        var lastHeadlineChange = 0.0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        timer.Tick += (_, _) =>
        {
            var t = sceneClock.Elapsed.TotalSeconds;

            // Subtelny „ambient” jak mgła w grze — ledwo widoczne pulsowanie jasności.
            ambient.Opacity = 0.015 + 0.015 * Math.Sin(t * 2.3);

            if (t is > 8 and < 24 && t - lastHeadlineChange >= 5)
            {
                lastHeadlineChange = t;
                phraseIndex = (phraseIndex + 1) % phrases.Length;
                headline.Text = phrases[phraseIndex];
            }

            // Zmiany malutkiego pola w momentach BEZ zmiany nagłówka — samotna,
            // kilkukomórkowa zmiana musi obudzić przetwarzanie sama z siebie.
            if (t is > 11 and < 12) tiny.Text = "Gold 350";
            else if (t is > 20 and < 21) tiny.Text = "Gold 1200";

            if (t is > 24 and < 30)
            {
                mover.Visibility = Visibility.Visible;
                // 360 px/s: między próbkami (6 fps) gradient przesuwa się o ~60 px,
                // czyli delta jasności komórek ~30 — powyżej progu MOCNEJ zmiany (25).
                Canvas.SetLeft(mover, 40 + (t - 24) * 360 % 380);
            }
            else if (t >= 30 && mover.Visibility == Visibility.Visible)
            {
                mover.Visibility = Visibility.Collapsed;
            }
        };
        window.Loaded += (_, _) => timer.Start();
        window.Closed += (_, _) => { timer.Stop(); window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
        return window;
    }
}
