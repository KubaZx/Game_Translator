using System.Collections.Concurrent;
using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class ClaudeStreamingTests
{
    private static ClaudeTranslationProvider CreateProvider(FakeHttpHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), () => "sk-ant-test", () => null,
            new LlmProviderOptions { MaxRetries = 0, RequestTimeout = timeout ?? TimeSpan.FromSeconds(30) });

    private static IEnumerable<SsePiece> Deltas(int index, string text, int size, TimeSpan delay = default)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            yield return FakeSse.Text(FakeSse.AnthropicTextDelta(index, text[offset..Math.Min(text.Length, offset + size)]), delay);
        }
    }

    private static string Answer(params string[] translations) => JsonSerializer.Serialize(new { translations });

    private static List<SsePiece> Message(string answer, int size = 4, string stopReason = "end_turn") =>
    [
        FakeSse.Text(FakeSse.AnthropicStart()),
        FakeSse.Text(FakeSse.AnthropicTextStart(0)),
        .. Deltas(0, answer, size),
        FakeSse.Text(FakeSse.AnthropicBlockStop(0)),
        FakeSse.Text(FakeSse.AnthropicEnd(stopReason)),
    ];

    private sealed class Collector
    {
        private readonly ConcurrentQueue<(int Index, string Text)> _items = new();
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<(int Index, string Text)> Items => _items.ToList();

        public void Add(int index, string text)
        {
            _items.Enqueue((index, text));
            First.TrySetResult();
        }
    }

    [Fact]
    public async Task Strumien_oddaje_pierwsza_linie_zanim_skonczy_sie_odpowiedz()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = "Witaj, \"podróżniku\"! \\ ąę 😀";
        var second = "Mroczna Latarnia\nświeci\tdalej";
        var answer = Answer(first, second);
        var cut = answer.IndexOf("Mroczna", StringComparison.Ordinal) - 2;
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            .. Deltas(0, answer[..cut], 3),
            FakeSse.Text(FakeSse.AnthropicTextDelta(0, answer[cut..]), waitFor: gate.Task),
            FakeSse.Text(FakeSse.AnthropicBlockStop(0)),
            FakeSse.Text(FakeSse.AnthropicEnd()),
        ])));
        var provider = CreateProvider(handler);
        var collector = new Collector();

        var task = provider.TranslateStreamingAsync(["Hello, \"traveler\"!", "The Dark Lantern\nshines"], "en", "pl",
            TranslationContext.Empty, collector.Add);
        await collector.First.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(task.IsCompleted);
        Assert.Equal(new (int, string)[] { (0, first) }, collector.Items);
        gate.SetResult();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([first, second], result);
        Assert.Equal(new (int, string)[] { (0, first), (1, second) }, collector.Items);
        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("default", body.RootElement.GetProperty("fallbacks").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Contains("server-side-fallback-2026-07-01", request.Header("anthropic-beta"));
        Assert.Equal("sk-ant-test", request.Header("x-api-key"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(17)]
    public async Task Bajty_strumienia_ciete_w_dowolnym_miejscu_daja_ten_sam_wynik(int bytesPerPiece)
    {
        var answer = JsonSerializer.Serialize(
            new { translations = new[] { "Zażółć gęślą jaźń „cytat”", "Linia\\druga \"w cudzysłowie\"", "Emoji 🏮 latarnia" } },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var events = string.Concat(Message(answer, size: 2).Select(static p => System.Text.Encoding.UTF8.GetString(p.Bytes)));
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(FakeSse.Split(events, bytesPerPiece))));
        var provider = CreateProvider(handler);
        var collector = new Collector();

        var result = await provider.TranslateStreamingAsync(["A", "B", "C"], "en", "pl", TranslationContext.Empty, collector.Add);

        var expected = LlmTranslationPrompt.ParseTranslations(answer, 3)!;
        Assert.Equal(expected, result);
        Assert.Equal(expected.Select(static (t, i) => (i, t)), collector.Items);
    }

    [Fact]
    public async Task Wynik_strumienia_jest_taki_sam_jak_bez_strumienia()
    {
        var answer = Answer("Pierwsza", "Druga — z pauzą", "Trzecia 100%");
        var streaming = CreateProvider(new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(Message(answer, size: 7)))));
        var plain = CreateProvider(new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            id = "msg_test", type = "message", role = "assistant", model = "claude-opus-5-5",
            content = new[] { new { type = "text", text = answer } },
            stop_reason = "end_turn", stop_sequence = (string?)null, usage = new { input_tokens = 10, output_tokens = 5 },
        })))));

        var streamed = await streaming.TranslateStreamingAsync(["A", "B", "C"], "en", "pl", TranslationContext.Empty, static (_, _) => { });
        var whole = await plain.TranslateWithContextAsync(["A", "B", "C"], "en", "pl", TranslationContext.Empty);

        Assert.Equal(whole, streamed);
    }

    [Fact]
    public async Task Fallback_w_trakcie_odpowiedzi_konczy_wczesne_linie_a_wynik_skleja_kontynuacje()
    {
        var answer = Answer("Raz", "Dwa", "Trzy");
        var cut = answer.IndexOf("Dwa", StringComparison.Ordinal) + 1;
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            .. Deltas(0, answer[..cut], 4),
            FakeSse.Text(FakeSse.AnthropicBlockStop(0)),
            FakeSse.Text(FakeSse.AnthropicFallbackStart(1)),
            FakeSse.Text(FakeSse.AnthropicBlockStop(1)),
            FakeSse.Text(FakeSse.AnthropicTextStart(2)),
            .. Deltas(2, answer[cut..], 4),
            FakeSse.Text(FakeSse.AnthropicBlockStop(2)),
            FakeSse.Text(FakeSse.AnthropicEnd()),
        ])));
        var collector = new Collector();

        var result = await CreateProvider(handler).TranslateStreamingAsync(["One", "Two", "Three"], "en", "pl",
            TranslationContext.Empty, collector.Add);

        Assert.Equal(["Raz", "Dwa", "Trzy"], result);
        Assert.Equal(new (int, string)[] { (0, "Raz") }, collector.Items);
    }

    [Fact]
    public async Task Fallback_z_nowa_odpowiedzia_bierze_ostatni_blok_jak_bez_strumienia()
    {
        var refused = Answer("Raz", "Dwa");
        var cut = refused.IndexOf("Dwa", StringComparison.Ordinal);
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            .. Deltas(0, refused[..cut], 3),
            FakeSse.Text(FakeSse.AnthropicBlockStop(0)),
            FakeSse.Text(FakeSse.AnthropicFallbackStart(1)),
            FakeSse.Text(FakeSse.AnthropicBlockStop(1)),
            FakeSse.Text(FakeSse.AnthropicTextStart(2)),
            .. Deltas(2, Answer("Jeden", "Dwa"), 3),
            FakeSse.Text(FakeSse.AnthropicBlockStop(2)),
            FakeSse.Text(FakeSse.AnthropicEnd()),
        ])));
        var collector = new Collector();

        var result = await CreateProvider(handler).TranslateStreamingAsync(["One", "Two"], "en", "pl",
            TranslationContext.Empty, collector.Add);

        Assert.Equal(["Jeden", "Dwa"], result);
        Assert.Equal(new (int, string)[] { (0, "Raz") }, collector.Items);
    }

    [Fact]
    public async Task Fallback_przed_odpowiedzia_nie_zglasza_linii_wczesnie()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart("claude-opus-5")),
            FakeSse.Text(FakeSse.AnthropicFallbackStart(0)),
            FakeSse.Text(FakeSse.AnthropicBlockStop(0)),
            FakeSse.Text(FakeSse.AnthropicTextStart(1)),
            .. Deltas(1, Answer("Raz", "Dwa"), 5),
            FakeSse.Text(FakeSse.AnthropicBlockStop(1)),
            FakeSse.Text(FakeSse.AnthropicEnd()),
        ])));
        var collector = new Collector();

        var result = await CreateProvider(handler).TranslateStreamingAsync(["One", "Two"], "en", "pl",
            TranslationContext.Empty, collector.Add);

        Assert.Equal(["Raz", "Dwa"], result);
        Assert.Empty(collector.Items);
    }

    [Fact]
    public async Task Odmowa_na_koncu_strumienia_odrzuca_czesciowy_wynik()
    {
        var partial = Answer("Raz", "Dwa");
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            .. Deltas(0, partial[..(partial.IndexOf("Dwa", StringComparison.Ordinal) + 2)], 4),
            FakeSse.Text(FakeSse.AnthropicBlockStop(0)),
            FakeSse.Text(FakeSse.AnthropicEnd("refusal")),
        ])));
        var collector = new Collector();

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty, collector.Add));

        Assert.Equal(TranslationFailureKind.ContentRefused, ex.Kind);
        Assert.Equal(new (int, string)[] { (0, "Raz") }, collector.Items);
    }

    [Fact]
    public async Task Anulowanie_przerywa_strumien()
    {
        var never = new TaskCompletionSource();
        ScriptedStream? body = null;
        var handler = new FakeHttpHandler((_, _) =>
        {
            var response = FakeSse.Response(
            [
                FakeSse.Text(FakeSse.AnthropicStart()),
                FakeSse.Text(FakeSse.AnthropicTextStart(0)),
                .. Deltas(0, """{"translations": ["Raz", """, 50),
                FakeSse.Text(FakeSse.AnthropicTextDelta(0, "\"Dwa\"]}"), waitFor: never.Task),
            ], out var stream);
            body = stream;
            return Task.FromResult(response);
        });
        using var cts = new CancellationTokenSource();
        var collector = new Collector();

        var task = CreateProvider(handler).TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty,
            collector.Add, cts.Token);
        await collector.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(body!.Disposed);
    }

    [Fact]
    public async Task Blad_zgloszony_w_trakcie_strumienia_jest_czytelny()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            .. Deltas(0, """{"translations": ["Raz", "Dw""", 6),
            FakeSse.Text(FakeSse.AnthropicError()),
        ])));
        var collector = new Collector();

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty, collector.Add));

        Assert.Equal(TranslationFailureKind.ServiceUnavailable, ex.Kind);
        Assert.Equal(new (int, string)[] { (0, "Raz") }, collector.Items);
    }

    [Fact]
    public async Task Zerwane_polaczenie_w_trakcie_strumienia_to_blad_sieci()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            .. Deltas(0, """{"translations": ["Raz", "Dw""", 6),
            FakeSse.Fail(new IOException("connection reset")),
        ])));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty, static (_, _) => { }));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
    }

    [Theory]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", TranslationFailureKind.ServiceUnavailable)]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", TranslationFailureKind.InvalidApiKey)]
    public async Task Blad_HTTP_przed_strumieniem_mapuje_sie_jak_bez_strumienia(int status, string body, TranslationFailureKind expected)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Status(status, body)));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { }));

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Zawieszony_strumien_konczy_sie_przekroczeniem_czasu()
    {
        var never = new TaskCompletionSource();
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.AnthropicStart()),
            FakeSse.Text(FakeSse.AnthropicTextStart(0)),
            FakeSse.Text(FakeSse.AnthropicTextDelta(0, "{\"translations\": [\"Raz"), waitFor: never.Task),
        ])));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler, TimeSpan.FromMilliseconds(400))
            .TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { })
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(TranslationFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Bez_odbiorcy_zapytanie_nie_jest_strumieniowane()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            id = "msg_test", type = "message", role = "assistant", model = "claude-opus-5-5",
            content = new[] { new { type = "text", text = Answer("Raz") } },
            stop_reason = "end_turn", stop_sequence = (string?)null, usage = new { input_tokens = 10, output_tokens = 5 },
        }))));

        var result = await CreateProvider(handler).TranslateWithContextAsync(["One"], "en", "pl", TranslationContext.Empty);

        Assert.Equal(["Raz"], result);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.False(body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean());
    }
}
