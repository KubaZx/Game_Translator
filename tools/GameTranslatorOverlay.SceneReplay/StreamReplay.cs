using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

internal static class StreamReplay
{
    public static readonly string[] Scenarios = ["stream", "stream-refusal", "stream-change"];

    private const string ChangedMarker = "garden";

    private static readonly string[] ChangedLines =
    [
        "The garden gate is open now.",
        "Water the garden before dusk.",
        "A garden path leads to the pier.",
        "Lanterns light the garden at night.",
        "Leave the garden key by the well.",
    ];

    public sealed record Options(string Provider, bool Streaming, int FirstLineMs, int LineMs);

    private const string Prefix = "PL ";
    private const string LocalEndpoint = "http://localhost:5799/v1";

    private static readonly string[] Lines =
    [
        "Welcome to the old lighthouse.",
        "The keeper left a note upstairs.",
        "Bring the brass key to the door.",
        "Do not wake the sleeping gulls.",
        "Return before the tide comes in.",
    ];

    public static int Run(string output, string scenario, Options options)
    {
        using var report = new Report(output, scenario, options);
        return RunAsync(report, scenario == "stream-refusal", options).GetAwaiter().GetResult();
    }

    public static bool RequiresStreaming(string scenario) => scenario is "stream-refusal";

    private static async Task<int> RunAsync(Report report, bool refuse, Options options)
    {
        var root = Path.Combine(Path.GetTempPath(), "gto-stream-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var claude = options.Provider == "claude";
        var settings = new AppSettings
        {
            Provider = claude ? ClaudeTranslationProvider.ProviderName : OpenAiCompatibleTranslationProvider.ProviderName,
            PrivateMode = true,
            ActiveProfileId = null,
            OcrUpscale = 1,
            LlmEndpoint = LocalEndpoint,
            LlmModel = "replay-model",
            StreamLlmResponses = options.Streaming,
        };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        var model = new ScriptedModelHandler(report, claude, refuse, options);
        using var http = new HttpClient(model);
        using var blocked = new HttpClient(new NoNetworkHandler());
        var llm = new OpenAiCompatibleTranslationProvider(http, static () => null, static () => null,
            () => settings.LlmEndpoint, () => settings.LlmModel);
        var anthropic = new ClaudeTranslationProvider(http, static () => "replay-placeholder", () => settings.ClaudeModel);
        var ocr = new ObservedOcr(new WindowsOcrProvider(), report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider(),
            new DeepLTranslationProvider(blocked, static () => null), ocr, usage, logging,
            additionalProviders: [llm, anthropic]);
        orchestrator.Initialize();
        if (!ocr.IsLanguageAvailable(settings.SourceLanguage)) throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            window = await TestWindow.CreateAsync();
            await window.AfterRenderingAsync();
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle,
                new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true }, report.Update, logging.CreateLogger("StreamReplay"));
            session.Start();
            await Task.Delay(1500);
            await window.ShowLinesAsync(Lines, report);
            if (report.Change)
            {
                await report.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(options.FirstLineMs + 100);
                await window.ShowLinesAsync(ChangedLines, report, changed: true);
                await report.ChangedBlocks.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(3000);
            }
            else if (refuse)
            {
                await report.FirstBlock.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await report.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(1500);
            }
            else
            {
                await report.AllBlocks.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(3000);
            }
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
        if (exitCode == 0 && report.ScreenFallback) { exitCode = 4; reason = "screen_fallback_observed"; }
        if (exitCode == 0 && report.DistinctBlocks < 2) { exitCode = 4; reason = "fewer_than_two_blocks"; }
        var result = report.Finish(exitCode, reason, usage, orchestrator.Latency);
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return result;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in StreamReplay.");
    }

    private sealed class ScriptedModelHandler(Report report, bool claude, bool refuse, Options options) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Relaxed = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            if (!host.Equals(claude ? "api.anthropic.com" : "localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("StreamReplay answers only its own scripted model address.");
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var stream = root.TryGetProperty("stream", out var flag) && flag.ValueKind == JsonValueKind.True;
            var messages = root.GetProperty("messages");
            var user = messages[messages.GetArrayLength() - 1].GetProperty("content");
            var userText = user.ValueKind == JsonValueKind.String ? user.GetString()! : user[0].GetProperty("text").GetString()!;
            using var payload = JsonDocument.Parse(userText[(userText.IndexOf('\n') + 1)..]);
            var translations = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => Prefix + t.GetString()).ToArray();
            var requestId = report.ProviderRequest(stream, translations.Length);
            var started = Stopwatch.GetTimestamp();

            if (!stream)
            {
                await Task.Delay(options.FirstLineMs + options.LineMs * (translations.Length - 1), cancellationToken).ConfigureAwait(false);
                report.ProviderFinished(requestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return Json(claude ? ClaudeMessage(Answer(translations)) : OpenAiMessage(Answer(translations)));
            }

            var pipe = new PushStream();
            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteStreamAsync(pipe, translations, started, requestId, cancellationToken).ConfigureAwait(false);
                    pipe.Complete();
                }
                catch (Exception ex)
                {
                    pipe.Complete(ex);
                }
            }, CancellationToken.None);
            var content = new StreamContent(pipe);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private async Task WriteStreamAsync(PushStream pipe, string[] translations, long started, int requestId, CancellationToken cancellationToken)
        {
            if (claude)
            {
                pipe.Push(Event("message_start", new
                {
                    type = "message_start",
                    message = new
                    {
                        id = "msg_replay", type = "message", role = "assistant", model = "claude-opus-5-5",
                        content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null,
                        usage = new { input_tokens = 10, output_tokens = 1 },
                    },
                }));
                pipe.Push(Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }));
            }
            for (var k = 0; k < translations.Length; k++)
            {
                var due = options.FirstLineMs + options.LineMs * k;
                var wait = due - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
                var element = JsonSerializer.Serialize(translations[k], Relaxed);
                var piece = (k == 0 ? "{\"translations\": [" : ", ") + element + (k == translations.Length - 1 ? "]}" : string.Empty);
                var half = piece.Length / 2;
                Delta(pipe, piece[..half]);
                Delta(pipe, piece[half..]);
                report.ProviderLine(requestId, k, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            if (claude)
            {
                pipe.Push(Event("content_block_stop", new { type = "content_block_stop", index = 0 }));
                pipe.Push(Event("message_delta", new
                {
                    type = "message_delta",
                    delta = new { stop_reason = refuse ? "refusal" : "end_turn", stop_sequence = (string?)null },
                    usage = new { output_tokens = 40 },
                }));
                pipe.Push(Event("message_stop", new { type = "message_stop" }));
            }
            else
            {
                pipe.Push("data: " + JsonSerializer.Serialize(new
                {
                    choices = new[] { new { index = 0, delta = new { content = (string?)null }, finish_reason = refuse ? "content_filter" : "stop" } },
                }, Relaxed) + "\n\n");
                pipe.Push("data: [DONE]\n\n");
            }
            report.ProviderFinished(requestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        private void Delta(PushStream pipe, string text) => pipe.Push(claude
            ? Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
            : "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text } } } }, Relaxed) + "\n\n");

        private static string Event(string type, object data) => $"event: {type}\ndata: {JsonSerializer.Serialize(data, Relaxed)}\n\n";

        private static string Answer(string[] translations) => JsonSerializer.Serialize(new { translations }, Relaxed);

        private string ClaudeMessage(string text) => JsonSerializer.Serialize(new
        {
            id = "msg_replay", type = "message", role = "assistant", model = "claude-opus-5-5",
            content = new[] { new { type = "text", text } },
            stop_reason = refuse ? "refusal" : "end_turn", stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 40 },
        }, Relaxed);

        private string OpenAiMessage(string text) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = text }, finish_reason = refuse ? "content_filter" : "stop" } },
        }, Relaxed);

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class PushStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (!_chunks.Reader.TryRead(out _current)) continue;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            report.Ocr(result.Lines.Count);
            return result;
        }
    }

    private sealed class TestWindow(Window window, TextBlock[] rows, Thread thread)
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
                    var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
                    var reference = new Border { Width = 50, Height = 640, Background = Brushes.Gray };
                    Canvas.SetLeft(reference, 890);
                    canvas.Children.Add(reference);
                    var rows = new TextBlock[Lines.Length];
                    for (var i = 0; i < rows.Length; i++)
                    {
                        rows[i] = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 26, Foreground = Brushes.White };
                        Canvas.SetLeft(rows[i], i % 2 == 0 ? 60 : 260);
                        Canvas.SetTop(rows[i], 40 + i * 115);
                        canvas.Children.Add(rows[i]);
                    }
                    var window = new Window
                    {
                        Title = "GTO StreamReplay - local test", Width = 940, Height = 640, Left = 40, Top = 40,
                        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, Content = canvas,
                    };
                    window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    window.Show();
                    ready.TrySetResult(new TestWindow(window, rows, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task AfterRenderingAsync() => RenderAsync(null, null);

        public Task ShowLinesAsync(IReadOnlyList<string> lines, Report report, bool changed = false) => RenderAsync(() =>
        {
            for (var i = 0; i < rows.Length; i++) rows[i].Text = lines[i];
            report.Event(new { type = "lines_mutated", lines = lines.Count, changed });
        }, changed ? report.BeginChange : report.BeginMeasurement);

        private async Task RenderAsync(Action? change, Action? renderedCallback)
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
        private readonly bool _refusal;
        private readonly Dictionary<string, string> _shown = new(StringComparer.Ordinal);
        private readonly HashSet<string> _everShown = new(StringComparer.Ordinal);
        private readonly List<(int Id, bool Stream, int Lines, double At)> _requests = [];
        private readonly Dictionary<int, List<double>> _lineTimes = [];
        private readonly Dictionary<int, double> _requestTotals = [];
        private double? _startedAt, _firstBlockAt, _allBlocksAt, _clearedAfterRefusalAt;
        private double? _changedAt, _oldClearedAt, _changedFirstAt, _changedAllAt, _lastOldAt;
        private int _maxShown, _textChanges, _drops, _provisionalUpdates, _ocrRuns, _updates;
        private int _oldShownAtChange, _oldUpdatesAfterChangedAll, _mixedUpdates, _maxOldAfterChange;
        private bool _ended;

        public bool ScreenFallback { get; private set; }
        public bool Change { get; }
        public int DistinctBlocks { get { lock (_gate) return _everShown.Count; } }
        public TaskCompletionSource FirstBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllBlocks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChangedBlocks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static bool IsChanged(string translation) => translation.Contains(ChangedMarker, StringComparison.OrdinalIgnoreCase);

        public void BeginChange()
        {
            lock (_gate)
            {
                _changedAt = _clock.Elapsed.TotalMilliseconds;
                _oldShownAtChange = _shown.Values.Count(static t => !IsChanged(t));
                Write(new { type = "change_started", oldShown = _oldShownAtChange });
            }
        }

        public Report(string output, string scenario, Options options)
        {
            _refusal = scenario == "stream-refusal";
            Change = scenario == "stream-change";
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new
            {
                type = "header", scenario, provider = options.Provider, streaming = options.Streaming,
                firstLineMs = options.FirstLineMs, lineMs = options.LineMs, lines = Lines.Length, ocrMode = "windows",
                cache = "fresh-memory", privateMode = true, network = "scripted local handler only",
                modelMeaning = "Scripted handler: element k of the answer completes firstLineMs + k*lineMs after the request; without stream the whole answer arrives at the last element's time",
                timingMeaning = "From WPF Rendering of the new lines to session callbacks; not physical overlay presentation latency",
            });
        }

        public void Event(object data) { lock (_gate) Write(data); }

        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        public void BeginMeasurement()
        {
            lock (_gate)
            {
                _startedAt = _clock.Elapsed.TotalMilliseconds;
                Write(new { type = "measurement_started" });
            }
        }

        public void EndMeasurement() { lock (_gate) _ended = true; }

        public int ProviderRequest(bool stream, int lines)
        {
            lock (_gate)
            {
                var id = _requests.Count;
                _requests.Add((id, stream, lines, _clock.Elapsed.TotalMilliseconds));
                Write(new { type = "provider_request", id, stream, lines });
                FirstRequest.TrySetResult();
                return id;
            }
        }

        public void ProviderLine(int id, int index, double sinceRequestMs)
        {
            lock (_gate)
            {
                if (!_lineTimes.TryGetValue(id, out var list)) _lineTimes[id] = list = [];
                list.Add(sinceRequestMs);
                Write(new { type = "provider_line", id, index, sinceRequestMs });
            }
        }

        public void ProviderFinished(int id, double sinceRequestMs)
        {
            lock (_gate)
            {
                _requestTotals[id] = sinceRequestMs;
                Write(new { type = "provider_finished", id, sinceRequestMs });
            }
        }

        public void Ocr(int lines)
        {
            lock (_gate)
            {
                _ocrRuns++;
                Write(new { type = "ocr_completed", lines });
            }
        }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                _updates++;
                ScreenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Stopped || _ended) return;
                var provisional = update.StatusLine.Contains("pierwszych tłumaczeń", StringComparison.Ordinal);
                if (provisional) _provisionalUpdates++;
                if (update.ClearOverlay) _shown.Clear();
                if (update.Blocks is { } blocks)
                {
                    var now = blocks.Where(static b => b.TranslatedText.StartsWith(Prefix, StringComparison.Ordinal))
                        .ToDictionary(static b => b.Key, static b => b.TranslatedText, StringComparer.Ordinal);
                    foreach (var (key, text) in now)
                    {
                        if (_shown.TryGetValue(key, out var before) && before != text) _textChanges++;
                    }
                    if (now.Count < _shown.Count && _startedAt is not null && _allBlocksAt is not null) _drops++;
                    _shown.Clear();
                    foreach (var (key, text) in now)
                    {
                        _shown[key] = text;
                        _everShown.Add(key);
                    }
                }
                var elapsed = _clock.Elapsed.TotalMilliseconds;
                if (_startedAt is not null && _shown.Count > 0 && _firstBlockAt is null)
                {
                    _firstBlockAt = elapsed;
                    FirstBlock.TrySetResult();
                }
                _maxShown = Math.Max(_maxShown, _shown.Count);
                if (_startedAt is not null && _shown.Count >= Lines.Length && _allBlocksAt is null)
                {
                    _allBlocksAt = elapsed;
                    AllBlocks.TrySetResult();
                }
                if (_refusal && _firstBlockAt is not null && _shown.Count == 0 && _clearedAfterRefusalAt is null)
                {
                    _clearedAfterRefusalAt = elapsed;
                    Refused.TrySetResult();
                }
                var oldShown = _shown.Values.Count(static t => !IsChanged(t));
                var changedShown = _shown.Count - oldShown;
                if (_changedAt is not null)
                {
                    if (oldShown == 0) _oldClearedAt ??= elapsed;
                    if (oldShown > 0) _lastOldAt = elapsed;
                    _maxOldAfterChange = Math.Max(_maxOldAfterChange, oldShown);
                    if (changedShown > 0) _changedFirstAt ??= elapsed;
                    if (oldShown > 0 && changedShown > 0) _mixedUpdates++;
                    if (_changedAllAt is not null && oldShown > 0) _oldUpdatesAfterChangedAll++;
                    if (changedShown >= ChangedLines.Length && oldShown == 0 && _changedAllAt is null)
                    {
                        _changedAllAt = elapsed;
                        ChangedBlocks.TrySetResult();
                    }
                }
                Write(new
                {
                    type = "update", shown = _shown.Count, oldShown, changedShown, provisional, update.ClearOverlay,
                    completedOcr = update.Diagnostics is not null, translateMs = update.Diagnostics?.TranslateMs,
                    notice = update.Notice is not null,
                });
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage, LatencyMonitor latency)
        {
            lock (_gate)
            {
                var first = _requests.Count > 0 ? _requests[0] : default;
                var firstLines = _lineTimes.TryGetValue(0, out var lines) ? lines : [];
                Write(new
                {
                    type = "summary", exitCode, reason, screenFallback = ScreenFallback, updates = _updates, ocrRuns = _ocrRuns,
                    distinctBlocks = _everShown.Count, maxShown = _maxShown,
                    firstBlockMs = _firstBlockAt - _startedAt, allBlocksMs = _allBlocksAt - _startedAt,
                    firstToAllMs = _allBlocksAt - _firstBlockAt,
                    clearedAfterRefusalMs = _clearedAfterRefusalAt - _firstBlockAt,
                    oldShownAtChange = Change ? _oldShownAtChange : (int?)null,
                    maxOldShownAfterChange = Change ? _maxOldAfterChange : (int?)null,
                    lastOldShownAfterChangeMs = _lastOldAt - _changedAt,
                    oldClearedAfterChangeMs = _oldClearedAt - _changedAt, changedFirstBlockMs = _changedFirstAt - _changedAt,
                    changedAllBlocksMs = _changedAllAt - _changedAt, mixedOldAndChangedUpdates = Change ? _mixedUpdates : (int?)null,
                    oldUpdatesAfterChangedAll = Change ? _oldUpdatesAfterChangedAll : (int?)null,
                    provisionalUpdates = _provisionalUpdates, textChangesOfShownBlocks = _textChanges, dropsAfterAllShown = _drops,
                    providerRequests = _requests.Count, streamedRequests = _requests.Count(static r => r.Stream),
                    firstRequestLines = first.Lines, firstRequestAfterChangeMs = _requests.Count > 0 ? first.At - _startedAt : null,
                    firstRequestFirstLineMs = firstLines.Count > 0 ? firstLines[0] : (double?)null,
                    firstRequestTotalMs = _requestTotals.TryGetValue(0, out var total) ? total : (double?)null,
                    latencyProviderMedianMs = latency.Summarize(LatencyStage.Provider)?.MedianMs,
                    latencyProviderFirstLineMedianMs = latency.Summarize(LatencyStage.ProviderFirstLine)?.MedianMs,
                    latencyNewTextMedianMs = latency.Summarize(LatencyStage.NewText)?.MedianMs,
                    latencyChangeToTextMedianMs = latency.Summarize(LatencyStage.ChangeToText)?.MedianMs,
                    usage.ApiRequests, usage.ApiCharacters, usage.FailedRequests,
                });
            }
            Console.WriteLine($"StreamReplay: {reason}; pierwszy blok {_firstBlockAt - _startedAt:F0} ms, wszystkie {_allBlocksAt - _startedAt:F0} ms.");
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
