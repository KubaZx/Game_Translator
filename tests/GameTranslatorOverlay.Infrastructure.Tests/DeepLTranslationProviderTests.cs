using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class DeepLTranslationProviderTests
{
    private sealed class FakeHandler(Func<FakeRequest, int, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<FakeRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new FakeRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body,
                cancellationToken);
            Requests.Add(captured);
            return await responder(captured, Calls++);
        }
    }

    private sealed record FakeRequest(string Url, string Authorization, string Body, CancellationToken CancellationToken);

    private static HttpResponseMessage TranslationsResponse(params string[] translations)
    {
        var payload = new
        {
            translations = translations.Select(static t => new { detected_source_language = "EN", text = t }).ToArray(),
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage EchoResponse(FakeRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        var texts = document.RootElement.GetProperty("text").EnumerateArray()
            .Select(static e => "PL:" + e.GetString())
            .ToArray();
        return TranslationsResponse(texts);
    }

    private static DeepLTranslationProvider CreateProvider(
        FakeHandler handler, string? apiKey = "test-key:fx", DeepLOptions? options = null) =>
        new(new HttpClient(handler), () => apiKey, options);

    [Fact]
    public async Task Sukces_zwraca_tlumaczenia_w_kolejnosci()
    {
        var handler = new FakeHandler(static (request, _) => Task.FromResult(EchoResponse(request)));
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["Hello", "World"], "en", "pl");

        Assert.Equal(["PL:Hello", "PL:World"], result);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api-free.deepl.com", request.Url);
        Assert.Contains("DeepL-Auth-Key", request.Authorization);
    }

    [Fact]
    public async Task Klucz_pro_uzywa_glownego_endpointu()
    {
        var handler = new FakeHandler(static (request, _) => Task.FromResult(EchoResponse(request)));
        var provider = CreateProvider(handler, apiKey: "pro-key-bez-sufiksu");

        await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.StartsWith("https://api.deepl.com", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Brak_klucza_rzuca_MissingApiKey_bez_zadnego_zapytania()
    {
        var handler = new FakeHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.MissingApiKey, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TranslationFailureKind.InvalidApiKey)]
    [InlineData((HttpStatusCode)456, TranslationFailureKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.BadRequest, TranslationFailureKind.InvalidRequest)]
    public async Task Kody_bledow_mapuja_sie_na_zrozumiale_rodzaje(HttpStatusCode status, TranslationFailureKind expected)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task RateLimit_jest_ponawiany_i_konczy_sie_sukcesem()
    {
        var handler = new FakeHandler(static (request, attempt) =>
        {
            if (attempt < 2)
            {
                var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(retry);
            }
            return Task.FromResult(EchoResponse(request));
        });
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.Equal("PL:Hello", result[0]);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Staly_RateLimit_konczy_sie_bledem_po_wyczerpaniu_prob()
    {
        var handler = new FakeHandler(static (_, _) =>
        {
            var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(retry);
        });
        var provider = CreateProvider(handler, options: new DeepLOptions { MaxRetries = 2 });

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.RateLimited, ex.Kind);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Timeout_mapuje_sie_na_zrozumialy_blad()
    {
        var handler = new FakeHandler(static async (request, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), request.CancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = CreateProvider(handler, options: new DeepLOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Brak_sieci_mapuje_sie_na_NetworkError()
    {
        var handler = new FakeHandler(static (_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("DNS failure")));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
    }

    [Fact]
    public async Task Duze_partie_sa_dzielone_na_zapytania_po_50_tekstow()
    {
        var handler = new FakeHandler(static (request, _) => Task.FromResult(EchoResponse(request)));
        var provider = CreateProvider(handler);
        var texts = Enumerable.Range(1, 60).Select(static i => $"Tekst {i}").ToList();

        var result = await provider.TranslateBatchAsync(texts, "en", "pl");

        Assert.Equal(60, result.Count);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("PL:Tekst 1", result[0]);
        Assert.Equal("PL:Tekst 60", result[59]);
    }

    [Fact]
    public async Task Niekompletna_odpowiedz_zglasza_blad()
    {
        var handler = new FakeHandler(static (_, _) => Task.FromResult(TranslationsResponse("tylko jedno")));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateBatchAsync(["A", "B"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.Unknown, ex.Kind);
    }

    [Fact]
    public async Task TestConnection_czyta_zuzycie_znakow()
    {
        var handler = new FakeHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"character_count": 12345, "character_limit": 500000}""", Encoding.UTF8, "application/json"),
        }));
        var provider = CreateProvider(handler);

        var status = await provider.TestConnectionAsync();

        Assert.True(status.IsOk);
        Assert.Equal(12345, status.CharactersUsed);
        Assert.Equal(500000, status.CharacterLimit);
    }

    [Fact]
    public async Task TestConnection_z_blednym_kluczem_zwraca_czytelny_komunikat()
    {
        var handler = new FakeHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var provider = CreateProvider(handler);

        var status = await provider.TestConnectionAsync();

        Assert.False(status.IsOk);
        Assert.Contains("klucz", status.Message, StringComparison.OrdinalIgnoreCase);
    }
}
