using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GameTranslatorOverlay.Infrastructure.Tests;

internal sealed class ScriptedStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _offset;

    public bool Disposed { get; private set; }

    public void Push(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

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
        Disposed = true;
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

internal sealed record SsePiece(byte[] Bytes, TimeSpan Delay, Task? WaitFor = null, Exception? Failure = null);

internal static class FakeSse
{
    public static HttpResponseMessage Response(IEnumerable<SsePiece> pieces, out ScriptedStream stream, string mediaType = "text/event-stream")
    {
        var body = new ScriptedStream();
        stream = body;
        var list = pieces.ToList();
        _ = Task.Run(async () =>
        {
            foreach (var piece in list)
            {
                if (piece.WaitFor is { } gate) await gate.ConfigureAwait(false);
                if (piece.Delay > TimeSpan.Zero) await Task.Delay(piece.Delay).ConfigureAwait(false);
                if (piece.Failure is { } failure)
                {
                    body.Complete(failure);
                    return;
                }
                body.Push(piece.Bytes);
            }
            body.Complete();
        });
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Response(IEnumerable<SsePiece> pieces) => Response(pieces, out _);

    public static IEnumerable<SsePiece> Split(string text, int bytesPerPiece, TimeSpan delay = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var offset = 0; offset < bytes.Length; offset += bytesPerPiece)
        {
            yield return new SsePiece(bytes[offset..Math.Min(bytes.Length, offset + bytesPerPiece)], delay);
        }
    }

    public static SsePiece Text(string text, TimeSpan delay = default, Task? waitFor = null) =>
        new(Encoding.UTF8.GetBytes(text), delay, waitFor);

    public static SsePiece Fail(Exception failure, TimeSpan delay = default, Task? waitFor = null) =>
        new([], delay, waitFor, failure);

    public static string OpenAiChunk(string? content, string? finishReason = null) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion.chunk",
            choices = new[] { new { index = 0, delta = new { content }, finish_reason = finishReason } },
        }, Relaxed) + "\n\n";

    public static string OpenAiUsage(long prompt, long completion) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = completion },
        }, Relaxed) + "\n\n";

    public const string OpenAiDone = "data: [DONE]\n\n";

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string AnthropicEvent(string type, object data) =>
        $"event: {type}\ndata: {JsonSerializer.Serialize(data, Relaxed)}\n\n";

    public static string AnthropicStart(string model = "claude-opus-5-5") => AnthropicEvent("message_start", new
    {
        type = "message_start",
        message = new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model,
            content = Array.Empty<object>(),
            stop_reason = (string?)null,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 1 },
        },
    });

    public static string AnthropicTextStart(int index) => AnthropicEvent("content_block_start", new
    {
        type = "content_block_start",
        index,
        content_block = new { type = "text", text = "" },
    });

    public static string AnthropicFallbackStart(int index, string from = "claude-opus-5-5", string to = "claude-opus-5") =>
        AnthropicEvent("content_block_start", new
        {
            type = "content_block_start",
            index,
            content_block = new { type = "fallback", from = new { model = from }, to = new { model = to } },
        });

    public static string AnthropicTextDelta(int index, string text) => AnthropicEvent("content_block_delta", new
    {
        type = "content_block_delta",
        index,
        delta = new { type = "text_delta", text },
    });

    public static string AnthropicBlockStop(int index) => AnthropicEvent("content_block_stop", new
    {
        type = "content_block_stop",
        index,
    });

    public static string AnthropicEnd(string stopReason = "end_turn") =>
        AnthropicEvent("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 20 },
        })
        + AnthropicEvent("message_stop", new { type = "message_stop" });

    public static string AnthropicError(string errorType = "overloaded_error", string message = "Overloaded") =>
        AnthropicEvent("error", new { type = "error", error = new { type = errorType, message } });
}
