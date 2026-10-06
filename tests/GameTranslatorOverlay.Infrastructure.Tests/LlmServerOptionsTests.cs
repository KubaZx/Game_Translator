using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class LlmServerOptionsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static OpenAiCompatibleTranslationProvider CreateProvider(
        FakeHttpHandler handler,
        string endpoint = "https://api.deepseek.com/v1",
        LlmServerOptions? serverOptions = null,
        ILogger<OpenAiCompatibleTranslationProvider>? logger = null) =>
        new(new HttpClient(handler), () => "sk-test", () => HostOf(endpoint), () => endpoint, () => "deepseek-flash",
            new LlmProviderOptions { MaxRetries = 0 }, logger, () => serverOptions);

    private static string? HostOf(string endpoint) =>
        LlmEndpoint.TryNormalize(endpoint, out var uri, out _) ? uri!.Authority : null;

    private static string ChatContent(string content, object? usage = null) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage,
    });

    private static HttpResponseMessage Echo(CapturedRequest request, object? usage = null)
    {
        using var body = JsonDocument.Parse(request.Body);
        var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
        var translations = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => "PL:" + t.GetString()).ToArray();
        return FakeHttpHandler.Json(ChatContent(JsonSerializer.Serialize(new { translations }), usage));
    }

    private static List<string> RootProperties(CapturedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        return body.RootElement.EnumerateObject().Select(static p => p.Name).ToList();
    }

    [Fact]
    public async Task Bez_opcji_serwera_zapytanie_ma_dokladnie_pola_jak_dotad()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));

        await CreateProvider(handler, serverOptions: null).TranslateBatchAsync(["Hello"], "en", "pl");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(["model", "messages", "stream"], RootProperties(request));
        Assert.StartsWith("{\"model\":\"deepseek-flash\",\"messages\":[{\"role\":\"system\",\"content\":", request.Body);
        Assert.EndsWith("}],\"stream\":false}", request.Body);
        using var body = JsonDocument.Parse(request.Body);
        var messages = body.RootElement.GetProperty("messages");
        var headShape = System.Net.Http.Json.JsonContent.Create(new
        {
            model = "deepseek-flash",
            messages = new[]
            {
                new { role = "system", content = messages[0].GetProperty("content").GetString() },
                new { role = "user", content = messages[1].GetProperty("content").GetString() },
            },
            stream = false,
        });
        Assert.Equal(await headShape.ReadAsStringAsync(), request.Body);
    }

    [Fact]
    public async Task Opcje_dla_tego_serwera_trafiaja_do_zapytania()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var options = new LlmServerOptions
        {
            Host = "api.deepseek.com",
            Thinking = "disabled",
            ReasoningEffort = "none",
            MaxTokens = 2048,
            ResponseFormat = "json_object",
        };

        await CreateProvider(handler, serverOptions: options).TranslateBatchAsync(["Hello"], "en", "pl");

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var root = body.RootElement;
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Opcje_zapisane_dla_innego_serwera_nie_sa_wysylane()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var options = new LlmServerOptions { Host = "api.deepseek.com", Thinking = "disabled" };

        var provider = CreateProvider(handler, endpoint: "https://api.openai.com/v1", serverOptions: options);
        await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.Equal(["model", "messages", "stream"], RootProperties(Assert.Single(handler.Requests)));
        Assert.Null(provider.ActiveServerOptions());
    }

    [Theory]
    [InlineData(null, "disabled")]
    [InlineData("", "disabled")]
    [InlineData("api.deepseek.com", "dis abled")]
    [InlineData("api.deepseek.com", "{\"type\":1}")]
    [InlineData("api.deepseek.com", "")]
    public async Task Opcje_bez_hosta_albo_z_niepoprawna_wartoscia_nie_sa_wysylane(string? host, string thinking)
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var options = new LlmServerOptions { Host = host, Thinking = thinking, MaxTokens = 0 };

        await CreateProvider(handler, serverOptions: options).TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.Equal(["model", "messages", "stream"], RootProperties(Assert.Single(handler.Requests)));
    }

    [Fact]
    public async Task Usage_DeepSeek_jest_sumowany_i_logowany_bez_tresci()
    {
        var usage = new
        {
            prompt_tokens = 120,
            completion_tokens = 30,
            prompt_cache_hit_tokens = 64,
            prompt_cache_miss_tokens = 56,
            completion_tokens_details = new { reasoning_tokens = 7 },
        };
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(Echo(request, usage)));
        var logger = new CapturingLogger<OpenAiCompatibleTranslationProvider>();
        var provider = CreateProvider(handler, logger: logger);

        await provider.TranslateBatchAsync(["Secret line"], "en", "pl");
        await provider.TranslateBatchAsync(["Another line"], "en", "pl");

        Assert.Equal(2, provider.Usage.Requests);
        Assert.Equal(2, provider.Usage.RequestsWithUsage);
        Assert.Equal(240, provider.Usage.PromptTokens);
        Assert.Equal(60, provider.Usage.CompletionTokens);
        Assert.Equal(14, provider.Usage.ReasoningTokens);
        Assert.Equal(128, provider.Usage.CachedPromptTokens);
        var line = Assert.Single(logger.Messages.Distinct());
        Assert.Contains("120", line);
        Assert.Contains("64", line);
        Assert.Contains("7", line);
        Assert.DoesNotContain("Secret", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task Usage_OpenAI_z_cached_tokens_i_brak_usage_sa_obslugiwane()
    {
        var withUsage = new
        {
            prompt_tokens = 50,
            completion_tokens = 10,
            prompt_tokens_details = new { cached_tokens = 32 },
        };
        var handler = new FakeHttpHandler((request, attempt) => Task.FromResult(attempt == 0 ? Echo(request, withUsage) : Echo(request)));
        var provider = CreateProvider(handler, endpoint: "https://api.openai.com/v1");

        await provider.TranslateBatchAsync(["One"], "en", "pl");
        await provider.TranslateBatchAsync(["Two"], "en", "pl");

        Assert.Equal(2, provider.Usage.Requests);
        Assert.Equal(1, provider.Usage.RequestsWithUsage);
        Assert.Equal(32, provider.Usage.CachedPromptTokens);
        Assert.Equal(0, provider.Usage.ReasoningTokens);
    }

    [Fact]
    public async Task Test_polaczenia_pokazuje_aktywne_opcje_serwera()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler, serverOptions: new LlmServerOptions { Host = "api.deepseek.com", Thinking = "disabled" });

        var status = await provider.TestConnectionAsync();

        Assert.True(status.IsOk);
        Assert.Contains("opcje serwera: thinking=disabled", status.Message);
        Assert.Equal("thinking=disabled", provider.ActiveServerOptions()!.Describe());
    }

    [Fact]
    public void Opis_i_czyszczenie_opcji()
    {
        var options = new LlmServerOptions { Host = " api.deepseek.com ", Thinking = " disabled ", MaxTokens = -5, ResponseFormat = "json_object" };

        var clean = options.Sanitized();

        Assert.Equal("api.deepseek.com", clean.Host);
        Assert.Equal("disabled", clean.Thinking);
        Assert.Null(clean.MaxTokens);
        Assert.Equal("thinking=disabled, response_format=json_object", options.Describe());
        Assert.Equal("brak", new LlmServerOptions().Describe());
        Assert.False(new LlmServerOptions { Host = "x" }.HasRequestFields);
        Assert.True(new LlmServerOptions { MaxTokens = 10 }.HasRequestFields);
        Assert.Equal("reasoning_effort=none, max_tokens=10", new LlmServerOptions { ReasoningEffort = "none", MaxTokens = 10 }.Describe());
        Assert.True(LlmServerOptions.IsValidValue("high"));
        Assert.False(LlmServerOptions.IsValidValue(new string('a', LlmServerOptions.MaxValueLength + 1)));
    }

    [Fact]
    public void Presety_DeepSeek_i_Ollama_maja_opcje_przypisane_do_swojego_hosta()
    {
        var deepSeek = Assert.Single(TranslationProviderCatalog.LlmPresets, static p => p.Name == "DeepSeek");
        Assert.Equal("https://api.deepseek.com/v1", deepSeek.Endpoint);
        Assert.Equal("deepseek-flash", deepSeek.Model);
        Assert.Equal("api.deepseek.com", deepSeek.Host);
        var deepSeekOptions = deepSeek.ServerOptionsForHost()!;
        Assert.Equal("disabled", deepSeekOptions.Thinking);
        Assert.Equal("api.deepseek.com", deepSeekOptions.Host);

        var ollama = Assert.Single(TranslationProviderCatalog.LlmPresets, static p => p.Name == "Ollama");
        Assert.Equal("none", ollama.ServerOptionsForHost()!.ReasoningEffort);
        Assert.Equal("localhost:11434", ollama.ServerOptionsForHost()!.Host);

        var openAi = Assert.Single(TranslationProviderCatalog.LlmPresets, static p => p.Name == "OpenAI");
        Assert.Null(openAi.ServerOptionsForHost());
        Assert.Null(openAi.Model);

        Assert.Same(deepSeek, TranslationProviderCatalog.FindLlmPreset(new Uri("https://API.deepseek.com/v1/")));
        Assert.Null(TranslationProviderCatalog.FindLlmPreset(new Uri("https://openrouter.ai/api/v1/")));
    }

    [Fact]
    public void Opcje_serwera_przetrwaja_zapis_ustawien_a_stary_plik_ich_nie_ma()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);
        store.Save(new AppSettings { LlmServerOptions = new LlmServerOptions { Host = "api.deepseek.com", Thinking = "disabled" } });

        var loaded = new JsonSettingsStore(paths).Load();

        Assert.Equal("api.deepseek.com", loaded.LlmServerOptions!.Host);
        Assert.Equal("disabled", loaded.LlmServerOptions.Thinking);
        Assert.Contains("\"llmServerOptions\"", File.ReadAllText(paths.SettingsPath));

        File.WriteAllText(paths.SettingsPath, """{ "provider": "LLM" }""");
        Assert.Null(new JsonSettingsStore(paths).Load().LlmServerOptions);
    }

    [Fact]
    public void Zmiana_opcji_serwera_przebudowuje_pipeline()
    {
        var settings = new AppSettings();
        var before = settings.PipelineSnapshot();

        settings.LlmServerOptions = new LlmServerOptions { Host = "api.deepseek.com", Thinking = "disabled" };

        Assert.NotEqual(before, settings.PipelineSnapshot());
    }

    [Fact]
    public void Prompt_bez_sceny_i_notatek_jest_taki_jak_dotad()
    {
        var context = new TranslationContext("Escape Academy", []);

        var withEmptyNotes = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", context with { TextNotes = [null, " "] });

        Assert.Equal(LlmTranslationPrompt.BuildSystemPrompt("en", "pl", context), withEmptyNotes);
        Assert.DoesNotContain("\"notes\"", withEmptyNotes);
        Assert.DoesNotContain("Scene:", withEmptyNotes);
        Assert.Equal(LlmTranslationPrompt.BuildUserMessage(["A", "B"]), LlmTranslationPrompt.BuildUserMessage(["A", "B"], null, [null, ""]));
        Assert.Equal(LlmTranslationPrompt.BuildUserMessage(["A"]), LlmTranslationPrompt.BuildUserMessage(["A"], null, ["za dużo", "notatek"]));
    }

    [Fact]
    public void Scena_i_notatki_trafiaja_do_promptu()
    {
        var context = new TranslationContext(null, [])
        {
            Scene = "Dialogue \"Intro\";\nspeakers: Ann",
            TextNotes = ["speaker: Ann", null],
        };

        var system = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", context);
        var user = LlmTranslationPrompt.BuildUserMessage(["Hi.", "Bye."], [new RecentExchange("Yo.", "Siema.")], context.TextNotes);

        Assert.Contains("Scene: Dialogue \"Intro\"; speakers: Ann", system);
        Assert.Contains("\"notes\", when present", system);
        using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
        Assert.Equal(["speaker: Ann", ""], payload.RootElement.GetProperty("notes").EnumerateArray().Select(static n => n.GetString()));
        Assert.Equal("Yo.", payload.RootElement.GetProperty("previous")[0].GetProperty("source").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("texts").GetArrayLength());
    }

    [Fact]
    public async Task Notatki_sa_dzielone_razem_z_partiami_i_pojedynczym_fallbackiem()
    {
        var handler = new FakeHttpHandler(static (request, attempt) => Task.FromResult(attempt == 1
            ? FakeHttpHandler.Json(ChatContent("""{"translations": ["tylko jedno"]}"""))
            : Echo(request)));
        var provider = new OpenAiCompatibleTranslationProvider(new HttpClient(handler), () => null, () => null,
            () => "http://localhost:11434/v1", () => "qwen", new LlmProviderOptions { MaxRetries = 0, MaxBatchSize = 2 });
        var context = new TranslationContext(null, []) { TextNotes = ["n1", "n2", "n3", null] };

        var result = await provider.TranslateWithContextAsync(["a", "b", "c", "d"], "en", "pl", context);

        Assert.Equal(["PL:a", "PL:b", "PL:c", "PL:d"], result);
        var notes = handler.Requests.Select(static r =>
        {
            using var body = JsonDocument.Parse(r.Body);
            var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            return payload.RootElement.TryGetProperty("notes", out var n) ? string.Join(",", n.EnumerateArray().Select(static x => x.GetString())) : "-";
        }).ToList();
        Assert.Equal(["n1,n2", "n3,", "n3", "-"], notes);
    }

    [Fact]
    public void Kontekst_DeepL_ze_scena_ma_ja_w_pierwszym_wierszu()
    {
        Assert.Null(DeepLTranslationProvider.BuildContext(["Hi."], []));
        Assert.Equal("Dialogue \"Intro\"", DeepLTranslationProvider.BuildContext(["Hi."], [], "Dialogue \"Intro\""));
        Assert.Equal("Scene\nEarlier\nA\nB", DeepLTranslationProvider.BuildContext(["A", "B"], ["Earlier"], "Scene"));
        Assert.Equal(DeepLTranslationProvider.BuildContext(["A", "B"], ["Earlier"]), DeepLTranslationProvider.BuildContext(["A", "B"], ["Earlier"], "  "));
        var longScene = new string('s', 400);
        Assert.Equal(300, DeepLTranslationProvider.BuildContext(["Hi."], [], longScene)!.Length);
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _messages.Add(formatter(state, exception));
    }
}
