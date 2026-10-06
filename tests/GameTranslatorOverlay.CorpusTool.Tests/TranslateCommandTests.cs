using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.CorpusTool.Translation;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Providers;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.CorpusTool.Tests;

public sealed class TranslateCommandTests : IDisposable
{
    private const string Profile = "test-game";
    private readonly TempDirectory _temp = new();
    private readonly string _profileFile;
    private readonly string _corpus;
    private readonly string _data;

    public TranslateCommandTests()
    {
        _profileFile = _temp.WriteFile("profile.json", Encoding.UTF8.GetBytes(
            """{ "id": "test-game", "name": "Test Game", "processNames": ["test.exe"], "sourceLanguage": "en" }"""));
        _corpus = _temp.Combine("corpus.jsonl");
        WriteCorpus(_corpus, [
            Corpus.Dialog("1", "Are you ready?", "Intro", 1, "Ann"),
            Corpus.Dialog("2", "Then follow me.", "Intro", 2, "Bob"),
            Corpus.Ui("Btn_Open", "Open the door", context: "Common | Button caption, verb"),
            Corpus.Ui("Stat_Health", "Health"),
            Corpus.Ui("Manual", "Manual line"),
            Corpus.Subtitle("Ann_Gasp", "Oh no!", speaker: "Ann"),
        ]);
        _data = _temp.Combine("data");
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string CachePath => Path.Combine(_data, "cache.db");

    private static void WriteCorpus(string path, IEnumerable<CorpusEntry> entries)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        CorpusJsonl.Write(writer, entries);
    }

    private TranslateOptions Options(string provider, params string[] extra) =>
        TranslateOptions.Parse(["translate", "--profile-file", _profileFile, "--provider", provider, "--corpus", _corpus, "--data-dir", _data, .. extra]);

    private static async Task<(int Exit, string Output, string Error)> Run(
        TranslateOptions options, Func<EnvironmentTranslationProviders>? providers = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await TranslateCommand.RunAsync(options, output, error, providers);
        return (exit, output.ToString(), error.ToString());
    }

    private static TranslationPipeline Pipeline(string databasePath, string profile, ITranslationProvider provider)
    {
        var glossary = new GlossaryService();
        glossary.LoadDocument(GlossarySerializer.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "glossaries", "global", "en-pl.json"))));
        return new TranslationPipeline(glossary, new SqliteTranslationCache(databasePath), provider, new UsageTracker(),
            new TranslationPipelineOptions { GameProfile = profile });
    }

    private static string Hash(string path)
    {
        SqliteConnection.ClearAllPools();
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static long WalLength(string path) => File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;

    [Fact]
    public async Task Mock_zapisuje_wpisy_czytelne_dla_pipeline_z_profilem_i_zachowuje_priorytety()
    {
        var seed = new SqliteTranslationCache(CachePath);
        await seed.SaveManualCorrectionAsync(new NewCacheEntry("Manual line", "Manual line", "en", "pl", "Ręczna linia", "manual", Profile));
        await seed.StoreAsync(new NewCacheEntry("Open the door", "Open the door", "en", "pl", "Otwórz drzwi", "DeepL"));

        var (exit, output, _) = await Run(Options("mock"));

        Assert.Equal(TranslateCommand.ExitOk, exit);
        Assert.Contains("Pominięte: słownik 1, ręczne korekty 1, zatwierdzone 0, już w profilu 0", output);
        Assert.Contains("przesłoni wpis globalny 1", output);
        var withProfile = await Pipeline(CachePath, Profile, new MockTranslationProvider())
            .TranslateLocalAsync(["Are you ready?", "Open the door", "Health", "Manual line", "Oh no!"], "en", "pl");
        Assert.Equal("[PL] Are you ready?", withProfile[0]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, withProfile[0]!.Origin);
        Assert.Equal("[PL] Open the door", withProfile[1]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Glossary, withProfile[2]!.Origin);
        Assert.Equal("Ręczna linia", withProfile[3]!.TranslatedText);
        Assert.Equal("[PL] Oh no!", withProfile[4]!.TranslatedText);

        var withoutProfile = await Pipeline(CachePath, "", new MockTranslationProvider())
            .TranslateLocalAsync(["Are you ready?", "Open the door"], "en", "pl");
        Assert.Null(withoutProfile[0]);
        Assert.Equal("Otwórz drzwi", withoutProfile[1]!.TranslatedText);

        var again = await Run(Options("mock"));
        Assert.Equal(TranslateCommand.ExitOk, again.Exit);
        Assert.Contains("już w profilu 4", again.Output);
        Assert.Contains("Nie ma nic do tłumaczenia.", again.Output);
    }

    [Fact]
    public async Task Przebieg_probny_nie_tworzy_bazy_i_nie_zmienia_istniejacej()
    {
        var (exit, output, _) = await Run(Options("mock", "--dry-run"));

        Assert.Equal(TranslateCommand.ExitOk, exit);
        Assert.Contains("Przebieg próbny: nic nie wysłano i nic nie zapisano.", output);
        Assert.False(File.Exists(CachePath));

        await new SqliteTranslationCache(CachePath).StoreAsync(new NewCacheEntry("Open the door", "Open the door", "en", "pl", "Otwórz drzwi", "DeepL"));
        var before = Hash(CachePath);
        Assert.Equal(0, WalLength(CachePath));
        var second = await Run(Options("mock", "--dry-run"));
        Assert.Equal(TranslateCommand.ExitOk, second.Exit);
        Assert.Contains("przesłoni wpis globalny 1", second.Output);
        Assert.Equal(before, Hash(CachePath));
        Assert.Equal(0, WalLength(CachePath));
    }

    [Fact]
    public async Task Tryb_prywatny_odmawia_zapisu_a_przebieg_probny_tylko_ostrzega()
    {
        File.WriteAllText(Path.Combine(_data, "settings.json"), """{ "privateMode": true, "playerGender": "female" }""");

        var refused = await Assert.ThrowsAsync<RefusedException>(() => Run(Options("mock")));
        var dry = await Run(Options("mock", "--dry-run"));

        Assert.Contains("Tryb prywatny", refused.Message);
        Assert.False(File.Exists(CachePath));
        Assert.Equal(TranslateCommand.ExitOk, dry.Exit);
        Assert.Contains("tryb prywatny jest włączony", dry.Output);
        Assert.Contains("płeć gracza: female", dry.Output);
    }

    [Fact]
    public async Task Nieczytelne_ustawienia_to_odmowa()
    {
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{ to nie jest json");

        var refused = await Assert.ThrowsAsync<RefusedException>(() => Run(Options("mock")));

        Assert.Contains("tryb prywatny", refused.Message);
    }

    [Fact]
    public async Task Baza_w_repozytorium_jest_odrzucana_poza_eval_private()
    {
        _temp.WriteFile("repo/.git/HEAD", Encoding.UTF8.GetBytes("ref: refs/heads/main"));
        var inside = _temp.Combine("repo", "data", "cache.db");
        var allowed = _temp.Combine("repo", "eval", "private", "cache.db");

        await Assert.ThrowsAsync<RefusedException>(() => Run(TranslateOptions.Parse(
            ["translate", "--profile-file", _profileFile, "--provider", "mock", "--corpus", _corpus, "--cache", inside])));
        await Assert.ThrowsAsync<RefusedException>(() => Run(Options("mock", "--stats", _temp.Combine("repo", "stats.json"))));
        var ok = await Run(TranslateOptions.Parse(
            ["translate", "--profile-file", _profileFile, "--provider", "mock", "--corpus", _corpus, "--cache", allowed]));

        Assert.Equal(TranslateCommand.ExitOk, ok.Exit);
        Assert.False(File.Exists(inside));
        Assert.True(File.Exists(allowed));
    }

    [Fact]
    public async Task Mock_bez_jawnej_bazy_jest_odrzucany()
    {
        var options = TranslateOptions.Parse(["translate", "--profile-file", _profileFile, "--provider", "mock", "--corpus", _corpus]);

        var refused = await Assert.ThrowsAsync<RefusedException>(() => Run(options));

        Assert.Contains("--cache", refused.Message);
    }

    [Fact]
    public async Task Brak_korpusu_i_brak_klucza_to_blad()
    {
        var missingCorpus = await Run(TranslateOptions.Parse(
            ["translate", "--profile-file", _profileFile, "--provider", "mock", "--corpus", _temp.Combine("nie-ma.jsonl"), "--data-dir", _data]));
        var missingKey = await Run(Options("deepl"), () => new EnvironmentTranslationProviders(static _ => null, static () => new HttpClient()));

        Assert.Equal(TranslateCommand.ExitError, missingCorpus.Exit);
        Assert.Contains("extract", missingCorpus.Error);
        Assert.Equal(TranslateCommand.ExitError, missingKey.Exit);
        Assert.Contains(EnvironmentTranslationProviders.DeepLKey, missingKey.Error);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task LLM_DeepSeek_wysyla_bez_myslenia_z_kontekstem_i_zapisuje_wpisy_z_plcia()
    {
        var env = new Dictionary<string, string>
        {
            [EnvironmentTranslationProviders.LlmEndpointVariable] = "https://api.deepseek.com/v1",
            [EnvironmentTranslationProviders.LlmModel] = "deepseek-flash",
            [EnvironmentTranslationProviders.LlmKey] = "sk-test",
        };
        var handler = new FakeHttpHandler(static (_, body, _) =>
        {
            using var request = JsonDocument.Parse(body);
            var user = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(user[(user.IndexOf('\n') + 1)..]);
            var translations = payload.RootElement.GetProperty("texts").EnumerateArray().Select(static t => "PL:" + t.GetString()).ToArray();
            return FakeHttpHandler.Json(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(new { translations }) }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 100, completion_tokens = 20, prompt_cache_hit_tokens = 50, completion_tokens_details = new { reasoning_tokens = 0 } },
            }));
        });
        var stats = Path.Combine(_temp.Path, "stats", "llm.json");

        var (exit, output, _) = await Run(Options("llm", "--player-gender", "female", "--stats", stats),
            () => new EnvironmentTranslationProviders(name => env.GetValueOrDefault(name), () => new HttpClient(handler)));

        Assert.Equal(TranslateCommand.ExitOk, exit);
        Assert.Contains("opcje serwera: thinking=disabled (preset DeepSeek)", output);
        Assert.Contains("Tokeny (z odpowiedzi serwera): zapytania 3", output);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, static r =>
        {
            Assert.Equal("https://api.deepseek.com/v1/chat/completions", r.Url);
            Assert.Equal("Bearer sk-test", r.Authorization);
            using var body = JsonDocument.Parse(r.Body);
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Contains("Game: Test Game", system);
            Assert.Contains("Scene: ", system);
            Assert.Contains("player character is female", system);
            Assert.Contains("\"notes\"", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        });
        var dialog = handler.Requests.Single(static r => r.Body.Contains("Are you ready?", StringComparison.Ordinal));
        Assert.Contains("speaker: Ann", dialog.Body);

        var peek = await new SqliteTranslationCache(CachePath).PeekManyAsync(["Are you ready?", "Open the door", "Oh no!"], "en", "pl", Profile);
        Assert.All(peek, static e =>
        {
            Assert.Equal("LLM", e!.Provider);
            Assert.Equal(Profile, e.GameProfile);
            Assert.Equal("reflow-1;pg=f;src=corpus", e.Context);
        });
        Assert.Equal("PL:Are you ready?", peek[0]!.TranslatedText);

        var json = File.ReadAllText(stats);
        Assert.DoesNotContain("Are you ready", json);
        Assert.DoesNotContain("Open the door", json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(300, parsed.RootElement.GetProperty("tokens").GetProperty("prompt").GetInt64());
        Assert.Equal(5, parsed.RootElement.GetProperty("report").GetProperty("stored").GetInt32());
        Assert.Equal("deepseek-flash", parsed.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task DeepL_dostaje_scene_w_kontekscie_i_nie_zapisuje_plci()
    {
        var env = new Dictionary<string, string> { [EnvironmentTranslationProviders.DeepLKey] = "key:fx" };
        var handler = new FakeHttpHandler(static (_, body, _) =>
        {
            using var request = JsonDocument.Parse(body);
            var translations = request.RootElement.GetProperty("text").EnumerateArray()
                .Select(static t => new { detected_source_language = "EN", text = "PL " + t.GetString() }).ToArray();
            return FakeHttpHandler.Json(JsonSerializer.Serialize(new { translations }));
        });

        var (exit, output, _) = await Run(Options("deepl", "--no-deepl-glossary", "--player-gender", "male", "--kinds", "dialog"),
            () => new EnvironmentTranslationProviders(name => env.GetValueOrDefault(name), () => new HttpClient(handler)));

        Assert.Equal(TranslateCommand.ExitOk, exit);
        Assert.Contains("Do tłumaczenia: 2 tekstów", output);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api-free.deepl.com/v2/translate", request.Url);
        Assert.Equal("DeepL-Auth-Key key:fx", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.StartsWith("Dialogue \"Intro\"; speakers: Ann, Bob\n", body.RootElement.GetProperty("context").GetString());
        var peek = await new SqliteTranslationCache(CachePath).PeekManyAsync(["Are you ready?", "Then follow me."], "en", "pl", Profile);
        Assert.All(peek, static e => Assert.Equal("reflow-1;src=corpus", e!.Context));
        Assert.Equal("PL Then follow me.", peek[1]!.TranslatedText);
    }
}
