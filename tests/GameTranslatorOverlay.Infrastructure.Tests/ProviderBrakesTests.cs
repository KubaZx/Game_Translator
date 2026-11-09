using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class ProviderBrakesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static OpenAiCompatibleTranslationProvider CreateProvider(FakeHttpHandler handler, string endpoint) =>
        new(new HttpClient(handler), () => null, () => null, () => endpoint, () => "model", new LlmProviderOptions { MaxRetries = 0 });

    private static string ChatContent(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
    });

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("https://LOCALHOST/v1", true)]
    [InlineData("https://api.example.test/v1", false)]
    [InlineData("https://localhost.example.test/v1", false)]
    [InlineData("", false)]
    [InlineData("nie adres", false)]
    public void Serwer_pod_adresem_tego_komputera_jest_rozpoznawany(string endpoint, bool local)
    {
        var provider = CreateProvider(new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Status(500))), endpoint);

        Assert.Equal(local, provider.UsesLocalServer);
    }

    [Fact]
    public async Task Nieczytelna_pojedyncza_linia_nie_wyrzuca_przetlumaczonych_linii_partii()
    {
        var handler = new FakeHttpHandler(static (request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            var texts = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => t.GetString()!).ToArray();
            if (texts.Length != 1 || texts[0] == "Bravo") return Task.FromResult(FakeHttpHandler.Json(ChatContent("{\"translations\":[\"x\",\"y\"]}")));
            return Task.FromResult(FakeHttpHandler.Json(ChatContent(
                JsonSerializer.Serialize(new { translations = new[] { "PL:" + texts[0] } }))));
        });
        var provider = CreateProvider(handler, "http://localhost:11434/v1");

        var result = await provider.TranslateBatchAsync(["Alpha", "Bravo", "Charlie"], "en", "pl");

        Assert.Equal(["PL:Alpha", string.Empty, "PL:Charlie"], result);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task Pojedynczy_tekst_z_nieczytelna_odpowiedzia_nadal_zglasza_blad()
    {
        var handler = new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Json(ChatContent("{\"translations\":[\"x\",\"y\"]}"))));
        var provider = CreateProvider(handler, "http://localhost:11434/v1");

        var ex = await Assert.ThrowsAsync<TranslationException>(() => provider.TranslateBatchAsync(["Alpha"], "en", "pl"));

        Assert.Equal(TranslationFailureKind.Unknown, ex.Kind);
    }

    [Fact]
    public void Nowe_ustawienia_nie_maja_limitu_znakow()
    {
        Assert.Null(new AppSettings().SessionCharacterLimit);
    }

    [Fact]
    public void Stary_plik_bez_pola_zostaje_bez_limitu()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, """{"provider": "Claude"}""");

        Assert.Null(new JsonSettingsStore(paths).Load().SessionCharacterLimit);
    }

    [Fact]
    public void Pusty_limit_zapisany_w_oknie_zostaje_pusty_po_ponownym_wczytaniu()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);

        store.Save(new AppSettings { SessionCharacterLimit = null });

        Assert.Null(store.Load().SessionCharacterLimit);
    }

    [Fact]
    public void Wlasny_limit_przechodzi_zapis_i_odczyt()
    {
        var paths = new AppPaths(_temp.Path);
        var store = new JsonSettingsStore(paths);

        store.Save(new AppSettings { SessionCharacterLimit = 250_000 });

        Assert.Equal(250_000, store.Load().SessionCharacterLimit);
    }

    [Fact]
    public void Zmiana_limitu_przebudowuje_pipeline()
    {
        var before = new AppSettings().PipelineSnapshot();

        var after = new AppSettings { SessionCharacterLimit = 250_000 }.PipelineSnapshot();

        Assert.NotEqual(before, after);
    }
}
