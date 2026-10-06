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
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using TextBlock = System.Windows.Controls.TextBlock;

internal static class Program
{
    private const string Initial = "Inspect";
    private const string Middle = "Archive note unlocks the old cabinet";
    private const string NoisyMiddle = "Archive n0t3 UnL0Cks 0ld cab1net";
    private const string Final = "Garden clue opens the next door";
    private static readonly string[] SceneWords = ["Inspect", "Archive", "Garden", "Copper", "Library", "Silver", "Harbor"];
    private static string SceneText(int scene) => scene switch
    {
        1 => Initial, 2 => Middle, 3 => Final,
        4 => "Copper key unlocks the hidden chest",
        5 => "Library map reveals the secret room",
        6 => "Silver coin activates the ancient gate",
        7 => "Harbor letter explains the final puzzle",
        _ => throw new ArgumentOutOfRangeException(nameof(scene)),
    };
    private static int ClassifyText(string text)
    {
        for (var scene = 1; scene <= SceneWords.Length; scene++)
            if (text.Contains(SceneWords[scene - 1], StringComparison.OrdinalIgnoreCase)) return scene;
        return 0;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("SceneReplay --output NOWY.jsonl [--scenario displayed|inflight|noisy|aba|churn|stop|local-reading|reading-jitter|reading-whiff|ocr-timing|local-occlusion|local-occlusion-hover|local-occlusion-inflight|moving-text|position-jitter|hud-motion|hud-motion-whiff|hud-motion-small-whiff|stale-junk|stale-junk-ghost|stale-texture|stale-newtext|stale-busy] [--ocr scripted|windows]\n" +
                "  [--provider-delay-ms 0..5000] [--phase-ms 0..200] [--ocr-delay-ms 100..600 only ocr-timing]\n" +
                "  [--assets KATALOG_Z_inspect_crop.png] [--texture OBRAZ] [--texture-origin X,Y] [--bright-spot-px 4..14] only stale-*\n" +
                "Wlasne widoczne okno; prawdziwy capture i LiveTranslationSession. Mock domyslnie 2000 ms, bez sieci.\n" +
                "Faza domyslnie 0 ms; niezerowa tylko dla displayed/inflight. Scenariusze local-reading/reading-jitter/reading-whiff odrzucaja oba parametry.\n" +
                "Domyslnie displayed oraz scripted OCR z koloru przechwyconej klatki. Bez sterowania gra.");
            return 0;
        }
        try
        {
            string? output = null;
            var scenario = "displayed";
            var ocrMode = "scripted";
            var providerDelayMs = 2000;
            var phaseMs = 0;
            var providerDelaySpecified = false;
            var phaseSpecified = false;
            var ocrDelayMs = 175;
            var ocrDelaySpecified = false;
            string? assetsDirectory = null;
            string? texturePath = null;
            (int X, int Y)? textureOrigin = null;
            var brightSpotPx = 0;
            for (var i = 0; i < args.Length; i++)
            {
                var option = args[i];
                if (++i >= args.Length) throw new ArgumentException("Brak wartosci argumentu.");
                switch (option)
                {
                    case "--output": output = args[i]; break;
                    case "--scenario": scenario = args[i]; break;
                    case "--ocr": ocrMode = args[i]; break;
                    case "--provider-delay-ms":
                        providerDelayMs = ParseMilliseconds(args[i], option, 5000);
                        providerDelaySpecified = true;
                        break;
                    case "--ocr-delay-ms":
                        ocrDelayMs = ParseMilliseconds(args[i], option, 600);
                        if (ocrDelayMs < 100) throw new ArgumentException("OCR delay requires 100..600 ms.");
                        ocrDelaySpecified = true;
                        break;
                    case "--phase-ms":
                        phaseMs = ParseMilliseconds(args[i], option, 200);
                        phaseSpecified = true;
                        break;
                    case "--assets": assetsDirectory = args[i]; break;
                    case "--texture": texturePath = args[i]; break;
                    case "--texture-origin": textureOrigin = ParseOrigin(args[i]); break;
                    case "--bright-spot-px":
                        brightSpotPx = ParseMilliseconds(args[i], option, 14);
                        if (brightSpotPx < 4) throw new ArgumentException("--bright-spot-px wymaga liczby calkowitej od 4 do 14.");
                        break;
                    default: throw new ArgumentException("Nieznany argument.");
                }
            }
            if (output is null) throw new ArgumentException("Wymagany nowy plik --output.");
            var staleScenario = scenario is "stale-junk" or "stale-junk-ghost" or "stale-texture" or "stale-newtext" or "stale-busy";
            if (!staleScenario && (assetsDirectory is not null || texturePath is not null || textureOrigin is not null || brightSpotPx > 0))
                throw new ArgumentException("--assets, --texture, --texture-origin i --bright-spot-px sa dostepne tylko dla stale-*.");
            if (staleScenario)
            {
                if (phaseSpecified || ocrDelaySpecified)
                    throw new ArgumentException("stale-* nie obsluguje --phase-ms ani --ocr-delay-ms.");
                var requiredOcr = scenario is "stale-junk" or "stale-junk-ghost" ? "scripted" : "windows";
                if (args.Contains("--ocr") && ocrMode != requiredOcr)
                    throw new ArgumentException($"{scenario} requires {requiredOcr} OCR.");
                return StaleLabelReplay.Run(output, scenario, assetsDirectory, texturePath, textureOrigin,
                    providerDelaySpecified ? providerDelayMs : 1000, brightSpotPx);
            }
            if (scenario is "hud-motion" or "hud-motion-whiff" or "hud-motion-small-whiff")
            {
                if (providerDelaySpecified || phaseSpecified || ocrDelaySpecified || (args.Contains("--ocr") && ocrMode != "windows"))
                    throw new ArgumentException("HUD motion uses Windows OCR and Mock 200; optional timing overrides are not supported.");
                return StaticHudReplay.Run(output, scenario != "hud-motion", scenario == "hud-motion-small-whiff");
            }
            if (scenario == "ocr-timing")
            {
                if (providerDelaySpecified || phaseSpecified || (args.Contains("--ocr") && ocrMode != "scripted"))
                    throw new ArgumentException("OCR timing uses scripted OCR and Mock 0; only --ocr-delay-ms is supported.");
                return OcrTimingReplay.Run(output, ocrDelayMs);
            }
            if (ocrDelaySpecified) throw new ArgumentException("--ocr-delay-ms requires --scenario ocr-timing.");
            if (scenario is ("local-occlusion" or "local-occlusion-hover" or "local-occlusion-inflight" or "moving-text" or "position-jitter"))
            {
                if (providerDelaySpecified || phaseSpecified)
                    throw new ArgumentException("This scenario has fixed provider delay and phase.");
                var isLocalOcclusion = scenario.StartsWith("local-occlusion", StringComparison.Ordinal);
                var requiredOcr = isLocalOcclusion ? "windows" : "scripted";
                if (args.Contains("--ocr") && ocrMode != requiredOcr)
                    throw new ArgumentException($"{scenario} requires {requiredOcr} OCR.");
                return isLocalOcclusion ? LocalOcclusionReplay.Run(output, scenario) : MovingTextReplay.Run(output, scenario);
            }
            if (scenario is not ("displayed" or "inflight" or "noisy" or "aba" or "churn" or "stop" or "local-reading" or "reading-jitter" or "reading-whiff") || ocrMode is not ("scripted" or "windows"))
                throw new ArgumentException("Wymagany nowy plik --output; sprawdz --help.");
            if (scenario is ("local-reading" or "reading-jitter" or "reading-whiff"))
            {
                if (providerDelaySpecified || phaseSpecified)
                    throw new ArgumentException($"{scenario} nie obsluguje --provider-delay-ms ani --phase-ms, nawet z wartoscia domyslna.");
                var requiredOcr = scenario == "local-reading" ? "windows" : "scripted";
                if (args.Contains("--ocr") && ocrMode != requiredOcr)
                    throw new ArgumentException($"{scenario} requires {requiredOcr} OCR.");
                return LocalReadingReplay.Run(output, scenario);
            }
            if (scenario == "stop" && (providerDelayMs != 2000 || phaseMs != 0))
                throw new ArgumentException("Stop wymaga --provider-delay-ms 2000 i --phase-ms 0: sprawdza dwie trwajace rezerwacje.");
            if (phaseMs != 0 && scenario is not ("displayed" or "inflight"))
                throw new ArgumentException("Niezerowe --phase-ms jest dostepne tylko dla displayed oraz inflight.");
            if (scenario is ("noisy" or "aba" or "churn" or "stop") && ocrMode != "scripted")
                throw new ArgumentException("Noisy, aba, churn and stop require scripted OCR.");
            using var report = new Report(output, scenario, ocrMode, providerDelayMs, phaseMs);
            return RunAsync(report, scenario, ocrMode, providerDelayMs, phaseMs).GetAwaiter().GetResult();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"SceneReplay: {ex.Message} Uzyj --help.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SceneReplay: {ex.GetType().Name}; plik istniejacy nie jest nadpisywany.");
            return 2;
        }
    }

    private static int ParseMilliseconds(string value, string option, int maximum)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed > maximum)
            throw new ArgumentException($"{option} wymaga liczby calkowitej od 0 do {maximum}.");
        return parsed;
    }

    private static (int X, int Y) ParseOrigin(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y))
            throw new ArgumentException("--texture-origin wymaga X,Y w pikselach (liczby calkowite >= 0).");
        return (x, y);
    }

    private static async Task<int> RunAsync(Report report, string scenario, string ocrMode, int providerDelayMs, int phaseMs)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-scenereplay-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = null, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var ocr = new ObservedOcr(ocrMode == "windows" ? new WindowsOcrProvider() : new ScriptedOcr(scenario == "noisy"), report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromMilliseconds(providerDelayMs) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        orchestrator.Initialize();
        if (!ocr.IsLanguageAvailable(settings.SourceLanguage)) throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
        SceneWindow? sceneWindow = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            sceneWindow = await SceneWindow.CreateAsync(expandedScenes: scenario == "churn");
            await sceneWindow.ChangeAsync(1, report);
            session = new LiveTranslationSession(orchestrator, ocr, sceneWindow.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true }, report.Update, logging.CreateLogger("SceneReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (phaseMs > 0) await Task.Delay(phaseMs);
            await sceneWindow.ChangeAsync(2, report);
            if (scenario == "inflight")
            {
                await report.MiddleOcr.Task.WaitAsync(TimeSpan.FromSeconds(15));
                // OCR just returned to the real session; Mock uses the configured delay.
                // With a delay below 250 ms, this scenario may no longer interrupt a pending request.
                await Task.Delay(250);
                await sceneWindow.ChangeAsync(3, report, isFinal: true);
                await report.FinalDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            else if (scenario == "stop")
            {
                await report.MiddleOcr.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                await sceneWindow.ChangeAsync(3, report);
                await report.GardenOcr.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(100);
                if (!report.ValidateStopPrecondition(usage))
                    throw new InvalidOperationException("Stop requires both Mock requests still pending.");
                // The common finally requests Stop immediately and awaits the real session Completion.
            }
            else if (scenario == "aba")
            {
                await report.MiddleOcr.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(650);
                await sceneWindow.ChangeAsync(3, report);
                await Task.Delay(650);
                await sceneWindow.ChangeAsync(2, report, isFinal: true);
                await report.FinalDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            else if (scenario == "churn")
            {
                await report.MiddleOcr.Task.WaitAsync(TimeSpan.FromSeconds(15));
                for (var scene = 3; scene <= 7; scene++)
                {
                    await Task.Delay(650);
                    await sceneWindow.ChangeAsync(scene, report, isFinal: scene == 7);
                }
                await report.FinalDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            else if (scenario == "noisy")
            {
                await report.MiddleOcr.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(8000); // Both builds get the same observation window; the baseline may never replace Inspect.
            }
            else
            {
                await report.MiddleDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            if (scenario is not ("noisy" or "stop")) await Task.Delay(3000); // Observe whether a cleared old block returns on later updates.
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
                if (scenario == "stop") report.BeginStop(usage);
                session.Stop();
                try
                {
                    await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                    if (scenario == "stop") report.StopCompleted(usage);
                    session.Dispose();
                }
                catch (OperationCanceledException)
                {
                    if (scenario == "stop") report.StopCompleted(usage);
                    session.Dispose();
                }
                catch (Exception) { exitCode = 4; reason = "session_shutdown_failed"; }
                if (scenario == "stop") await Task.Delay(100); // Also observe callbacks after Completion.
            }
            if (sceneWindow is not null)
            {
                try { await sceneWindow.CloseAsync(); }
                catch (Exception) { exitCode = 4; reason = "window_shutdown_failed"; }
            }
        }
        if (exitCode == 0 && scenario == "stop" && !report.HasCleanStop(usage))
        { exitCode = 4; reason = "stop_invariants_failed"; }
        if (report.ScreenFallback) { exitCode = 4; reason = "screen_fallback_observed"; }
        report.Finish(exitCode, reason, usage);
        return exitCode;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in SceneReplay.");
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
            var containsMiddle = result.Lines.Any(line => ClassifyText(line.Text) == 2);
            report.ObserveOcr(result.Lines.Select(line => ClassifyText(line.Text)).Where(scene => scene > 0).Distinct().ToArray(),
                result.Lines.Count, bitmap.Width, bitmap.Height);
            if (containsMiddle) report.MiddleOcr.TrySetResult();
            return result;
        }
    }

    // Scripted OCR is deliberate: real captured pixels determine the scene, not mutable UI state.
    // Geometry/text are synthetic. This mode tests session lifetime, not Windows OCR accuracy.
    private sealed class ScriptedOcr(bool noisyMiddle) : IOcrProvider
    {
        public string Name => "Scripted OCR from captured background";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = new List<byte>();
            for (var y = 1; y < 8; y++)
                for (var x = 1; x < 8; x++)
                    samples.Add(bitmap.PixelsBgra32[(bitmap.Height * y / 8) * bitmap.Stride + (bitmap.Width * x / 8) * 4]);
            samples.Sort();
            var background = samples[samples.Count / 2];
            var scene = Math.Clamp((int)Math.Round((background - 20) / 18.0) + 1, 1, 7);
            var text = noisyMiddle && scene == 2 ? NoisyMiddle : SceneText(scene);
            var box = new RectPx(bitmap.Width / 12, bitmap.Height / 3, bitmap.Width * 5 / 6, bitmap.Height / 10);
            return Task.FromResult(new OcrResult([new OcrLine(text, box, [new OcrWord(text, box)])], languageTag));
        }
    }

    private sealed class SceneWindow(Window window, TextBlock text, Thread thread)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;
        public static async Task<SceneWindow> CreateAsync(bool expandedScenes)
        {
            var ready = new TaskCompletionSource<SceneWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var text = new TextBlock { FontSize = 34, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(60, 160, 40, 40) };
                    var content = new Grid();
                    // A permanent reference stripe prevents the capture heuristic from treating
                    // sparse text on a uniform background as a failed PrintWindow image.
                    content.Children.Add(new Border { Width = 50, HorizontalAlignment = HorizontalAlignment.Right, Background = expandedScenes ? Brushes.White : Brushes.Gray });
                    content.Children.Add(text);
                    var window = new Window
                    {
                        Title = "GTO SceneReplay - local test", Width = 940, Height = 560, Left = 40, Top = 40,
                        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, Content = content,
                    };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new SceneWindow(window, text, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public async Task ChangeAsync(int scene, Report report, bool isFinal = false)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                var gray = checked((byte)(20 + 18 * (scene - 1)));
                window.Background = new SolidColorBrush(Color.FromRgb(gray, gray, gray));
                text.Foreground = Brushes.White;
                text.Text = SceneText(scene);
                report.Event(new { type = "scene_mutated", scene });
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    report.Change(scene, isFinal);
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
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly string _scenario;
        private bool _oldVisible;
        private bool _oldRemoved;
        private double? _changeAt, _finalChangeAt, _oldRemovedAt, _middleReadyAt, _finalReadyAt;
        private int _oldReturns, _staleMiddleUpdates, _updates, _completedOcr, _oldCallbacksAfterChange, _reusedAfterChange;
        private int _currentScene, _finalTargetScene, _staleSceneCallbacks, _obsoleteCallbacksAfterFinal;
        private double? _firstMiddleOcrAt, _initialDisplayedAt, _stopRequestedAt, _shutdownMs;
        private long? _reservedBeforeStop, _reservedAfterCompletion;
        private int _postStopUpdateCallbacks;
        private bool _stopPreconditionMet;
        private readonly Dictionary<int, int> _ocrScenes = [];
        public bool ScreenFallback { get; private set; }
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MiddleOcr { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GardenOcr { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MiddleDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Report(string path, string scenario, string ocrMode, int providerDelayMs, int phaseMs)
        {
            _scenario = scenario;
            if (scenario == "noisy" && (!JunkFilter.IsMeaningful(NoisyMiddle)
                || ReadingQuality.Score(TextNormalizer.Normalize(NoisyMiddle)) >= 0.9
                || TextSimilarity.Ratio(NoisyMiddle, Initial) >= 0.5))
                throw new InvalidOperationException("Noisy OCR fixture no longer exercises low-quality overlap reuse.");
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new
            {
                type = "header", scenario, ocrMode, provider = "Mock", providerDelayMs, phaseMs, cache = "fresh-memory", privateMode = true,
                phaseMeaning = "Requested wait after InitialDisplayed before the first scene 2 change; actual interval includes dispatch/render scheduling",
                capture = "own-window-only", actualOverlayWindow = false, backgroundGrays = Enumerable.Range(1, 7).Select(scene => 20 + 18 * (scene - 1)).ToArray(),
                sceneIntervalMs = scenario is ("aba" or "churn") ? 650 : (int?)null,
                concurrencyMeaning = "This fixture records completed Mock requests, not active concurrency; hard cap is tested by BoundedTranslationWorkTests",
                timingMeaning = "From WPF Rendering event to session callback; not physical display latency",
                noisyMiddle = scenario == "noisy", noisyFixtureMeaningful = JunkFilter.IsMeaningful(NoisyMiddle),
                noisyFixtureQuality = ReadingQuality.Score(TextNormalizer.Normalize(NoisyMiddle)), initialFixtureQuality = ReadingQuality.Score(Initial),
                noisyFixtureSimilarity = TextSimilarity.Ratio(NoisyMiddle, Initial),
                scriptedOcrMeaning = "When scripted, scene selected from captured background; text and boxes synthetic",
            });
        }
        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));
        public void Change(int scene, bool isFinal)
        {
            lock (_gate)
            {
                _currentScene = scene;
                if (scene == 2) _changeAt ??= _clock.Elapsed.TotalMilliseconds;
                if (isFinal)
                {
                    _finalChangeAt = _clock.Elapsed.TotalMilliseconds;
                    _finalTargetScene = scene;
                }
                Write(new { type = "scene_rendering", scene, isFinal });
            }
        }
        public void ObserveOcr(int[] scenes, int lineCount, int width, int height)
        {
            lock (_gate)
            {
                foreach (var scene in scenes) _ocrScenes[scene] = _ocrScenes.GetValueOrDefault(scene) + 1;
                if (scenes.Contains(2)) _firstMiddleOcrAt ??= _clock.Elapsed.TotalMilliseconds;
                if (scenes.Contains(3)) GardenOcr.TrySetResult();
                Write(new { type = "ocr_completed", scenes, lineCount, width, height, containsMiddle = scenes.Contains(2) });
            }
        }
        // Reservation getters stay in stop-only methods so older baseline assemblies
        // can still run the other scenarios when only this tool's binary is replaced.
        public bool ValidateStopPrecondition(UsageTracker usage)
        {
            lock (_gate)
            {
                var expectedReserved = Middle.Length + Final.Length;
                var reserved = usage.ReservedApiCharacters;
                var completed = usage.ApiRequests;
                _stopPreconditionMet = reserved == expectedReserved && completed == 1;
                Write(new { type = "stop_precondition", expectedReservedCharacters = expectedReserved,
                    reservedCharacters = reserved, completedMockRequests = completed, passed = _stopPreconditionMet });
                return _stopPreconditionMet;
            }
        }
        public void BeginStop(UsageTracker usage)
        {
            lock (_gate)
            {
                _reservedBeforeStop = usage.ReservedApiCharacters;
                _stopRequestedAt = _clock.Elapsed.TotalMilliseconds;
                Write(new { type = "stop_requested", reservedCharacters = _reservedBeforeStop });
            }
        }
        public void StopCompleted(UsageTracker usage)
        {
            lock (_gate)
            {
                _shutdownMs = _clock.Elapsed.TotalMilliseconds - _stopRequestedAt;
                _reservedAfterCompletion = usage.ReservedApiCharacters;
                Write(new { type = "stop_completed", shutdownMs = _shutdownMs,
                    reservedCharacters = _reservedAfterCompletion, completedMockRequests = usage.ApiRequests });
            }
        }
        public bool HasCleanStop(UsageTracker usage)
        {
            lock (_gate) return _stopPreconditionMet && _shutdownMs is not null
                && _reservedAfterCompletion == 0 && usage.ApiRequests == 1 && _postStopUpdateCallbacks == 0;
        }
        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                _updates++;
                if (_stopRequestedAt is not null) _postStopUpdateCallbacks++;
                if (update.Stopped)
                {
                    Write(new { type = "session_stopped", update.ClearOverlay, update.HideOverlay });
                    return; // Shutdown must not count as successful removal of an obsolete block.
                }
                var old = update.Blocks?.Any(b => ClassifyText(b.TranslatedText) == 1) ?? false;
                var middle = update.Blocks?.Any(b => ClassifyText(b.TranslatedText) == 2) ?? false;
                var final = update.Blocks?.Any(b => ClassifyText(b.TranslatedText) == 3) ?? false;
                var publishedScenes = update.Blocks?.Select(b => ClassifyText(b.TranslatedText)).Where(scene => scene > 0).Distinct().ToArray() ?? [];
                var containsObsoleteScene = publishedScenes.Any(scene => scene != _currentScene);
                if (_changeAt is not null && containsObsoleteScene) _staleSceneCallbacks++;
                if (_finalChangeAt is not null && publishedScenes.Any(scene => scene != _finalTargetScene)) _obsoleteCallbacksAfterFinal++;
                if (update.ClearOverlay || update.HideOverlay || update.Stopped) _oldVisible = false;
                // MainWindow applies Blocks after Hide/Clear; a block list can show the window again.
                if (update.Blocks is not null && !update.Stopped) _oldVisible = old;
                if (_changeAt is not null && !_oldVisible && !_oldRemoved) { _oldRemovedAt = now; _oldRemoved = true; }
                if (_oldRemoved && old) _oldReturns++;
                if (_changeAt is not null && old) _oldCallbacksAfterChange++;
                if (_changeAt is not null) _reusedAfterChange += update.Diagnostics?.ReusedBlocks ?? 0;
                if (old && _changeAt is null)
                {
                    _initialDisplayedAt ??= now;
                    InitialDisplayed.TrySetResult();
                }
                if (middle && _changeAt is not null)
                {
                    _middleReadyAt ??= now;
                    MiddleDisplayed.TrySetResult();
                    if (_scenario == "inflight" && _finalChangeAt is not null) _staleMiddleUpdates++;
                }
                if (_finalChangeAt is not null && publishedScenes.Contains(_finalTargetScene))
                {
                    _finalReadyAt ??= now;
                    FinalDisplayed.TrySetResult();
                }
                if (update.Diagnostics is { } d) { _completedOcr++; ScreenFallback |= d.UsedScreenFallback; }
                Write(new
                {
                    type = "update", blocksProvided = update.Blocks is not null, blockCount = update.Blocks?.Count,
                    update.ClearOverlay, update.HideOverlay, update.Stopped, oldVisible = _oldVisible,
                    containsOld = old, containsMiddle = middle, containsFinal = final, publishedScenes, currentScene = _currentScene, containsObsoleteScene,
                    completedOcr = update.Diagnostics is not null, sceneCut = update.Diagnostics?.SceneCut,
                    retainedBlocks = update.Diagnostics?.RetainedBlocks, reusedBlocks = update.Diagnostics?.ReusedBlocks,
                    captureToUpdateMs = update.Diagnostics?.CaptureToUpdateMs,
                    captureMs = update.Diagnostics?.CaptureMs, ocrMs = update.Diagnostics?.OcrMs,
                    translateMs = update.Diagnostics?.TranslateMs,
                });
            }
        }
        public void Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                Write(new
                {
                    type = "summary", exitCode, reason, callbacks = _updates, completedOcr = _completedOcr, screenFallback = ScreenFallback,
                    oldRemovedMs = _oldRemovedAt - _changeAt, middleReadyMs = _middleReadyAt - _changeAt,
                    initialDisplayedToScene2Ms = _changeAt - _initialDisplayedAt,
                    middleFirstOcrCompletedMs = _firstMiddleOcrAt - _changeAt,
                    finalReadyMs = _finalReadyAt - _finalChangeAt, oldReturnCallbacks = _oldReturns,
                    oldCallbacksAfterSceneChange = _oldCallbacksAfterChange, reusedBlocksAfterSceneChange = _reusedAfterChange,
                    oldPresentAtEnd = _oldVisible, staleSceneCallbacks = _staleSceneCallbacks,
                    shutdownMs = _shutdownMs, postStopUpdateCallbacks = _postStopUpdateCallbacks,
                    reservedCharactersBeforeStop = _reservedBeforeStop, reservedApiCharactersAfterCompletion = _reservedAfterCompletion,
                    stopPreconditionMet = _scenario == "stop" ? _stopPreconditionMet : (bool?)null,
                    obsoleteCallbacksAfterFinalScene = _obsoleteCallbacksAfterFinal, finalTargetScene = _finalTargetScene,
                    finalFreshPublished = _finalReadyAt is not null, ocrScenes = _ocrScenes,
                    abaReturnAfterFirstOcrMs = _scenario == "aba" ? _finalChangeAt - _firstMiddleOcrAt : null,
                    abaMockRequestsWithinDistinctTexts = _scenario == "aba" ? usage.ApiRequests <= 3 : (bool?)null,
                    churnMockRequestsWithinDistinctTexts = _scenario == "churn" ? usage.ApiRequests <= 7 : (bool?)null,
                    staleMiddleCallbacksAfterFinalScene = _staleMiddleUpdates,
                    oldClearedBeforeNew = _oldRemovedAt is { } removed && _middleReadyAt is { } middle && removed < middle,
                    inflightStaleResultRejected = _scenario == "inflight" ? _finalReadyAt is not null && _staleMiddleUpdates == 0 : (bool?)null,
                    mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters, usage.CacheHits, usage.GlossaryHits,
                });
            }
            Console.WriteLine($"SceneReplay: {reason}; callbacki {_updates}, OCR {_completedOcr}, stary usuniety {_oldRemovedAt - _changeAt:F0} ms.");
        }
        public void Dispose() => _writer.Dispose();
    }
}