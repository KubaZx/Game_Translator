using System.Text.Json;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class OpenAiCompatibleTranslationProviderTests
{
    private static OpenAiCompatibleTranslationProvider CreateProvider(
        FakeHttpHandler handler,
        string? apiKey = "sk-test",
        string? endpoint = "https://api.openai.com/v1",
        string? model = "gpt-test",
        string? keyHost = null) =>
        new(new HttpClient(handler), () => apiKey, () => keyHost ?? HostOf(endpoint), () => endpoint, () => model,
            new LlmProviderOptions { MaxRetries = 0 });

    /// <summary>Domyślnie klucz jest zapisany dla serwera z testowanego adresu.</summary>
    private static string? HostOf(string? endpoint) =>
        LlmEndpoint.TryNormalize(endpoint, out var uri, out _) ? uri!.Authority : null;

    private static string ChatContent(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
    });

    /// <summary>Odpowiada tłumaczeniem „PL:tekst” dla każdego tekstu z wiadomości użytkownika.</summary>
    private static HttpResponseMessage EchoTranslations(CapturedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
        var translations = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => "PL:" + t.GetString()).ToArray();
        return FakeHttpHandler.Json(ChatContent(JsonSerializer.Serialize(new { translations })));
    }

    [Fact]
    public async Task Sukces_wysyla_chat_completions_z_kluczem_i_modelem()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["Hello", "World"], "en", "pl");

        Assert.Equal(["PL:Hello", "PL:World"], result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Url);
        Assert.Equal("Bearer sk-test", request.Header("Authorization"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("gpt-test", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Serwer_lokalny_dziala_bez_klucza_i_bez_naglowka_Authorization()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler, apiKey: null, endpoint: "http://localhost:11434/v1");

        await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.Url);
        Assert.Null(request.Header("Authorization"));
    }

    [Fact]
    public async Task Klucz_zapisany_dla_innego_serwera_nie_jest_wysylany()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler, endpoint: "https://openrouter.ai/api/v1", keyHost: "api.openai.com");

        await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.Null(Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task Serwer_wymagajacy_klucza_innego_hosta_daje_wskazowke_zamiast_bledu_klucza()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Status(401)));
        var provider = CreateProvider(handler, endpoint: "https://openrouter.ai/api/v1", keyHost: "api.openai.com");

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.InvalidConfiguration, ex.Kind);
        Assert.Contains("api.openai.com", ex.UserFriendlyMessage);
    }

    [Fact]
    public async Task Terminy_slownika_i_nazwa_gry_trafiaja_do_promptu()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler);
        var context = new TranslationContext("Path of Exile 2", [new GlossaryTerm("Waystone", "Kamień drogi")]);

        await provider.TranslateWithContextAsync(["Use a Waystone"], "en", "pl", context);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("Waystone => Kamień drogi", system);
        Assert.Contains("Path of Exile 2", system);
    }

    [Fact]
    public async Task Pamiec_dialogu_i_plec_gracza_trafiaja_do_zapytania()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler);
        var context = new TranslationContext(null, [])
        {
            RecentTexts = ["Did you find it?"],
            RecentExchanges = [new RecentExchange("Did you find it?", "Znalazłeś to?")],
            PlayerGender = PlayerGender.Male,
        };

        var result = await provider.TranslateWithContextAsync(["Well done."], "en", "pl", context);

        Assert.Equal(["PL:Well done."], result);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("player character is male", messages[0].GetProperty("content").GetString());
        var user = messages[1].GetProperty("content").GetString()!;
        using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
        var previous = Assert.Single(payload.RootElement.GetProperty("previous").EnumerateArray());
        Assert.Equal("Did you find it?", previous.GetProperty("source").GetString());
        Assert.Equal("Znalazłeś to?", previous.GetProperty("translation").GetString());
        Assert.False(payload.RootElement.TryGetProperty("previous_lines", out _));
    }

    [Fact]
    public async Task Zla_liczba_tlumaczen_w_partii_konczy_sie_tlumaczeniem_pojedynczym()
    {
        var handler = new FakeHttpHandler(static (request, attempt) => Task.FromResult(attempt == 0
            ? FakeHttpHandler.Json(ChatContent("""{"translations": ["tylko jedno"]}"""))
            : EchoTranslations(request)));
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["A", "B"], "en", "pl");

        Assert.Equal(["PL:A", "PL:B"], result);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Brak_modelu_na_serwerze_mapuje_sie_na_ModelNotFound()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(
            FakeHttpHandler.Status(404, """{"error":{"message":"model \"llama9\" not found, try pulling it first"}}""")));
        var provider = CreateProvider(handler, endpoint: "http://localhost:11434/v1");

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task Brak_srodkow_nie_jest_ponawiany_i_mapuje_sie_na_QuotaExceeded()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(
            FakeHttpHandler.Status(429, """{"error":{"type":"insufficient_quota","code":"insufficient_quota"}}""")));
        var provider = new OpenAiCompatibleTranslationProvider(
            new HttpClient(handler), () => "sk", () => "api.openai.com", () => "https://api.openai.com/v1", () => "gpt-test",
            new LlmProviderOptions { MaxRetries = 2 });

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.QuotaExceeded, ex.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://example.com/v1", "gpt-test")]
    [InlineData("https://api.openai.com/v1", "")]
    public async Task Bledna_konfiguracja_nie_wysyla_niczego(string endpoint, string model)
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler, endpoint: endpoint, model: model);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.InvalidConfiguration, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Niedzialajacy_serwer_lokalny_daje_wskazowke_o_uruchomieniu()
    {
        var handler = new FakeHttpHandler(static (_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));
        var provider = CreateProvider(handler, endpoint: "http://localhost:11434/v1");

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
        Assert.Contains("Ollam", ex.Message);
    }

    [Fact]
    public async Task Odmowa_modelu_mapuje_sie_na_ContentRefused()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = (string?)null, refusal = "I can't help." }, finish_reason = "stop" } },
        }))));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.ContentRefused, ex.Kind);
    }

    [Fact]
    public async Task TestConnection_pokazuje_probne_tlumaczenie()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(EchoTranslations(request)));
        var provider = CreateProvider(handler, endpoint: "http://localhost:11434/v1", model: "qwen");

        var status = await provider.TestConnectionAsync();

        Assert.True(status.IsOk);
        Assert.Contains("localhost:11434", status.Message);
        Assert.Contains("PL:Hello, adventurer!", status.Message);
    }

    [Fact]
    public async Task TestConnection_z_blednym_kluczem_zwraca_czytelny_komunikat()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Status(401)));
        var provider = CreateProvider(handler);

        var status = await provider.TestConnectionAsync();

        Assert.False(status.IsOk);
        Assert.Contains("klucz", status.Message, StringComparison.OrdinalIgnoreCase);
    }
}
