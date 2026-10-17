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
    public static readonly string[] Scenarios = ["stream", "stream-refusal", "stream-change", "stream-partition"];

    public const string LocalProvider = "openai-local";

    private const string ChangedMarker = "garden";
    private const int PartitionSize = 3;
    private static readonly string[] LineMarkers = ["lighthouse", "keeper", "brass", "gulls", "tide"];

    private static readonly string[] ChangedLines =
    [
        "The garden gate is open now.",
        "Water the garden before dusk.",
        "A garden path leads to the pier.",
        "Lanterns light the garden at night.",
        "Leave the garden key by the well.",
    ];

    public sealed record Options(string Provider, bool Streaming, int FirstLineMs, int LineMs)
    {
        public Uri? LocalEndpoint { get; init; }
        public string? LocalModel { get; init; }
        public bool Local => Provider == LocalProvider;
    }

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
        return RunAsync(report, scenario == "stream-refusal", scenario == "stream-partition", options).GetAwaiter().GetResult();
    }

    public static bool RequiresStreaming(string scenario) => scenario is "stream-refusal" or "stream-partition";

    public static bool SupportsLocalProvider(string scenario) => scenario == "stream";

    private static async Task<int> RunAsync(Report report, bool refuse, bool partitioned, Options options)
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
            LlmEndpoint = options.Local ? options.LocalEndpoint!.ToString() : LocalEndpoint,
            LlmModel = options.Local ? options.LocalModel : "replay-model",
            LlmServerOptions = options.Local ? TranslationProviderCatalog.FindLlmPreset(options.LocalEndpoint!)?.ServerOptionsForHost() : null,
            StreamLlmResponses = options.Streaming,
        };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        var observer = new LocalModelObserver(report.Event);
        HttpMessageHandler handler = options.Local
            ? LocalModelServer.CreateHandler(options.LocalEndpoint!, LocalModelServer.DefaultMaxRequests, observer.Wrap)
            : new ScriptedModelHandler(report, claude, refuse, partitioned, options);
        using var http = new HttpClient(handler);
        using var blocked = new HttpClient(new NoNetworkHandler());
        var batching = partitioned ? new LlmProviderOptions { MaxBatchSize = PartitionSize } : null;
        var llm = new OpenAiCompatibleTranslationProvider(http, static () => null, static () => null,
            () => settings.LlmEndpoint, () => settings.LlmModel, batching, serverOptionsAccessor: () => settings.LlmServerOptions);
        ITranslationProvider[] providers = options.Local
            ? [llm]
            : [llm, new ClaudeTranslationProvider(http, static () => "replay-placeholder", () => settings.ClaudeModel, batching)];
        report.Event(new
        {
            type = "provider_setup", provider = options.Provider, endpoint = options.Local ? options.LocalEndpoint!.Authority : null,
            model = options.Local ? options.LocalModel : null, serverOptions = settings.LlmServerOptions?.Describe(),
            maxBatchSize = batching?.MaxBatchSize ?? new LlmProviderOptions().MaxBatchSize,
            maxRequests = options.Local ? LocalModelServer.DefaultMaxRequests : (int?)null,
        });
        var ocr = new ObservedOcr(new WindowsOcrProvider(), report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider(),
            new DeepLTranslationProvider(blocked, static () => null), ocr, usage, logging,
            additionalProviders: providers);
        orchestrator.Initialize();
        if (!ocr.IsLanguageAvailable(settings.SourceLanguage)) throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        var sceneTimeout = TimeSpan.FromSeconds(options.Local ? 180 : 20);
        try
        {
            if (options.Local)
            {
                var warmup = Stopwatch.StartNew();
                var status = await llm.TestConnectionAsync();
                report.Event(new { type = "warmup", ok = status.IsOk, ms = warmup.Elapsed.TotalMilliseconds, message = status.IsOk ? null : status.Message });
                report.WarmupRequests = observer.Responses.Count;
                if (!status.IsOk) throw new WarmupFailedException();
            }
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
                await report.AllBlocks.Task.WaitAsync(sceneTimeout);
                await Task.Delay(3000);
            }
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex switch
            {
                TimeoutException => "scenario_timeout",
                WarmupFailedException => "warmup_failed",
                _ => "runtime_error",
            };
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
        var result = report.Finish(exitCode, reason, usage, orchestrator.Latency, options.Local ? observer.Responses : null);
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return result;
    }

    private sealed class WarmupFailedException : Exception;

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in StreamReplay.");
    }

    private sealed class ScriptedModelHandler(Report report, bool claude, bool refuse, bool partitioned, Options options) : HttpMessageHandler
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
            var miscounted = partitioned && translations.Length > 1 && translations.Length < PartitionSize;
            var count = miscounted ? 1 : translations.Length;
            for (var k = 0; k < count; k++)
            {
                var due = options.FirstLineMs + options.LineMs * k;
                var wait = due - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
                var element = JsonSerializer.Serialize(translations[k], Relaxed);
                var piece = (k == 0 ? "{\"translations\": [" : ", ") + element + (k == count - 1 && !miscounted ? "]}" : string.Empty);
                var half = piece.Length / 2;
                Delta(pipe, piece[..half]);
                Delta(pipe, piece[half..]);
                report.ProviderLine(requestId, k, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            if (miscounted)
            {
                await Task.Delay(options.LineMs, cancellationToken).ConfigureAwait(false);
                Delta(pipe, "]}");
                report.Event(new { type = "provider_miscount", id = requestId, lines = translations.Length, returned = count });
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
        private readonly bool _partition;
        private readonly bool _local;
        private readonly Dictionary<int, double> _lineFirstShown = [];
        private readonly int[] _lineDropsBeforeFinal = new int[Lines.Length];
        private HashSet<int> _shownLines = [];
        private readonly Dictionary<string, string> _firstProvisional = new(StringComparer.Ordinal);
        private List<LiveDisplayBlock> _lastBlocks = [];
        private double? _finalAt;
        private int _miscountStatus;

        public int WarmupRequests { get; set; }

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
            _partition = scenario == "stream-partition";
            _local = options.Local;
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new
            {
                type = "header", scenario, provider = options.Provider, streaming = options.Streaming,
                firstLineMs = _local ? (int?)null : options.FirstLineMs, lineMs = _local ? (int?)null : options.LineMs,
                lines = Lines.Length, ocrMode = "windows", cache = "fresh-memory", privateMode = true,
                network = _local ? "loopback model server only (127.0.0.1, localhost, [::1]), no API key" : "scripted local handler only",
                modelMeaning = _local
                    ? "Real local OpenAI-compatible server; one warm-up request (not measured) before the scene"
                    : "Scripted handler: element k of the answer completes firstLineMs + k*lineMs after the request; without stream the whole answer arrives at the last element's time",
                partitionMeaning = _partition
                    ? $"Provider batches of {PartitionSize} lines; the scripted model returns 1 element for the 2-line batch, then the provider translates it line by line"
                    : null,
                timingMeaning = "From WPF Rendering of the new lines to session callbacks; not physical overlay presentation latency",
            });
        }

        private static int LineIndex(string translation) =>
            Array.FindIndex(LineMarkers, marker => translation.Contains(marker, StringComparison.OrdinalIgnoreCase));

        private bool IsReplayBlock(LiveDisplayBlock block) =>
            _local ? !string.IsNullOrWhiteSpace(block.TranslatedText) : block.TranslatedText.StartsWith(Prefix, StringComparison.Ordinal);

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
                var elapsed = _clock.Elapsed.TotalMilliseconds;
                if (update.StatusLine.Contains("pomylił liczbę linii", StringComparison.Ordinal)) _miscountStatus++;
                if (update.Blocks is { } blocks)
                {
                    var replayBlocks = blocks.Where(IsReplayBlock).ToList();
                    var now = replayBlocks.ToDictionary(static b => b.Key, static b => b.TranslatedText, StringComparer.Ordinal);
                    foreach (var (key, text) in now)
                    {
                        if (_shown.TryGetValue(key, out var before) && before != text) _textChanges++;
                        if (provisional) _firstProvisional.TryAdd(key, text);
                    }
                    if (now.Count < _shown.Count && _startedAt is not null && _allBlocksAt is not null) _drops++;
                    _shown.Clear();
                    foreach (var (key, text) in now)
                    {
                        _shown[key] = text;
                        _everShown.Add(key);
                    }
                    _lastBlocks = replayBlocks;
                    var lines = now.Values.Select(LineIndex).Where(static i => i >= 0).ToHashSet();
                    if (_startedAt is not null)
                    {
                        if (_finalAt is null)
                        {
                            foreach (var line in _shownLines.Where(line => !lines.Contains(line))) _lineDropsBeforeFinal[line]++;
                        }
                        foreach (var line in lines) _lineFirstShown.TryAdd(line, elapsed);
                        if (update.Diagnostics is not null && now.Count > 0) _finalAt ??= elapsed;
                    }
                    _shownLines = lines;
                }
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

        public int Finish(int exitCode, string reason, UsageTracker usage, LatencyMonitor latency,
            IReadOnlyList<LocalModelObserver.ResponseSummary>? localResponses)
        {
            lock (_gate)
            {
                if (_partition) exitCode = FinishPartition(exitCode, ref reason);
                if (localResponses is not null) FinishLocal(localResponses.Skip(WarmupRequests).ToList());
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
                    providerRequests = _local ? (int?)null : _requests.Count,
                    streamedRequests = _local ? (int?)null : _requests.Count(static r => r.Stream),
                    firstRequestLines = _local ? (int?)null : first.Lines,
                    firstRequestAfterChangeMs = _requests.Count > 0 ? first.At - _startedAt : null,
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

        private int FinishPartition(int exitCode, ref string reason)
        {
            var first = Enumerable.Range(0, PartitionSize).ToArray();
            var second = Enumerable.Range(PartitionSize, Lines.Length - PartitionSize).ToArray();
            bool ShownEarly(int line) => _lineFirstShown.TryGetValue(line, out var at) && (_finalAt is null || at < _finalAt);
            var pattern = _requests.Select(static r => (r.Stream ? "stream:" : "plain:") + r.Lines).ToArray();
            var fixtureValid = pattern.SequenceEqual(["stream:3", "stream:2", "plain:1", "plain:1"])
                               && first.Any(ShownEarly) && second.Any(ShownEarly) && _finalAt is not null;
            var firstDrops = first.Sum(line => _lineDropsBeforeFinal[line]);
            var secondEarlyDropped = second.Where(ShownEarly).All(line => _lineDropsBeforeFinal[line] > 0);
            var allAtEnd = Enumerable.Range(0, Lines.Length).All(_shownLines.Contains);
            var expected = firstDrops == 0 && secondEarlyDropped && allAtEnd;
            Write(new
            {
                type = "partition_summary", fixtureValid, expectedBehavior = expected, requestPattern = pattern,
                firstPartitionLines = first.Length, firstPartitionEarlyShown = first.Count(ShownEarly),
                firstPartitionFirstShownMs = first.Where(_lineFirstShown.ContainsKey).Select(line => _lineFirstShown[line] - _startedAt).Min(),
                firstPartitionDropsBeforeFinal = firstDrops,
                secondPartitionEarlyShown = second.Count(ShownEarly),
                secondPartitionDropsBeforeFinal = second.Sum(line => _lineDropsBeforeFinal[line]),
                finalPublishedMs = _finalAt - _startedAt, allLinesShownAtEnd = allAtEnd, miscountStatusUpdates = _miscountStatus,
            });
            Console.WriteLine($"StreamReplay partition: fixtureValid={fixtureValid}, expectedBehavior={expected}, pierwsza partia zdjeta {firstDrops} razy.");
            if (exitCode == 0 && !fixtureValid)
            {
                reason = "partition_fixture_invalid";
                return 4;
            }
            return exitCode;
        }

        private void FinishLocal(IReadOnlyList<LocalModelObserver.ResponseSummary> responses)
        {
            var streamed = responses.Where(static r => r.Stream).ToList();
            var firstScene = responses.Count > 0 ? responses[0] : null;
            Write(new
            {
                type = "local_summary", requests = responses.Count, streamedRequests = streamed.Count,
                plainRequests = responses.Count - streamed.Count, failedRequests = responses.Count(static r => r.Status is < 200 or >= 300),
                textsRequested = responses.Sum(static r => r.Texts), translationsReturned = responses.Sum(static r => r.Returned),
                emptyTranslations = responses.Sum(static r => r.Empty), qualityFlagged = responses.Sum(static r => r.QualityFlagged),
                numbersChanged = responses.Sum(static r => r.NumbersChanged), untranslated = responses.Sum(static r => r.Untranslated),
                runaway = responses.Sum(static r => r.Runaway), responsesNotAcceptedByApp = responses.Count(static r => !r.AppAccepted),
                responsesWithMiscount = responses.Count(static r => r.Returned != r.Texts),
                firstRequestStream = firstScene?.Stream, firstRequestTexts = firstScene?.Texts, firstRequestHeadersMs = firstScene?.HeadersMs,
                firstRequestFirstContentMs = firstScene?.FirstContentMs, firstRequestFirstLineMs = firstScene?.FirstLineMs,
                firstRequestTotalMs = firstScene?.TotalMs, firstRequestReasoningChars = firstScene?.ReasoningChars,
                firstRequestUsage = firstScene?.Usage, firstRequestDoneMarker = firstScene?.DoneMarker,
                firstRequestFinishReason = firstScene?.FinishReason, firstRequestMediaType = firstScene?.MediaType,
                lineResults = _lastBlocks.OrderBy(static b => b.ScreenBox.Y).Select((b, i) => new
                {
                    line = i, source = i < Lines.Length ? Lines[i] : null, translation = b.TranslatedText,
                    provisional = _firstProvisional.TryGetValue(b.Key, out var early) ? early : null,
                    changedAfterProvisional = _firstProvisional.TryGetValue(b.Key, out var earlier) && earlier != b.TranslatedText,
                    qualityIssues = b.QualityIssues.ToString(), b.SameAsSource,
                }),
            });
        }

        public void Dispose() => _writer.Dispose();
    }
}
