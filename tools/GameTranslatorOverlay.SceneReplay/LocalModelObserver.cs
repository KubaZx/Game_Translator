using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

internal sealed partial class LocalModelObserver(Action<object> write)
{
    private const int MaxTranslationChars = 200;
    private static readonly string[] TranslationKeys = ["translations", "Translations", "translation", "result"];

    private readonly object _gate = new();
    private readonly List<ResponseSummary> _responses = [];

    public sealed record ResponseSummary(
        int Id, bool Stream, int Status, string? MediaType, int Texts, double HeadersMs, double? FirstByteMs, double? FirstContentMs,
        double? FirstLineMs, double TotalMs, int Events, bool DoneMarker, bool Usage, string? FinishReason, int ReasoningChars,
        int Returned, int Empty, int QualityFlagged, int NumbersChanged, int Untranslated, int Runaway, bool AppAccepted);

    public IReadOnlyList<ResponseSummary> Responses
    {
        get { lock (_gate) return [.. _responses]; }
    }

    public HttpMessageHandler Wrap(HttpMessageHandler inner) => new ObservingHandler(this, inner);

    private int _nextId = -1;

    private int NextId() => Interlocked.Increment(ref _nextId);

    private void Complete(ResponseSummary summary, IReadOnlyList<string?> translations)
    {
        lock (_gate) _responses.Add(summary);
        write(new
        {
            type = "provider_response", summary.Id, summary.Stream, summary.Status, summary.MediaType, summary.Texts,
            summary.HeadersMs, summary.FirstByteMs, summary.FirstContentMs, summary.FirstLineMs, summary.TotalMs, summary.Events,
            summary.DoneMarker, summary.Usage, summary.FinishReason, summary.ReasoningChars, summary.Returned, summary.Empty,
            summary.QualityFlagged, summary.NumbersChanged, summary.Untranslated, summary.Runaway, summary.AppAccepted,
            translations = translations.Select(static t => t is null ? null : t.Length > MaxTranslationChars ? t[..MaxTranslationChars] : t),
        });
    }

    private sealed class ObservingHandler(LocalModelObserver observer, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var requestBody = new MemoryStream();
            if (request.Content is { } content) request.Content = new TeeRequestContent(content, requestBody);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var headersMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var exchange = new Exchange(observer, observer.NextId(), started, headersMs, requestBody, response);
            var original = response.Content;
            var body = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var observed = new StreamContent(new TeeResponseStream(body, original, exchange));
            foreach (var header in original.Headers) observed.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = observed;
            return response;
        }
    }

    private sealed class TeeRequestContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly MemoryStream _copy;

        public TeeRequestContent(HttpContent inner, MemoryStream copy)
        {
            _inner = inner;
            _copy = copy;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new MemoryStream();
            await _inner.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            _copy.SetLength(0);
            await buffer.CopyToAsync(_copy, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            await buffer.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Exchange(LocalModelObserver observer, int id, long started, double headersMs, MemoryStream requestBody, HttpResponseMessage response)
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        private readonly StringBuilder _raw = new();
        private readonly StringBuilder _content = new();
        private readonly bool _sse = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        private LlmTranslationStreamParser? _parser;
        private double? _firstByteMs, _firstContentMs, _firstLineMs;
        private int _events, _reasoningChars, _finished;
        private bool _done, _usage;
        private string? _finishReason;

        private double Elapsed => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;
            _firstByteMs ??= Elapsed;
            var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
            _decoder.GetChars(bytes, chars, flush: false);
            _raw.Append(chars);
            if (!_sse) return;
            foreach (var c in chars)
            {
                if (c != '\n')
                {
                    _line.Append(c);
                    continue;
                }
                Line(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
        }

        private void Line(string line)
        {
            if (line.Length == 0)
            {
                Dispatch();
                return;
            }
            if (line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal)) return;
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
        }

        private void Dispatch()
        {
            if (_data.Length == 0) return;
            var payload = _data.ToString().Trim();
            _data.Clear();
            if (payload == "[DONE]")
            {
                _done = true;
                return;
            }
            try
            {
                using var document = JsonDocument.Parse(payload);
                _events++;
                var root = document.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) _usage = true;
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) _finishReason = reason.GetString();
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return;
                foreach (var name in new[] { "reasoning_content", "reasoning" })
                {
                    if (delta.TryGetProperty(name, out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                        _reasoningChars += reasoning.GetString()!.Length;
                }
                if (delta.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String && piece.GetString() is { Length: > 0 } text)
                {
                    _firstContentMs ??= Elapsed;
                    _content.Append(text);
                    _parser ??= new LlmTranslationStreamParser(int.MaxValue, (_, _) => _firstLineMs ??= Elapsed);
                    _parser.Append(text);
                }
            }
            catch (JsonException)
            {
            }
        }

        public void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1) return;
            var totalMs = Elapsed;
            if (_sse)
            {
                if (_line.Length > 0) Line(_line.ToString().TrimEnd('\r'));
                Dispatch();
            }
            else
            {
                _content.Append(MessageContent(_raw.ToString()));
            }
            var request = Request(Encoding.UTF8.GetString(requestBody.ToArray()));
            var translations = Translations(_content.ToString());
            var flags = translations.Select((t, i) => t is null || i >= request.Texts.Count
                ? TranslationQualityFlags.None
                : TranslationQualityGate.Check(request.Texts[i], t)).ToList();
            var accepted = LlmTranslationPrompt.ParseTranslations(_content.ToString(), Math.Max(1, request.Texts.Count)) is not null;
            observer.Complete(new ResponseSummary(id, request.Stream, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString(),
                request.Texts.Count, headersMs, _firstByteMs, _firstContentMs, _firstLineMs, totalMs, _events, _done, _usage, _finishReason,
                _reasoningChars, translations.Count, translations.Count(static t => string.IsNullOrWhiteSpace(t)),
                flags.Count(static f => f is not (TranslationQualityFlags.None or TranslationQualityFlags.Empty)),
                flags.Count(static f => f.HasFlag(TranslationQualityFlags.NumbersChanged)),
                flags.Count(static f => f.HasFlag(TranslationQualityFlags.Untranslated)),
                flags.Count(static f => f.HasFlag(TranslationQualityFlags.Runaway)), accepted), translations);
        }
    }

    private sealed class TeeResponseStream(Stream inner, HttpContent original, Exchange exchange) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) exchange.Finish();
            else exchange.Bytes(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read == 0) exchange.Finish();
            else exchange.Bytes(buffer.AsSpan(offset, read));
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                exchange.Finish();
                inner.Dispose();
                original.Dispose();
            }
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

    private sealed record RequestInfo(bool Stream, IReadOnlyList<string> Texts);

    private static RequestInfo Request(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var stream = root.TryGetProperty("stream", out var flag) && flag.ValueKind == JsonValueKind.True;
            var messages = root.GetProperty("messages");
            var user = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? string.Empty;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            var texts = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => t.GetString() ?? string.Empty).ToList();
            return new RequestInfo(stream, texts);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return new RequestInfo(false, []);
        }
    }

    private static string MessageContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                   && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var message)
                   && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static List<string?> Translations(string content)
    {
        var cleaned = ThinkBlock().Replace(content, string.Empty).Trim();
        var fenced = CodeFence().Match(cleaned) is { Success: true } fence ? fence.Groups[1].Value : null;
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        foreach (var candidate in new[] { cleaned, fenced, start >= 0 && end > start ? cleaned[start..(end + 1)] : null })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                var array = root.ValueKind == JsonValueKind.Array ? root : default;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in TranslationKeys)
                    {
                        if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                        {
                            array = value;
                            break;
                        }
                    }
                }
                if (array.ValueKind != JsonValueKind.Array) continue;
                return array.EnumerateArray()
                    .Select(static item => item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Number => item.GetRawText(),
                        _ => null,
                    })
                    .ToList();
            }
            catch (JsonException)
            {
            }
        }
        return [];
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeFence();
}
