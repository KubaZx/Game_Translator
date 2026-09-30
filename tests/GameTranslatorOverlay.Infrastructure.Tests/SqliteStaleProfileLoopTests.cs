using System.Text.Json;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// Nieaktualny (sprzed sklejania wierszy) automatyczny wpis profilu gry — np. z importu
/// albo starej bazy — nie może powodować płatnego zapytania przy każdym wystąpieniu tekstu.
/// </summary>
public sealed class SqliteStaleProfileLoopTests : IDisposable
{
    private const string Profile = "witcher";
    private static readonly string MultiLine = TextNormalizer.Normalize("Talk to the\nblacksmith about the sword.");

    private readonly string _databasePath;
    private readonly SqliteTranslationCache _cache;

    public SqliteStaleProfileLoopTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gto-tests");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        _cache = new SqliteTranslationCache(_databasePath);
        _cache.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class CountingProvider : ITranslationProvider
    {
        public int CallCount;
        public string Name => "Counting";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL: " + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static string ImportedProfileEntryJson() => JsonSerializer.Serialize(
        new[]
        {
            new CacheExportEntry(MultiLine, MultiLine, "en", "pl", "Porozmawiaj z\nkowalem o mieczu.", "DeepL",
                Profile, IsManual: false, IsApproved: false),
        },
        CacheExportEntry.JsonOptions);

    [Fact]
    public async Task Nieaktualny_wpis_profilu_z_importu_jest_tlumaczony_tylko_raz()
    {
        Assert.Contains('\n', MultiLine);
        Assert.Equal(1, await _cache.ImportJsonAsync(ImportedProfileEntryJson()));
        var provider = new CountingProvider();
        var pipeline = new TranslationPipeline(new GlossaryService(), _cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { GameProfile = Profile });

        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 4; i++)
        {
            outcomes.Add(Assert.Single(await pipeline.TranslateAsync([MultiLine], "en", "pl")));
        }

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(TranslationOrigin.Provider, outcomes[0].Origin);
        Assert.All(outcomes.Skip(1), o => Assert.Equal(TranslationOrigin.Cache, o.Origin));
        // Nowy wynik nadpisał wpis profilu (a nie powstał obok w globalnym).
        var hit = await _cache.LookupAsync(MultiLine, "en", "pl", Profile);
        Assert.Equal(Profile, hit!.GameProfile);
        Assert.Equal(TextReflow.FormatVersion, hit.Context);
        Assert.Null(await _cache.LookupAsync(MultiLine, "en", "pl", ""));
    }

    [Fact]
    public async Task Nowy_pipeline_na_tej_samej_bazie_nie_tlumaczy_ponownie()
    {
        await _cache.ImportJsonAsync(ImportedProfileEntryJson());
        var provider = new CountingProvider();
        var options = new TranslationPipelineOptions { GameProfile = Profile };

        await new TranslationPipeline(new GlossaryService(), _cache, provider, new UsageTracker(), options)
            .TranslateAsync([MultiLine], "en", "pl");
        var reopened = new SqliteTranslationCache(_databasePath);
        var outcome = Assert.Single(await new TranslationPipeline(new GlossaryService(), reopened, provider, new UsageTracker(), options)
            .TranslateAsync([MultiLine], "en", "pl"));

        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Eksport_i_import_zachowuje_znacznik_kontekstu()
    {
        await _cache.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "Porozmawiaj z\nkowalem.", "DeepL",
            Context: TextReflow.FormatVersion));
        await _cache.StoreAsync(new NewCacheEntry("Gold: 150", "Gold: 150", "en", "pl", "Złoto: 15", "Claude",
            Context: "reflow-1;qa=numbers"));
        var json = await _cache.ExportJsonAsync();

        var otherPath = Path.Combine(Path.GetDirectoryName(_databasePath)!, Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var other = new SqliteTranslationCache(otherPath);
            Assert.Equal(2, await other.ImportJsonAsync(json));

            Assert.Equal(TextReflow.FormatVersion, (await other.LookupAsync(MultiLine, "en", "pl", ""))!.Context);
            Assert.Equal("reflow-1;qa=numbers", (await other.LookupAsync("Gold: 150", "en", "pl", ""))!.Context);

            // Zaimportowany wpis w aktualnym formacie nie jest tłumaczony ponownie.
            var provider = new CountingProvider();
            var outcome = Assert.Single(await new TranslationPipeline(new GlossaryService(), other, provider, new UsageTracker(),
                new TranslationPipelineOptions()).TranslateAsync([MultiLine], "en", "pl"));
            Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
            Assert.Equal(0, provider.CallCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(otherPath + suffix)) File.Delete(otherPath + suffix);
            }
        }
    }

    [Fact]
    public async Task Stary_plik_eksportu_bez_kontekstu_nadal_sie_importuje()
    {
        const string oldJson = """
            [
              {
                "sourceText": "Hello",
                "normalizedText": "Hello",
                "sourceLanguage": "en",
                "targetLanguage": "pl",
                "translatedText": "Cześć",
                "provider": "DeepL",
                "gameProfile": "",
                "isManual": false,
                "isApproved": false
              },
              {
                "sourceText": "Bye",
                "normalizedText": "Bye",
                "sourceLanguage": "en",
                "targetLanguage": "pl",
                "translatedText": "Pa",
                "provider": "manual",
                "gameProfile": "",
                "isManual": true,
                "isApproved": true
              }
            ]
            """;

        Assert.Equal(2, await _cache.ImportJsonAsync(oldJson));

        var hello = await _cache.LookupAsync("Hello", "en", "pl", "");
        Assert.Equal("Cześć", hello!.TranslatedText);
        Assert.Null(hello.Context);
        Assert.True((await _cache.LookupAsync("Bye", "en", "pl", ""))!.IsManual);
    }
}
