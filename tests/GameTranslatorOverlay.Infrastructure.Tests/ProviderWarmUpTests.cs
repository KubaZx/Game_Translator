using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class ProviderWarmUpTests
{
    private static FakeHttpHandler NotFoundHandler() =>
        new(static (_, _) => Task.FromResult(FakeHttpHandler.Status(404)));

    [Fact]
    public async Task DeepL_rozgrzewa_wlasciwy_host_bez_klucza_i_tresci()
    {
        var handler = NotFoundHandler();
        IWarmableTranslationProvider provider = new DeepLTranslationProvider(new HttpClient(handler), () => "sekret:fx");

        await provider.WarmUpAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal("https://api-free.deepl.com/", request.Url);
        Assert.Null(request.Header("Authorization"));
        Assert.DoesNotContain("sekret", string.Join(" ", request.Headers.Values));
        Assert.Empty(request.Body);
    }

    [Fact]
    public async Task Bez_klucza_rozgrzewka_niczego_nie_wysyla()
    {
        var handler = NotFoundHandler();
        IWarmableTranslationProvider[] providers =
        [
            new DeepLTranslationProvider(new HttpClient(handler), () => null),
            new AzureTranslatorProvider(new HttpClient(handler), () => null, () => null),
            new GoogleTranslateProvider(new HttpClient(handler), () => " "),
            new ClaudeTranslationProvider(new HttpClient(handler), () => null, () => null),
        ];

        foreach (var provider in providers) await provider.WarmUpAsync();

        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", 1)]
    [InlineData("http://localhost:11434/v1", 0)]
    [InlineData("http://example.com/v1", 0)]
    public async Task LLM_rozgrzewa_tylko_poprawny_zdalny_serwer(string endpoint, int expectedCalls)
    {
        var handler = NotFoundHandler();
        IWarmableTranslationProvider provider = new OpenAiCompatibleTranslationProvider(
            new HttpClient(handler), () => "sk", () => "openrouter.ai", () => endpoint, () => "model");

        await provider.WarmUpAsync();

        Assert.Equal(expectedCalls, handler.Calls);
        if (expectedCalls == 1)
        {
            Assert.Equal("https://openrouter.ai/", handler.Requests[0].Url);
            Assert.Null(handler.Requests[0].Header("Authorization"));
        }
    }

    [Fact]
    public async Task Blad_sieci_podczas_rozgrzewki_nie_wychodzi_na_zewnatrz()
    {
        var handler = new FakeHttpHandler(static (_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("DNS failure")));
        IWarmableTranslationProvider provider = new GoogleTranslateProvider(new HttpClient(handler), () => "key");

        await provider.WarmUpAsync();

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Fabryka_tworzy_klienta_z_dluzej_zyjaca_pula()
    {
        using var client = ProviderHttpClientFactory.Create();

        Assert.NotNull(client);
    }
}
