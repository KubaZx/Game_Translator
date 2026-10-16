using System.Collections.Concurrent;
using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class OpenAiCompatibleStreamingTests
{
    private static OpenAiCompatibleTranslationProvider CreateProvider(
        FakeHttpHandler handler, string endpoint = "https://api.openai.com/v1", TimeSpan? timeout = null,
        LlmServerOptions? serverOptions = null, int maxBatchSize = 25) =>
        new(new HttpClient(handler), () => "sk-test", () => new Uri(endpoint).Authority, () => endpoint, () => "gpt-test",
            new LlmProviderOptions { MaxRetries = 0, RequestTimeout = timeout ?? TimeSpan.FromSeconds(30), MaxBatchSize = maxBatchSize },
            serverOptionsAccessor: () => serverOptions);

    private static string Answer(params string[] translations) => JsonSerializer.Serialize(new { translations });

    private static IEnumerable<string> Chunks(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            yield return FakeSse.OpenAiChunk(text[offset..Math.Min(text.Length, offset + size)]);
        }
    }

    private static string Stream(string answer, int size, string finishReason = "stop") =>
        string.Concat(Chunks(answer, size)) + FakeSse.OpenAiChunk(null, finishReason) + FakeSse.OpenAiUsage(120, 30) + FakeSse.OpenAiDone;

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
    public async Task Strumien_oddaje_pierwsza_linie_przed_koncem_i_zachowuje_opcje_serwera()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = Answer("Mroczna Latarnia", "Zgaś światło");
        var cut = answer.IndexOf("Zga", StringComparison.Ordinal) - 2;
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            .. Chunks(answer[..cut], 3).Select(static c => FakeSse.Text(c)),
            FakeSse.Text(string.Concat(Chunks(answer[cut..], 3)) + FakeSse.OpenAiChunk(null, "stop") + FakeSse.OpenAiDone,
                waitFor: gate.Task),
        ])));
        var provider = CreateProvider(handler, serverOptions: new LlmServerOptions
        {
            Host = "api.openai.com", ReasoningEffort = "none", ResponseFormat = "json_object",
        });
        var collector = new Collector();

        var task = provider.TranslateStreamingAsync(["The Dark Lantern", "Put out the light"], "en", "pl",
            TranslationContext.Empty, collector.Add);
        await collector.First.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(task.IsCompleted);
        Assert.Equal(new (int, string)[] { (0, "Mroczna Latarnia") }, collector.Items);
        gate.SetResult();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Mroczna Latarnia", "Zgaś światło"], result);
        Assert.Equal(new (int, string)[] { (0, "Mroczna Latarnia"), (1, "Zgaś światło") }, collector.Items);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Url);
        Assert.Equal("Bearer sk-test", request.Header("Authorization"));
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(11)]
    public async Task Bajty_ciete_w_srodku_znakow_UTF8_i_sekwencji_ucieczki_daja_ten_sam_wynik(int bytesPerPiece)
    {
        var raw = """{"translations": ["Zażółć \"gęślą\" jaźń", "Linia\\nnie\nłamana ą 🏮", "Tab\tkoniec"]}""";
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(FakeSse.Split(Stream(raw, 4), bytesPerPiece))));
        var collector = new Collector();

        var result = await CreateProvider(handler).TranslateStreamingAsync(["A", "B", "C"], "en", "pl",
            TranslationContext.Empty, collector.Add);

        var expected = LlmTranslationPrompt.ParseTranslations(raw, 3)!;
        Assert.Equal(expected, result);
        Assert.Equal(expected.Select(static (t, i) => (i, t)), collector.Items);
        Assert.Contains("ą 🏮", expected[1]);
    }

    [Fact]
    public async Task Wynik_strumienia_jest_taki_sam_jak_bez_strumienia_i_liczy_tokeny()
    {
        var answer = Answer("Jeden", "Dwa — trzy", "50% obrażeń");
        var streaming = CreateProvider(new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response([FakeSse.Text(Stream(answer, 5))]))));
        var plain = CreateProvider(new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = answer }, finish_reason = "stop" } },
        })))));

        var streamed = await streaming.TranslateStreamingAsync(["A", "B", "C"], "en", "pl", TranslationContext.Empty, static (_, _) => { });
        var whole = await plain.TranslateWithContextAsync(["A", "B", "C"], "en", "pl", TranslationContext.Empty);

        Assert.Equal(whole, streamed);
        Assert.Equal(1, streaming.Usage.Requests);
        Assert.Equal(1, streaming.Usage.RequestsWithUsage);
        Assert.Equal(120, streaming.Usage.PromptTokens);
        Assert.Equal(30, streaming.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Filtr_tresci_na_koncu_strumienia_to_odmowa()
    {
        var answer = Answer("Raz", "Dwa");
        var partial = answer[..(answer.IndexOf("Dwa", StringComparison.Ordinal) + 1)];
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
            [FakeSse.Text(string.Concat(Chunks(partial, 4)) + FakeSse.OpenAiChunk(null, "content_filter") + FakeSse.OpenAiDone)])));
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
                FakeSse.Text(string.Concat(Chunks("""{"translations": ["Raz", """, 6))),
                FakeSse.Text(FakeSse.OpenAiChunk("\"Dwa\"]}"), waitFor: never.Task),
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
    public async Task Zerwane_polaczenie_w_trakcie_strumienia_to_blad_sieci()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(string.Concat(Chunks("""{"translations": ["Raz", "Dw""", 6))),
            FakeSse.Fail(new IOException("connection reset")),
        ])));
        var collector = new Collector();

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty, collector.Add));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
        Assert.Equal(new (int, string)[] { (0, "Raz") }, collector.Items);
    }

    [Fact]
    public async Task Blad_zgloszony_w_strumieniu_przerywa_tlumaczenie()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(string.Concat(Chunks("""{"translations": ["Raz", """, 6))),
            FakeSse.Text("""data: {"error":{"message":"upstream overloaded","code":503}}""" + "\n\n"),
        ])));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty, static (_, _) => { }));

        Assert.Equal(TranslationFailureKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task Blad_HTTP_przed_strumieniem_mapuje_sie_jak_dotad()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(
            FakeHttpHandler.Status(429, """{"error":{"type":"insufficient_quota","code":"insufficient_quota"}}""")));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { }));

        Assert.Equal(TranslationFailureKind.QuotaExceeded, ex.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Zawieszony_strumien_konczy_sie_przekroczeniem_czasu()
    {
        var never = new TaskCompletionSource();
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
        [
            FakeSse.Text(FakeSse.OpenAiChunk("{\"translations\": [\"Ra")),
            FakeSse.Text(FakeSse.OpenAiChunk("z\"]}"), waitFor: never.Task),
        ])));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler, timeout: TimeSpan.FromMilliseconds(300))
            .TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { })
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(TranslationFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Serwer_ignorujacy_strumien_odpowiada_zwyklym_JSON_em()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = Answer("Raz") }, finish_reason = "stop" } },
        }))));

        var result = await CreateProvider(handler, endpoint: "http://localhost:11434/v1")
            .TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { });

        Assert.Equal(["Raz"], result);
    }

    [Fact]
    public async Task Serwer_odrzucajacy_strumien_dostaje_zwykle_zapytanie_i_jest_zapamietany()
    {
        var handler = new FakeHttpHandler((request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            if (body.RootElement.GetProperty("stream").GetBoolean())
                return Task.FromResult(FakeHttpHandler.Status(400, """{"error":{"message":"Unrecognized request argument: stream_options"}}"""));
            return Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = Answer("Raz") }, finish_reason = "stop" } },
            })));
        });
        var provider = CreateProvider(handler, endpoint: "http://localhost:1234/v1");

        var first = await provider.TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { });
        var second = await provider.TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { });

        Assert.Equal(["Raz"], first);
        Assert.Equal(["Raz"], second);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Zla_liczba_elementow_w_strumieniu_konczy_sie_tlumaczeniem_pojedynczym()
    {
        var handler = new FakeHttpHandler((request, attempt) =>
        {
            if (attempt == 0) return Task.FromResult(FakeSse.Response([FakeSse.Text(Stream(Answer("tylko jedno"), 4))]));
            using var body = JsonDocument.Parse(request.Body);
            var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            var text = payload.RootElement.GetProperty("texts")[0].GetString();
            return Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = Answer("PL:" + text) }, finish_reason = "stop" } },
            })));
        });
        var collector = new Collector();

        var result = await CreateProvider(handler).TranslateStreamingAsync(["A", "B"], "en", "pl", TranslationContext.Empty, collector.Add);

        Assert.Equal(["PL:A", "PL:B"], result);
        Assert.Equal(new (int, string)[] { (0, "tylko jedno"), (PartialResults.Reset, string.Empty) }, collector.Items);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Indeksy_kolejnych_partii_sa_przesuniete_do_calej_listy()
    {
        var handler = new FakeHttpHandler((request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            var texts = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => "PL:" + t.GetString()).ToArray();
            return Task.FromResult(FakeSse.Response([FakeSse.Text(Stream(Answer(texts), 3))]));
        });
        var collector = new Collector();

        var result = await CreateProvider(handler, maxBatchSize: 2)
            .TranslateStreamingAsync(["A", "B", "C"], "en", "pl", TranslationContext.Empty, collector.Add);

        Assert.Equal(["PL:A", "PL:B", "PL:C"], result);
        Assert.Equal(new (int, string)[] { (0, "PL:A"), (1, "PL:B"), (2, "PL:C") }, collector.Items);
    }

    [Fact]
    public async Task Komentarze_SSE_i_dane_w_wielu_liniach_sa_obslugiwane()
    {
        var answer = Answer("Raz");
        var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } });
        var multiline = "data: " + chunk[..10] + "\ndata: " + chunk[10..] + "\n\n";
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
            [FakeSse.Text(": PROCESSING\n\nevent: chunk\n" + multiline + FakeSse.OpenAiDone)])));

        var result = await CreateProvider(handler).TranslateStreamingAsync(["One"], "en", "pl",
            TranslationContext.Empty, static (_, _) => { });

        Assert.Equal(["Raz"], result);
    }

    [Fact]
    public async Task Odpowiedz_portalu_w_strumieniu_to_czytelny_blad_sieci()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response(
            [FakeSse.Text("<html><body>Zaloguj się do sieci Wi-Fi</body></html>")], out _, mediaType: "text/html")));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => CreateProvider(handler)
            .TranslateStreamingAsync(["One"], "en", "pl", TranslationContext.Empty, static (_, _) => { }));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
    }

    [Fact]
    public async Task Odbiorca_rzucajacy_wyjatkiem_nie_psuje_odpowiedzi()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeSse.Response([FakeSse.Text(Stream(Answer("Raz", "Dwa"), 3))])));
        var calls = 0;

        var result = await CreateProvider(handler).TranslateStreamingAsync(["One", "Two"], "en", "pl", TranslationContext.Empty,
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("odbiorca");
            });

        Assert.Equal(["Raz", "Dwa"], result);
        Assert.Equal(1, calls);
    }
}
