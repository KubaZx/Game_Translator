using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Vision;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using TextBlock = System.Windows.Controls.TextBlock;

internal static class UiFontsReplay
{
    public static readonly string[] Paths = ["sync", "background"];

    private const string Profile = "escape-academy";
    private static readonly TimeSpan FirstUpdate = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan UpdateStep = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PrepareWait = TimeSpan.FromMilliseconds(600);

    private static readonly (string Source, string Translated)[] Texts =
    [
        ("Open the cellar door", "Otwórz drzwi do piwnicy"),
        ("Find the brass key", "Znajdź mosiężny klucz"),
        ("Talk to the lighthouse keeper", "Porozmawiaj z latarnikiem"),
        ("Light the signal fire", "Rozpal ognisko sygnałowe"),
        ("Climb the spiral stairs", "Wejdź po krętych schodach"),
        ("Read the old logbook", "Przeczytaj stary dziennik"),
    ];

    private sealed record Sample(int Index, double SessionPrepareMs, bool Prepared, double UiVoteMs, double UiLayoutMs, double UiTotalMs,
        int Weight, double EmSize, string Family);

    public static int Run(string output, string path)
    {
        using var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        return RunAsync(writer, path).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(StreamWriter writer, string path)
    {
        var clock = Stopwatch.StartNew();
        void Write(object data) => writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = clock.Elapsed.TotalMilliseconds, data }));
        Write(new
        {
            type = "header", scenario = "ui-fonts", fontPath = path, profile = Profile, placement = "cover",
            firstUpdateMs = FirstUpdate.TotalMilliseconds, updateStepMs = UpdateStep.TotalMilliseconds, updates = Texts.Length,
            prepareWaitMs = PrepareWait.TotalMilliseconds,
            meaning = "sync = OverlayFonts.WarmUpProfile and the weight measurement on the UI thread (as before); "
                + "background = WarmUpProfileInBackground and PrepareNativeWeights on the session thread, then the same UI layout",
            probeMeaning = "Latency of a Send-priority dispatcher call posted every ~0.5 ms from another thread; the maximum approximates the longest UI-thread stall",
        });

        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-uifonts-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings
        {
            Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = Profile,
            OverlayPlacement = "cover", LiveDisplayMode = "at-source", OverlayFontFamily = AppSettings.AutoFontFamily,
        };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetwork());
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider(), new DeepLTranslationProvider(http, static () => null),
            new NoOcr(), new UsageTracker(), logging);

        var covers = await Task.Run(() => Texts.Select(static t => BuildCover(t.Source)).ToArray());
        if (covers.Any(static c => c is null))
        {
            Write(new { type = "error", errorType = "cover_not_built" });
            return 4;
        }

        var (dispatcher, thread) = await StartUiThreadAsync();
        var probe = new Probe(dispatcher, clock);
        var exitCode = 0;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                orchestrator.Initialize();
                OverlayFonts.ProfileFont = orchestrator.ActiveProfile?.Overlay?.FontFamily;
                OverlayFonts.ProfileRules = orchestrator.ActiveFontRules;
            });
            if (OverlayFonts.ProfileFont is null) throw new InvalidOperationException("Profil bez kroju.");
            await Task.Delay(300);
            probe.Start();
            await Task.Delay(200);

            var t0 = clock.Elapsed;
            Task? background = null;
            var startCallMs = await dispatcher.InvokeAsync(() =>
            {
                var watch = Stopwatch.StartNew();
                if (path == "sync") OverlayFonts.WarmUpProfile(settings);
                else background = OverlayFonts.WarmUpProfileInBackground(settings);
                return watch.Elapsed.TotalMilliseconds;
            });
            double? warmUpDoneMs = path == "sync" ? startCallMs : null;
            var warmUpTracker = background?.ContinueWith(_ => warmUpDoneMs = (clock.Elapsed - t0).TotalMilliseconds, TaskScheduler.Default);
            Write(new { type = "live_start", startCallMs });

            var samples = new List<Sample>();
            for (var i = 0; i < Texts.Length; i++)
            {
                var due = t0 + FirstUpdate + UpdateStep * i - clock.Elapsed;
                if (due > TimeSpan.Zero) await Task.Delay(due);
                var cover = covers[i]!;
                var box = new RectPx(200, 300 + 70 * i, 520, 64);
                var block = new LiveDisplayBlock($"k{i}#0", box, Texts[i].Translated, 40, cover.TextRgb, -1, cover.OutlineRgb) { Cover = cover };
                var prepared = false;
                var sessionWatch = Stopwatch.StartNew();
                if (path == "background")
                    prepared = await Task.Run(() => OverlayBlockRenderer.PrepareNativeWeights([block], settings, OverlayFonts.ProfileFont, PrepareWait));
                var sessionMs = path == "background" ? sessionWatch.Elapsed.TotalMilliseconds : 0;
                var sample = await dispatcher.InvokeAsync(() =>
                {
                    var monitor = Displays.FromRect(box);
                    var total = Stopwatch.StartNew();
                    OverlayBlockRenderer.VoteNativeWeight(cover, block.TranslatedText, settings, OverlayFonts.ProfileFont, monitor);
                    var voteMs = total.Elapsed.TotalMilliseconds;
                    var element = OverlayBlockRenderer.CreateNativeElement(block.TranslatedText, cover, monitor.Scale);
                    OverlayBlockRenderer.LayoutNativeElement(element, box, monitor, settings, OverlayFonts.ProfileFont);
                    var totalMs = total.Elapsed.TotalMilliseconds;
                    var native = (GameTextElement)element.Child;
                    return new Sample(i, sessionMs, prepared, voteMs, totalMs - voteMs, totalMs,
                        native.Typeface.Weight.ToOpenTypeWeight(), Math.Round(native.EmSize, 3),
                        OverlayFonts.CoverFamilyName(settings, OverlayFonts.ProfileFont, cover, monitor.Bounds.Height));
                });
                samples.Add(sample);
                Write(new { type = "update", sample.Index, sample.SessionPrepareMs, sample.Prepared, sample.UiVoteMs, sample.UiLayoutMs,
                    sample.UiTotalMs, sample.Weight, sample.EmSize, sample.Family });
            }
            if (warmUpTracker is not null) await warmUpTracker.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            probe.Stop();
            var startWindow = probe.MaxLatency(t0, t0 + FirstUpdate);
            var whole = probe.MaxLatency(t0, clock.Elapsed);
            Write(new
            {
                type = "summary", exitCode = 0, fontPath = path, startCallMs, warmUpDoneMs,
                firstUpdateUiMs = samples[0].UiTotalMs, firstUpdateSessionMs = samples[0].SessionPrepareMs,
                newTextUiMedianMs = Median(samples.Skip(1).Select(static s => s.UiTotalMs)),
                newTextUiVoteMedianMs = Median(samples.Skip(1).Select(static s => s.UiVoteMs)),
                newTextSessionMedianMs = Median(samples.Skip(1).Select(static s => s.SessionPrepareMs)),
                uiThreadBusyTotalMs = startCallMs + samples.Sum(static s => s.UiTotalMs),
                maxProbeLatencyStartMs = startWindow, maxProbeLatencyWholeMs = whole, probeSamples = probe.Count,
                weights = samples.Select(static s => s.Weight), emSizes = samples.Select(static s => s.EmSize),
                families = samples.Select(static s => s.Family).Distinct(),
            });
            Console.WriteLine($"UiFontsReplay {path}: start {startCallMs:F1} ms na wątku UI, pierwszy napis {samples[0].UiTotalMs:F1} ms, " +
                $"maks. przestój {whole:F1} ms.");
        }
        catch (Exception ex)
        {
            exitCode = ex is TimeoutException ? 3 : 4;
            Write(new { type = "error", errorType = ex.GetType().Name });
        }
        finally
        {
            probe.Stop();
            dispatcher.InvokeShutdown();
            thread.Join(TimeSpan.FromSeconds(3));
        }
        return exitCode;
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : Math.Round(sorted[sorted.Length / 2], 3);
    }

    private static GlyphCover? BuildCover(string text)
    {
        const int width = 560;
        const int height = 96;
        var frame = new OcrBitmap(new byte[width * height * 4], width, height, width * 4);
        Fill(frame, 0, 0, width, height, 0x1B2230);
        var x = 24;
        foreach (var c in text)
        {
            if (c == ' ')
            {
                x += 12;
                continue;
            }
            var tall = char.IsUpper(c) || c is 'b' or 'd' or 'f' or 'h' or 'k' or 'l' or 't';
            var top = tall ? 26 : 38;
            Fill(frame, x - 2, top - 2, 9, 72 - top + 4, 0x000000);
            Fill(frame, x, top, 5, 72 - top, 0xF2EEE4);
            x += 13;
            if (x > width - 30) break;
        }
        var box = new RectPx(20, 22, Math.Min(width - 24, x), 56);
        return GlyphCoverBuilder.Build(frame, box, [box], [text]);
    }

    private static void Fill(OcrBitmap frame, int left, int top, int w, int h, int rgb)
    {
        for (var y = Math.Max(0, top); y < Math.Min(frame.Height, top + h); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(frame.Width, left + w); x++)
            {
                var o = y * frame.Stride + x * 4;
                frame.PixelsBgra32[o] = (byte)rgb;
                frame.PixelsBgra32[o + 1] = (byte)(rgb >> 8);
                frame.PixelsBgra32[o + 2] = (byte)(rgb >> 16);
                frame.PixelsBgra32[o + 3] = 255;
            }
        }
    }

    private static async Task<(Dispatcher Dispatcher, Thread Thread)> StartUiThreadAsync()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new StackPanel { Margin = new Thickness(16) };
                foreach (var line in new[] { "GTO UiFontsReplay - local test", "Pulpit · Gra · Tłumaczenie · Nakładka", "Live: obserwuję" })
                    panel.Children.Add(new TextBlock { Text = line, FontSize = 14, Margin = new Thickness(0, 4, 0, 4) });
                var window = new Window { Title = "GTO UiFontsReplay - local test", Width = 420, Height = 180, Left = 40, Top = 40,
                    WindowStyle = WindowStyle.ToolWindow, ShowActivated = false, Content = panel };
                window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.Show();
                ready.TrySetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return (await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)), thread);
    }

    private sealed class Probe(Dispatcher dispatcher, Stopwatch clock)
    {
        private static readonly TimeSpan ProbeStep = TimeSpan.FromMilliseconds(0.5);
        private readonly Lock _gate = new();
        private readonly List<(TimeSpan Posted, double LatencyMs)> _samples = [];
        private volatile bool _stopped;
        private Thread? _thread;

        public int Count
        {
            get { lock (_gate) return _samples.Count; }
        }

        public void Start()
        {
            _thread = new Thread(() =>
            {
                while (!_stopped)
                {
                    var posted = clock.Elapsed;
                    var operation = dispatcher.InvokeAsync(() => clock.Elapsed, DispatcherPriority.Send);
                    if (operation.Wait(TimeSpan.FromSeconds(5)) != DispatcherOperationStatus.Completed) break;
                    lock (_gate) _samples.Add((posted, (operation.Result - posted).TotalMilliseconds));
                    var next = clock.Elapsed + ProbeStep;
                    while (clock.Elapsed < next && !_stopped) Thread.SpinWait(64);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _stopped = true;
            _thread?.Join(TimeSpan.FromSeconds(6));
        }

        public double MaxLatency(TimeSpan from, TimeSpan to)
        {
            lock (_gate)
            {
                return Math.Round(_samples.Where(s => s.Posted >= from && s.Posted <= to).Select(static s => s.LatencyMs).DefaultIfEmpty(0).Max(), 3);
            }
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in UiFontsReplay.");
    }

    private sealed class NoOcr : IOcrProvider
    {
        public string Name => "No OCR";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => true;
        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult([], languageTag));
    }
}
