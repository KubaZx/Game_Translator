using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class AzureTranslatorProviderTests
{
    private static AzureTranslatorProvider CreateProvider(FakeHttpHandler handler, string? key = "azure-key", string? region = "westeurope") =>
        new(new HttpClient(handler), () => key, () => region, new AzureTranslatorOptions { MaxRetries = 0 });

    private static HttpResponseMessage Echo(CapturedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        var results = body.RootElement.EnumerateArray()
            .Select(static item => new { translations = new[] { new { text = "PL:" + item.GetProperty("Text").GetString(), to = "pl" } } })
            .ToArray();
        return FakeHttpHandler.Json(JsonSerializer.Serialize(results));
    }

    [Fact]
    public async Task Sukces_wysyla_klucz_region_i_jezyki()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["Hello", "World"], "EN", "PL");

        Assert.Equal(["PL:Hello", "PL:World"], result);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0", request.Url);
        Assert.Contains("from=en", request.Url);
        Assert.Contains("to=pl", request.Url);
        Assert.Equal("azure-key", request.Header("Ocp-Apim-Subscription-Key"));
        Assert.Equal("westeurope", request.Header("Ocp-Apim-Subscription-Region"));
    }

    [Fact]
    public async Task Zasob_globalny_nie_wysyla_naglowka_regionu()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler, region: "");

        await provider.TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.Null(handler.Requests[0].Header("Ocp-Apim-Subscription-Region"));
    }

    [Theory]
    [InlineData(401, TranslationFailureKind.InvalidApiKey)]
    [InlineData(403, TranslationFailureKind.QuotaExceeded)]
    [InlineData(429, TranslationFailureKind.RateLimited)]
    [InlineData(503, TranslationFailureKind.ServiceUnavailable)]
    public async Task Kody_bledow_mapuja_sie_na_zrozumiale_rodzaje(int status, TranslationFailureKind expected)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Status(status)));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Wyczerpany_limit_nie_jest_ponawiany()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Status(403)));
        var provider = new AzureTranslatorProvider(new HttpClient(handler), () => "k", () => null,
            new AzureTranslatorOptions { MaxRetries = 2 });

        await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null, "westeurope", TranslationFailureKind.MissingApiKey)]
    [InlineData("k", "west europe\n", TranslationFailureKind.InvalidConfiguration)]
    public async Task Brak_klucza_lub_zly_region_nie_wysyla_niczego(string? key, string region, TranslationFailureKind expected)
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler, key, region);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TestConnection_tlumaczy_krotki_tekst()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);

        var status = await provider.TestConnectionAsync();

        Assert.True(status.IsOk);
        Assert.Contains("PL:Hello", status.Message);
        Assert.Contains("westeurope", status.Message);
    }
}

public class GoogleTranslateProviderTests
{
    private static GoogleTranslateProvider CreateProvider(FakeHttpHandler handler, string? key = "google-key") =>
        new(new HttpClient(handler), () => key, new GoogleTranslateOptions { MaxRetries = 0 });

    private static HttpResponseMessage Echo(CapturedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        var translations = body.RootElement.GetProperty("q").EnumerateArray()
            .Select(static q => new { translatedText = "PL:" + q.GetString() })
            .ToArray();
        return FakeHttpHandler.Json(JsonSerializer.Serialize(new { data = new { translations } }));
    }

    [Fact]
    public async Task Sukces_wysyla_klucz_w_naglowku_i_format_text()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler);

        var result = await provider.TranslateBatchAsync(["Hello", "World"], "en", "pl");

        Assert.Equal(["PL:Hello", "PL:World"], result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://translation.googleapis.com/language/translate/v2", request.Url);
        Assert.DoesNotContain("google-key", request.Url);
        Assert.Equal("google-key", request.Header("X-goog-api-key"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("text", body.RootElement.GetProperty("format").GetString());
        Assert.Equal("pl", body.RootElement.GetProperty("target").GetString());
    }

    [Theory]
    [InlineData(400, """{"error":{"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""", TranslationFailureKind.InvalidApiKey)]
    [InlineData(400, """{"error":{"message":"Invalid Value"}}""", TranslationFailureKind.InvalidRequest)]
    [InlineData(403, """{"error":{"errors":[{"reason":"dailyLimitExceeded"}]}}""", TranslationFailureKind.QuotaExceeded)]
    [InlineData(403, """{"error":{"status":"PERMISSION_DENIED","details":[{"reason":"SERVICE_DISABLED"}]}}""", TranslationFailureKind.InvalidApiKey)]
    [InlineData(429, """{"error":{"status":"RESOURCE_EXHAUSTED"}}""", TranslationFailureKind.RateLimited)]
    public async Task Bledy_Google_mapuja_sie_na_zrozumiale_rodzaje(int status, string body, TranslationFailureKind expected)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Status(status, body)));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Brak_klucza_rzuca_MissingApiKey_bez_zapytania()
    {
        var handler = new FakeHttpHandler(static (request, _) => Task.FromResult(Echo(request)));
        var provider = CreateProvider(handler, key: " ");

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.MissingApiKey, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Odpowiedz_proxy_zamiast_JSON_to_blad_sieci()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Zaloguj się do Wi-Fi</html>"),
        }));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Hello"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.NetworkError, ex.Kind);
    }
}
