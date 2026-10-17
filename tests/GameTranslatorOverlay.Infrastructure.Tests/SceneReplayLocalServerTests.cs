using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class SceneReplayLocalServerTests
{
    private static readonly Uri LmStudio = new("http://127.0.0.1:1234/v1/");

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/")]
    [InlineData("http://LOCALHOST:11434/v1/chat/completions", "http://localhost:11434/v1/")]
    [InlineData("http://[::1]:8080/v1/", "http://[::1]:8080/v1/")]
    public void Adres_serwera_na_tym_komputerze_jest_przyjmowany(string raw, string expected)
    {
        Assert.True(LocalModelServer.TryParseEndpoint(raw, out var baseUri, out var error), error);
        Assert.Equal(expected, baseUri!.ToString());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("http://192.168.1.20:1234/v1")]
    [InlineData("http://127.0.0.2:1234/v1")]
    [InlineData("http://0.0.0.0:1234/v1")]
    [InlineData("http://localhost.example.com:1234/v1")]
    [InlineData("http://localhost.:1234/v1")]
    [InlineData("http://user:sekret@localhost:1234/v1")]
    [InlineData("http://localhost:1234/v1?key=abc")]
    [InlineData("ftp://localhost/v1")]
    [InlineData("localhost:1234")]
    [InlineData("")]
    [InlineData(null)]
    public void Adres_poza_tym_komputerem_albo_z_danymi_logowania_jest_odrzucany(string? raw)
    {
        Assert.False(LocalModelServer.TryParseEndpoint(raw, out var baseUri, out var error));
        Assert.Null(baseUri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Odmowa_adresu_zdalnego_mowi_co_jest_dozwolone()
    {
        Assert.False(LocalModelServer.TryParseEndpoint("https://api.openai.com/v1", out _, out var error));

        Assert.Contains("odmowa", error);
        Assert.Contains("127.0.0.1", error);
        Assert.Contains("[::1]", error);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions", null)]
    [InlineData("http://localhost:1234/v1/chat/completions", null)]
    [InlineData("http://127.0.0.1:11434/v1/chat/completions", null)]
    [InlineData("https://127.0.0.1:1234/v1/chat/completions", null)]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions", "Authorization")]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions", "x-api-key")]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions", "api-key")]
    public async Task Zapytanie_na_inny_adres_albo_z_kluczem_nie_wychodzi(string url, string? secretHeader)
    {
        var inner = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("{}")));
        using var client = new HttpClient(LocalModelServer.CreateHandler(LmStudio, 5, _ => inner));
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") };
        if (secretHeader is not null) request.Headers.TryAddWithoutValidation(secretHeader, "Bearer sk-test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Zapytania_ponad_limit_przebiegu_nie_wychodza()
    {
        var inner = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("{}")));
        using var client = new HttpClient(LocalModelServer.CreateHandler(LmStudio, 2, _ => inner));

        await client.GetAsync("http://127.0.0.1:1234/v1/models");
        await client.GetAsync("http://127.0.0.1:1234/v1/models");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:1234/v1/models"));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Dostawca_z_zapisanym_kluczem_nie_wysle_go_przez_blokade()
    {
        var inner = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json("{}")));
        var provider = new OpenAiCompatibleTranslationProvider(new HttpClient(LocalModelServer.CreateHandler(LmStudio, 5, _ => inner)),
            () => "sk-test", () => "127.0.0.1:1234", () => "http://127.0.0.1:1234/v1", () => "lokalny-model",
            new LlmProviderOptions { MaxRetries = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateWithContextAsync(["Light the beacon"], "en", "pl",
            TranslationContext.Empty));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Prawdziwe_polaczenie_dochodzi_do_serwera_na_petli_zwrotnej()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await socket.ReceiveAsync(buffer, SocketFlags.None);
                if (read == 0) break;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            var body = JsonSerializer.Serialize(new { data = new[] { new { id = "lokalny-model" } } });
            await socket.SendAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), SocketFlags.None);
            return request.ToString();
        });
        var endpoint = new Uri($"http://localhost:{port}/v1/");
        using var client = new HttpClient(LocalModelServer.CreateHandler(endpoint, 1));

        var response = await client.GetStringAsync(new Uri(endpoint, "models")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("lokalny-model", response);
        var received = await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("GET /v1/models HTTP/1.1", received);
        Assert.DoesNotContain("Authorization", received, StringComparison.OrdinalIgnoreCase);
    }
}
