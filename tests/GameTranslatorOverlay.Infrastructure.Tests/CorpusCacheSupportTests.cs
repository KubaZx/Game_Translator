using System.Text.Json;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Providers;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class CorpusCacheSupportTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string DatabasePath => Path.Combine(_temp.Path, "cache.db");

    private static NewCacheEntry Entry(string text, string translated, string profile = "", string provider = "DeepL",
        string? context = "reflow-1", bool approved = false) =>
        new(text, text, "en", "pl", translated, provider, profile, context, IsApproved: approved);

    private long UseCount(string text)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(use_count) FROM translations WHERE normalized_text = $text;";
        command.Parameters.AddWithValue("$text", text);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Podglad_nieistniejacej_bazy_nie_tworzy_pliku()
    {
        var cache = new SqliteTranslationCache(DatabasePath);

        var result = await cache.PeekManyAsync(["Hello", "World"], "en", "pl", "game");

        Assert.Equal([null, null], result);
        Assert.False(File.Exists(DatabasePath));
        Assert.Empty(await cache.PeekManyAsync([], "en", "pl", "game"));
    }

    [Fact]
    public async Task Podglad_ma_priorytety_odczytu_i_nie_liczy_uzyc()
    {
        var cache = new SqliteTranslationCache(DatabasePath);
        await cache.StoreAsync(Entry("Hello", "Cześć (globalny)"));
        await cache.StoreAsync(Entry("Hello", "Cześć (profil)", profile: "game"));
        await cache.StoreAsync(Entry("Bye", "Pa (globalny)"));
        await cache.SaveManualCorrectionAsync(Entry("Bye", "Pa (korekta)", profile: "", provider: "manual"));
        await cache.StoreAsync(Entry("Ok", "OK", profile: "other"));
        cache.FlushUsageStatistics();
        var before = UseCount("Hello");

        var peek = await new SqliteTranslationCache(DatabasePath).PeekManyAsync(["Hello", "Bye", "Ok", "Missing"], "en", "pl", "game");

        Assert.Equal("Cześć (profil)", peek[0]!.TranslatedText);
        Assert.Equal("Pa (korekta)", peek[1]!.TranslatedText);
        Assert.True(peek[1]!.IsManual);
        Assert.Null(peek[2]);
        Assert.Null(peek[3]);
        Assert.Equal(before, UseCount("Hello"));
        var lookup = await cache.LookupAsync("Hello", "en", "pl", "game");
        Assert.Equal(peek[0]!.TranslatedText, lookup!.TranslatedText);
    }

    [Fact]
    public async Task Podglad_bazy_bez_tabeli_zwraca_brak_wpisow()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE other (id INTEGER);";
            command.ExecuteNonQuery();
        }

        var result = await new SqliteTranslationCache(DatabasePath).PeekManyAsync(["Hello"], "en", "pl", "");

        Assert.Equal([null], result);
    }

    [Fact]
    public async Task Uszkodzona_baza_przy_podgladzie_daje_czytelny_blad()
    {
        File.WriteAllText(DatabasePath, "to nie jest baza SQLite, tylko zwykły tekst o długości większej niż nagłówek");

        await Assert.ThrowsAsync<CacheStorageException>(() => new SqliteTranslationCache(DatabasePath).PeekManyAsync(["Hello"], "en", "pl", ""));
    }

    [Fact]
    public async Task Zapis_partii_nie_nadpisuje_korekt_ani_zatwierdzonych_i_mowi_ile_zapisal()
    {
        var cache = new SqliteTranslationCache(DatabasePath);
        await cache.SaveManualCorrectionAsync(Entry("Manual", "Korekta", profile: "game", provider: "manual"));
        await cache.StoreAsync(Entry("Approved", "Zatwierdzony", profile: "game", approved: true));
        await cache.StoreAsync(Entry("Old", "Stary", profile: "game", context: null));
        Assert.Equal("Stary", (await cache.LookupAsync("Old", "en", "pl", "game"))!.TranslatedText);

        var written = await cache.StoreManyAsync([
            Entry("Manual", "Nowy", profile: "game", context: "reflow-1;src=corpus"),
            Entry("Approved", "Nowy", profile: "game", context: "reflow-1;src=corpus"),
            Entry("Old", "Nowy", profile: "game", context: "reflow-1;src=corpus"),
            Entry("Fresh", "Świeży", profile: "game", context: "reflow-1;src=corpus"),
        ]);

        Assert.Equal(2, written);
        Assert.Equal("Korekta", (await cache.LookupAsync("Manual", "en", "pl", "game"))!.TranslatedText);
        Assert.Equal("Zatwierdzony", (await cache.LookupAsync("Approved", "en", "pl", "game"))!.TranslatedText);
        var refreshed = await cache.LookupAsync("Old", "en", "pl", "game");
        Assert.Equal("Nowy", refreshed!.TranslatedText);
        Assert.Equal("reflow-1;src=corpus", refreshed.Context);
        Assert.Equal("Świeży", (await cache.LookupAsync("Fresh", "en", "pl", "game"))!.TranslatedText);
        Assert.Null(await cache.LookupAsync("Fresh", "en", "pl", ""));
        Assert.Equal(0, await cache.StoreManyAsync([]));
    }

    [Fact]
    public void Dostawcy_ze_zmiennych_srodowiskowych_bez_kluczy_sa_pomijani()
    {
        using var providers = new EnvironmentTranslationProviders(static _ => null, static () => new HttpClient(new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Status(500)))));

        Assert.Contains(EnvironmentTranslationProviders.DeepLKey, providers.Create("deepl").SkipReason);
        Assert.Contains(EnvironmentTranslationProviders.AzureKey, providers.Create("azure").SkipReason);
        Assert.Contains(EnvironmentTranslationProviders.GoogleKey, providers.Create("google").SkipReason);
        Assert.Contains(EnvironmentTranslationProviders.AnthropicKey, providers.Create("claude").SkipReason);
        Assert.Contains(EnvironmentTranslationProviders.LlmEndpointVariable, providers.Create("llm").SkipReason);
        Assert.Equal("nieznany dostawca.", providers.Create("inny").SkipReason);
        var mock = providers.Create(" Mock ");
        Assert.IsType<MockTranslationProvider>(mock.Provider);
        Assert.True(mock.IsLocal);
    }

    [Fact]
    public void Dostawcy_z_kluczami_ze_zmiennych_srodowiskowych_sa_tworzeni()
    {
        var env = new Dictionary<string, string>
        {
            [EnvironmentTranslationProviders.DeepLKey] = "k:fx",
            [EnvironmentTranslationProviders.AzureKey] = "a",
            [EnvironmentTranslationProviders.GoogleKey] = "g",
            [EnvironmentTranslationProviders.AnthropicKey] = "c",
            [EnvironmentTranslationProviders.ClaudeModel] = "claude-haiku-4-5",
        };
        var created = 0;
        var providers = new EnvironmentTranslationProviders(name => env.GetValueOrDefault(name), () =>
        {
            created++;
            return new HttpClient(new FakeHttpHandler(static (_, _) => Task.FromResult(FakeHttpHandler.Status(500))));
        });

        Assert.IsType<DeepLTranslationProvider>(providers.Create("deepl", new EnvironmentProviderOptions { DeepLGlossary = false }).Provider);
        Assert.IsType<AzureTranslatorProvider>(providers.Create("azure").Provider);
        Assert.IsType<GoogleTranslateProvider>(providers.Create("google").Provider);
        var claude = providers.Create("claude");
        Assert.IsType<ClaudeTranslationProvider>(claude.Provider);
        Assert.Equal("claude-haiku-4-5", claude.Model);
        Assert.Equal(2, created);
        providers.Dispose();
        providers.Dispose();
    }

    [Fact]
    public async Task LLM_z_adresem_DeepSeek_dostaje_opcje_presetu_a_jawne_opcje_je_zastepuja()
    {
        var env = new Dictionary<string, string>
        {
            [EnvironmentTranslationProviders.LlmEndpointVariable] = "https://api.deepseek.com/v1",
            [EnvironmentTranslationProviders.LlmModel] = "deepseek-flash",
            [EnvironmentTranslationProviders.LlmKey] = "sk",
        };
        var handler = new FakeHttpHandler(static (request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            var translations = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => "PL:" + t.GetString()).ToArray();
            return Task.FromResult(FakeHttpHandler.Json(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(new { translations }) }, finish_reason = "stop" } },
            })));
        });
        using var providers = new EnvironmentTranslationProviders(name => env.GetValueOrDefault(name), () => new HttpClient(handler));

        var preset = providers.Create("llm");
        var explicitOptions = providers.Create("llm", new EnvironmentProviderOptions { LlmServerOptions = new LlmServerOptions { ReasoningEffort = "low" } });
        var raw = providers.Create("llm", new EnvironmentProviderOptions { UseLlmPreset = false });
        await preset.Provider!.TranslateBatchAsync(["Hi"], "en", "pl");
        await explicitOptions.Provider!.TranslateBatchAsync(["Hi"], "en", "pl");
        await raw.Provider!.TranslateBatchAsync(["Hi"], "en", "pl");

        Assert.Equal("DeepSeek", preset.PresetName);
        Assert.Equal("api.deepseek.com", preset.Endpoint);
        Assert.Equal("deepseek-flash", preset.Model);
        Assert.False(preset.IsLocal);
        Assert.Equal("thinking=disabled", preset.ServerOptions!.Describe());
        Assert.Equal("reasoning_effort=low", explicitOptions.ServerOptions!.Describe());
        Assert.Null(explicitOptions.PresetName);
        Assert.Null(raw.ServerOptions);
        var bodies = handler.Requests.Select(static r => JsonDocument.Parse(r.Body).RootElement).ToList();
        Assert.Equal("disabled", bodies[0].GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(bodies[1].TryGetProperty("thinking", out _));
        Assert.Equal("low", bodies[1].GetProperty("reasoning_effort").GetString());
        Assert.False(bodies[2].TryGetProperty("thinking", out _));
        Assert.Equal("Bearer sk", handler.Requests[0].Header("Authorization"));
    }

    [Fact]
    public void LLM_z_blednym_adresem_jest_pomijany_z_powodem()
    {
        var env = new Dictionary<string, string>
        {
            [EnvironmentTranslationProviders.LlmEndpointVariable] = "http://example.com/v1",
            [EnvironmentTranslationProviders.LlmModel] = "m",
        };
        using var providers = new EnvironmentTranslationProviders(name => env.GetValueOrDefault(name), static () => new HttpClient());

        var result = providers.Create("llm");

        Assert.Null(result.Provider);
        Assert.StartsWith(EnvironmentTranslationProviders.LlmEndpointVariable, result.SkipReason);
    }
}
