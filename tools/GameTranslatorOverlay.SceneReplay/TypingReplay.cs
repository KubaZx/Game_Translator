using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

internal static class TypingReplay
{
    private const string ProfileId = "typing-replay";
    private static readonly string[] Lines =
    [
        "Welcome back, detective. The lighthouse keeper left a sealed note for you on the desk.",
        "Careful now! The floor creaks, and the clock upstairs has not ticked in twelve years.",
    ];
    private static readonly string[] Distractors =
    [
        "Open the drawer",
        "The lighthouse is closed for the winter.",
        "Welcome to the museum of forgotten maps.",
    ];
    private const double FontSizeDip = 26;
    private const int CharMs = 35;
    private const int PunctuationPauseMs = 450;
    private const int HoldAfterLineMs = 3000;
    private const int GapMs = 1500;

    public static int Run(string output, bool hold)
    {
        using var report = new Report(output, hold);
        return RunAsync(report, hold).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report, bool hold)
    {
        var root = Path.Combine(Path.GetTempPath(), "gto-typing-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Directory.CreateDirectory(Path.Combine(root, "profiles", ProfileId));
        await File.WriteAllTextAsync(Path.Combine(root, "profiles", ProfileId, "profile.json"), JsonSerializer.Serialize(new
        {
            id = ProfileId,
            name = "Typing replay",
            profileVersion = 1,
            processNames = Array.Empty<string>(),
            windowTitles = Array.Empty<string>(),
            sourceLanguage = "en",
        }));
        Directory.CreateDirectory(paths.CorpusDirectory);
        var corpus = Lines.Select((text, i) => new { key = "line-" + i, en = text, kind = "dialog", source = "synthetic" })
            .Concat(Distractors.Select((text, i) => new { key = "other-" + i, en = text, kind = i == 0 ? "ui" : "dialog", source = "synthetic" }))
            .Select(entry => JsonSerializer.Serialize(entry));
        await File.WriteAllLinesAsync(Path.Combine(paths.CorpusDirectory, ProfileId + CorpusCatalog.FileSuffix), corpus);

        var settings = new AppSettings { Provider = MockTranslationProvider.ProviderName, PrivateMode = true, ActiveProfileId = ProfileId, OcrUpscale = 1 };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var usage = new UsageTracker();
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            window = await TestWindow.CreateAsync(report);
            await window.AfterRenderingAsync();
            var ocr = new TypedOcr(window, report);
            var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
                new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
                new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.Zero },
                new DeepLTranslationProvider(http, static () => null), ocr, usage, logging,
                corpusCatalog: CorpusCatalog.CreateDefault(paths));
            orchestrator.Initialize();
            if (!orchestrator.ActiveCorpus.IsLoaded) throw new InvalidOperationException("Synthetic corpus did not load.");
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, AllowAutoUpscale = false, EnableDiagnostics = true, HoldTypingPrefixes = () => hold },
                report.Update, logging.CreateLogger("TypingReplay"));
            session.Start();
            await Task.Delay(1500);
            for (var round = 0; round < Lines.Length; round++)
            {
                await window.TypeAsync(round, Lines[round]);
                await Task.Delay(HoldAfterLineMs);
                await window.ClearAsync();
                await Task.Delay(GapMs);
            }
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex is TimeoutException ? "scenario_timeout" : "runtime_error";
            report.Event(new { type = "error", errorType = ex.GetType().Name, ex.Message });
        }
        finally
        {
            report.EndMeasurement();
            if (session is not null)
            {
                session.Stop();
                try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(10)); session.Dispose(); }
                catch (OperationCanceledException) { session.Dispose(); }
                catch (Exception) { exitCode = 4; reason = "session_shutdown_failed"; }
            }
            if (window is not null)
            {
                try { await window.CloseAsync(); }
                catch (Exception) { exitCode = 4; reason = "window_shutdown_failed"; }
            }
        }
        var result = report.Finish(exitCode, reason, usage);
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return result;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in TypingReplay.");
    }

    private sealed class TypedOcr(TestWindow window, Report report) : IOcrProvider
    {
        public string Name => "Captured white band mapped to the typed prefix by rendered ink width";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            var band = WhiteBand(bitmap);
            if (band is not { } box || window.CurrentRound is not { } round)
                return Task.FromResult(new OcrResult([], languageTag));
            if (CropLeft(bitmap) is not { } cropLeft)
            {
                report.FragmentCrop(round);
                return Task.FromResult(new OcrResult([], languageTag));
            }
            var (text, prefix) = window.TextForInk(round, cropLeft + box.X, cropLeft + box.Right);
            report.Ocr(round, text.Length, prefix);
            if (text.Length == 0) return Task.FromResult(new OcrResult([], languageTag));
            var line = new OcrLine(text, box, [new OcrWord(text, box)]);
            return Task.FromResult(new OcrResult([line], languageTag));
        }

        private static int? CropLeft(OcrBitmap bitmap)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = y * bitmap.Stride;
                var b = bitmap.PixelsBgra32[p];
                var g = bitmap.PixelsBgra32[p + 1];
                var r = bitmap.PixelsBgra32[p + 2];
                if (r is < 16 or > 31 || g is < 16 or > 31 || b is < 16 or > 31) continue;
                return TestWindow.DecodeX(r, g, b);
            }
            return null;
        }

        private static RectPx? WhiteBand(OcrBitmap bitmap)
        {
            var band = default(RectPx);
            for (var y = 0; y < bitmap.Height; y++)
            {
                var left = bitmap.Width;
                var right = -1;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var p = y * bitmap.Stride + x * 4;
                    if (bitmap.PixelsBgra32[p] < 200 || bitmap.PixelsBgra32[p + 1] < 200 || bitmap.PixelsBgra32[p + 2] < 200) continue;
                    left = Math.Min(left, x);
                    right = x;
                }
                if (right >= left) band = band.Union(new RectPx(left, y, right - left + 1, 1));
            }
            return band.IsEmpty || band.Width < 8 ? null : band;
        }
    }

    private sealed class TestWindow(Window window, TextBlock text, Thread thread, Report report)
    {
        private readonly double[][] _inkWidths = new double[Lines.Length][];
        private volatile int _round = -1;
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;
        public int? CurrentRound => _round >= 0 ? _round : null;

        public static async Task<TestWindow> CreateAsync(Report report)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var canvas = new Canvas { Background = CoordinateBrush() };
                    RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.NearestNeighbor);
                    var stripe = new Border { Width = 40, Height = 200, Background = Brushes.Gray };
                    Canvas.SetLeft(stripe, 0);
                    Canvas.SetTop(stripe, 500);
                    canvas.Children.Add(stripe);
                    var text = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = FontSizeDip,
                        Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap };
                    Canvas.SetLeft(text, 80);
                    Canvas.SetTop(text, 300);
                    canvas.Children.Add(text);
                    var window = new Window { Title = "GTO typing - local test", Width = 1280, Height = 720,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, Content = canvas };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    window.Width = 1920 / dpi.DpiScaleX;
                    window.Height = 1080 / dpi.DpiScaleY;
                    var created = new TestWindow(window, text, thread!, report);
                    created.MeasurePrefixes(dpi.DpiScaleX, 80 * dpi.DpiScaleX);
                    ready.TrySetResult(created);
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private const int FrameWidth = 1920;
        private readonly double[][] _inkLeft = new double[Lines.Length][];

        private static ImageBrush CoordinateBrush()
        {
            var pixels = new byte[FrameWidth * 4];
            for (var x = 0; x < FrameWidth; x++)
            {
                pixels[x * 4] = (byte)(16 + ((x / 8) % 16));
                pixels[x * 4 + 1] = (byte)(16 + x / 128);
                pixels[x * 4 + 2] = (byte)(16 + (x % 8) * 2);
                pixels[x * 4 + 3] = 255;
            }
            var bitmap = BitmapSource.Create(FrameWidth, 1, 96, 96, PixelFormats.Bgra32, null, pixels, FrameWidth * 4);
            bitmap.Freeze();
            return new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        }

        public static int DecodeX(byte r, byte g, byte b) => (g - 16) * 128 + (b - 16) * 8 + (r - 16) / 2;

        private void MeasurePrefixes(double scale, double originPx)
        {
            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            for (var round = 0; round < Lines.Length; round++)
            {
                var line = Lines[round];
                var right = new double[line.Length + 1];
                var left = new double[line.Length + 1];
                for (var n = 1; n <= line.Length; n++)
                {
                    var formatted = new FormattedText(line[..n], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, FontSizeDip, Brushes.White, scale);
                    var bounds = formatted.BuildGeometry(new Point(0, 0)).Bounds;
                    right[n] = bounds.IsEmpty ? originPx : originPx + bounds.Right * scale;
                    var before = new FormattedText(line[..(n - 1)], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, FontSizeDip, Brushes.White, scale).WidthIncludingTrailingWhitespace;
                    var glyph = new FormattedText(line[(n - 1)..n], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, FontSizeDip, Brushes.White, scale).BuildGeometry(new Point(0, 0)).Bounds;
                    left[n - 1] = originPx + (before + (glyph.IsEmpty ? 0 : glyph.Left)) * scale;
                }
                _inkWidths[round] = right;
                _inkLeft[round] = left;
            }
        }

        public (string Text, bool Prefix) TextForInk(int round, int inkLeftPx, int inkRightPx)
        {
            var right = _inkWidths[round];
            var left = _inkLeft[round];
            var end = 0;
            for (var n = 1; n < right.Length; n++)
                if (right[n] <= inkRightPx + 3) end = n;
            var start = 0;
            for (var i = 0; i < end; i++)
            {
                if (char.IsWhiteSpace(Lines[round][i])) continue;
                if (left[i] >= inkLeftPx - 4) { start = i; break; }
            }
            return (Lines[round][start..end].Trim(), start == 0);
        }

        public async Task TypeAsync(int round, string line)
        {
            _round = round;
            report.TypingStarted(round, line.Length);
            for (var n = 1; n <= line.Length; n++)
            {
                var visible = line[..n];
                await window.Dispatcher.InvokeAsync(() => text.Text = visible);
                report.Typed(round, n);
                await Task.Delay(".,!?".Contains(line[n - 1]) ? PunctuationPauseMs : CharMs);
            }
            report.TypingFinished(round);
        }

        public async Task ClearAsync()
        {
            await window.Dispatcher.InvokeAsync(() => text.Text = string.Empty);
            report.Cleared();
            _round = -1;
        }

        public async Task AfterRenderingAsync()
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                EventHandler? handler = null;
                handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(); };
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

    private sealed class RoundStats
    {
        public int Length;
        public double StartedMs;
        public double? FinishedMs;
        public double? FirstShownMs;
        public int ShownWhileTyping;
        public int PartialTexts;
        public int TextChanges;
        public string? LastShown;
        public int OcrReads;
        public int OcrPartialReads;
        public int FragmentCrops;
        public int FragmentReads;
    }

    private sealed class Report : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly RoundStats[] _rounds = Lines.Select(static l => new RoundStats { Length = l.Length }).ToArray();
        private int _round = -1;
        private int _typed;
        private bool _ended;
        private bool _fallback;
        private readonly bool _hold;

        public Report(string output, bool hold)
        {
            _hold = hold;
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new { type = "header", scenario = hold ? "typing" : "typing-nohold", ocrMode = "scripted-ink-width", provider = "Mock",
                providerDelayMs = 0, privateMode = true, network = "blocked", corpus = "synthetic", charMs = CharMs,
                punctuationPauseMs = PunctuationPauseMs, holdTypingPrefixes = hold, actualOverlayWindow = false });
        }

        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));
        public void EndMeasurement() { lock (_gate) _ended = true; }

        public void TypingStarted(int round, int length)
        {
            lock (_gate)
            {
                _round = round;
                _typed = 0;
                _rounds[round].StartedMs = _clock.Elapsed.TotalMilliseconds;
                Write(new { type = "typing_started", round, length });
            }
        }

        public void Typed(int round, int count) { lock (_gate) _typed = count; }

        public void TypingFinished(int round)
        {
            lock (_gate)
            {
                _rounds[round].FinishedMs = _clock.Elapsed.TotalMilliseconds;
                Write(new { type = "typing_finished", round });
            }
        }

        public void Cleared()
        {
            lock (_gate)
            {
                Write(new { type = "cleared", round = _round });
                _round = -1;
                _typed = 0;
            }
        }

        public void FragmentCrop(int round)
        {
            lock (_gate)
            {
                _rounds[round].FragmentCrops++;
                Write(new { type = "fragment_crop", round });
            }
        }

        public void Ocr(int round, int length, bool prefix)
        {
            lock (_gate)
            {
                var stats = _rounds[round];
                stats.OcrReads++;
                if (length < Lines[round].Length) stats.OcrPartialReads++;
                if (!prefix) stats.FragmentReads++;
                Write(new { type = "ocr", round, readLength = length, fullLength = Lines[round].Length, prefix });
            }
        }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                if (_ended) return;
                _fallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Blocks is null) return;
                var shown = update.Blocks.Count > 0 ? update.Blocks[0].TranslatedText : null;
                Write(new { type = "update", round = _round, typed = _typed, blocks = update.Blocks.Count, shown });
                if (_round < 0)
                {
                    return;
                }
                var stats = _rounds[_round];
                if (shown is null) return;
                var now = _clock.Elapsed.TotalMilliseconds;
                stats.FirstShownMs ??= now;
                if (_typed < stats.Length) stats.ShownWhileTyping++;
                if (!shown.Replace('\n', ' ').Contains(LastWord(Lines[_round]), StringComparison.OrdinalIgnoreCase)) stats.PartialTexts++;
                if (stats.LastShown is not null && stats.LastShown != shown) stats.TextChanges++;
                stats.LastShown = shown;
            }
        }

        private static string LastWord(string line) => line.TrimEnd('.', '!', '?').Split(' ')[^1];

        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var rounds = _rounds.Select((r, i) => new
                {
                    round = i,
                    typingMs = r.FinishedMs - r.StartedMs,
                    shownAfterTypingEndMs = r.FirstShownMs - r.FinishedMs,
                    shownWhileTypingUpdates = r.ShownWhileTyping,
                    partialTranslationUpdates = r.PartialTexts,
                    textChanges = r.TextChanges,
                    ocrReads = r.OcrReads,
                    ocrPartialReads = r.OcrPartialReads,
                    undecodedCrops = r.FragmentCrops,
                    fragmentReads = r.FragmentReads,
                }).ToList();
                var valid = exitCode == 0 && !_fallback && _rounds.All(r => r.FinishedMs is not null && r.OcrPartialReads > 0 && r.FirstShownMs is not null && r.FragmentCrops == 0);
                Write(new { type = "summary", exitCode, reason, fixtureValid = valid, holdTypingPrefixes = _hold, screenFallback = _fallback,
                    rounds, mockProviderRequests = usage.ApiRequests, mockProviderCharacters = usage.ApiCharacters, usage.CacheHits });
                foreach (var r in rounds)
                    Console.WriteLine($"TypingReplay round {r.round}: typing {r.typingMs:F0} ms; first shown {r.shownAfterTypingEndMs:F0} ms after typing ended; " +
                        $"shown while typing {r.shownWhileTypingUpdates}; partial translations {r.partialTranslationUpdates}; text changes {r.textChanges}; " +
                        $"OCR {r.ocrReads} ({r.ocrPartialReads} partial, {r.fragmentReads} fragments, {r.undecodedCrops} undecoded).");
                Console.WriteLine($"TypingReplay: {reason}; valid={valid}; hold={_hold}; provider requests={usage.ApiRequests}, characters={usage.ApiCharacters}.");
                return exitCode == 0 && !valid ? 4 : exitCode;
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
