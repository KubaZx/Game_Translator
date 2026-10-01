using System.Text.Json;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class ClaudeTranslationProviderTests
{
    private static ClaudeTranslationProvider CreateProvider(FakeHttpHandler handler, string? apiKey = "sk-ant-test", string? model = null) =>
        new(new HttpClient(handler), () => apiKey, () => model, new LlmProviderOptions { MaxRetries = 0 });

    private static HttpResponseMessage MessageResponse(string text, string stopReason = "end_turn", string model = "claude-opus-5-5") =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model,
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 5 },
        }));

    /// <summary>Odpowiada „PL:tekst” dla każdego tekstu z wiadomości użytkownika.</summary>
    private static HttpResponseMessage Echo(CapturedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        var user = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        var userText = user.ValueKind == JsonValueKind.String ? user.GetString()! : user[0].GetProperty("text").GetString()!;
        using var payload = JsonDocument.Parse(userText[(userText.IndexOf('\n') + 1)..]);
        var translations = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => "PL:" + t.GetString()).ToArray();
        return MessageResponse(JsonSerializer.Serialize(new { translations }));
    }

    [Fact]
    public async Task Domyslnie_uzywa_Opus_5_5_z_fallbackiem_schematem_JSON_i_niskim_effort()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["Hello", "World"], "en", "pl");

        Assert.Equal(["PL:Hello", "PL:World"], result);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.anthropic.com/v1/messages", request.Url);
        Assert.Equal("sk-ant-test", request.Header("x-api-key"));
        Assert.Contains("server-side-fallback-2026-07-01", request.Header("anthropic-beta"));

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(ClaudeTranslationProvider.DefaultModel, root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task Model_bez_obslugi_effort_i_fallbacku_dostaje_proste_zapytanie()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler, model: "claude-haiku-4-5");

        await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("fallbacks", out _));
        Assert.False(root.GetProperty("output_config").TryGetProperty("effort", out _));
        Assert.Null(request.Header("anthropic-beta"));
    }

    [Fact]
    public async Task Terminy_slownika_trafiaja_do_promptu_systemowego()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);

        await provider.TranslateWithContextAsync(["Max Energy Shield"], "en", "pl",
            new TranslationContext(null, [new GlossaryTerm("Energy Shield", "Tarcza energetyczna")]));

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var system = body.RootElement.GetProperty("system");
        var systemText = system.ValueKind == JsonValueKind.String ? system.GetString() : system[0].GetProperty("text").GetString();
        Assert.Contains("Energy Shield => Tarcza energetyczna", systemText);
    }

    [Fact]
    public async Task Pamiec_dialogu_i_plec_gracza_trafiaja_do_zapytania()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);
        var context = new TranslationContext(null, [])
        {
            RecentTexts = ["Are you ready?"],
            RecentExchanges = [new RecentExchange("Are you ready?", "Jesteś gotowa?")],
            PlayerGender = PlayerGender.Female,
        };

        var result = await provider.TranslateWithContextAsync(["Let's go."], "en", "pl", context);

        Assert.Equal(["PL:Let's go."], result);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var system = body.RootElement.GetProperty("system");
        var systemText = system.ValueKind == JsonValueKind.String ? system.GetString() : system[0].GetProperty("text").GetString();
        Assert.Contains("player character is female", systemText);
        var user = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        var userText = user.ValueKind == JsonValueKind.String ? user.GetString()! : user[0].GetProperty("text").GetString()!;
        using var payload = JsonDocument.Parse(userText[(userText.IndexOf('\n') + 1)..]);
        var previous = Assert.Single(payload.RootElement.GetProperty("previous").EnumerateArray());
        Assert.Equal("Are you ready?", previous.GetProperty("source").GetString());
        Assert.Equal("Jesteś gotowa?", previous.GetProperty("translation").GetString());
    }

    [Fact]
    public async Task Kolejne_zapytania_dzialaja_na_tym_samym_kliencie()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);

        await provider.TranslateBatchAsync(["One"], "en", "pl");
        var second = await provider.TranslateBatchAsync(["Two"], "en", "pl");

        Assert.Equal(["PL:Two"], second);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Odmowa_mapuje_sie_na_ContentRefused()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(MessageResponse("", stopReason: "refusal")));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.ContentRefused, ex.Kind);
    }

    [Theory]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", TranslationFailureKind.InvalidApiKey)]
    [InlineData(404, """{"type":"error","error":{"type":"not_found_error","message":"model: claude-nope"}}""", TranslationFailureKind.ModelNotFound)]
    [InlineData(429, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", TranslationFailureKind.RateLimited)]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", TranslationFailureKind.ServiceUnavailable)]
    [InlineData(400, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""", TranslationFailureKind.QuotaExceeded)]
    public async Task Bledy_API_mapuja_sie_na_zrozumiale_rodzaje(int status, string body, TranslationFailureKind expected)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Status(status, body)));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Zmienne_srodowiskowe_SDK_nie_przekierowuja_klucza_pod_inny_adres()
    {
        var previousUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        var previousToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "http://evil.example.com");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "obcy-token");
            var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
            var provider = CreateProvider(handler);

            await provider.TranslateBatchAsync(["Hello"], "en", "pl");

            var request = Assert.Single(handler.Requests);
            Assert.StartsWith("https://api.anthropic.com/", request.Url);
            Assert.Null(request.Header("Authorization"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", previousUrl);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", previousToken);
        }
    }

    [Theory]
    [InlineData("<html><body>Zaloguj się do sieci Wi-Fi</body></html>")]
    [InlineData("""{"id":"msg_x","type":"message"}""")]
    public async Task Odpowiedz_portalu_lub_niepelna_to_czytelny_blad_a_nie_surowy_wyjatek(string body)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
    }

    [Fact]
    public async Task Brak_klucza_rzuca_MissingApiKey_bez_zapytania()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.MissingApiKey, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TestConnection_sprawdza_model_bez_generowania_tekstu()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Json("""
            {"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus 5.5","created_at":"2026-01-01T00:00:00Z"}
            """)));
        var provider = CreateProvider(handler);

        var status = await provider.TestConnectionAsync();

        Assert.True(status.IsOk, status.Message);
        Assert.Contains("Claude Opus 5.5", status.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/v1/models/claude-opus-5-5", request.Url);
    }

    [Fact]
    public async Task TestConnection_z_nieznanym_modelem_zwraca_czytelny_komunikat()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(
            FakeHttpHandler.Status(404, """{"type":"error","error":{"type":"not_found_error","message":"model: claude-nope"}}""")));
        var provider = CreateProvider(handler, model: "claude-nope");

        var status = await provider.TestConnectionAsync();

        Assert.False(status.IsOk);
        Assert.Contains("model", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("claude-opus-5-5", true, true)]
    [InlineData("claude-sonnet-5-5", true, true)]
    [InlineData("claude-opus-4-8", false, true)]
    [InlineData("claude-haiku-4-5", false, false)]
    public void Cechy_modeli(string model, bool fallbacks, bool effort)
    {
        Assert.Equal(fallbacks, ClaudeTranslationProvider.UsesDefaultFallbacks(model));
        Assert.Equal(effort, ClaudeTranslationProvider.SupportsEffort(model));
    }
}
