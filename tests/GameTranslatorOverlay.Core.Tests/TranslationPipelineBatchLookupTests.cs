using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Pipeline pyta cache o całą klatkę jednym odczytem partii (<see cref="ITranslationCache.LookupManyAsync"/>):
/// o te same teksty co dawniej tekst po tekście, z tą samą obsługą błędów pojedynczych odczytów.
/// </summary>
public class TranslationPipelineBatchLookupTests
{
    private sealed class EchoProvider : ITranslationProvider
    {
        public int Calls;
        public string Name => "Echo";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    /// <summary>Zapisuje partie odczytów; opcjonalnie psuje odczyt wybranego tekstu albo całą partię.</summary>
    private sealed class RecordingCache(InMemoryTranslationCache inner) : ITranslationCache
    {
        public readonly List<string[]> Batches = [];
        public readonly List<string> SingleLookups = [];
        public string? FailingText;
        public Exception? WholeBatchFailure;
        public bool UseDefaultBatch;

        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default)
        {
            lock (SingleLookups) SingleLookups.Add(normalizedText);
            return normalizedText == FailingText
                ? Task.FromException<CachedTranslation?>(new IOException("disk I/O error"))
                : inner.LookupAsync(normalizedText, sourceLanguage, targetLanguage, gameProfile, cancellationToken);
        }

        public Task<IReadOnlyList<CacheLookupResult>> LookupManyAsync(IReadOnlyList<string> normalizedTexts,
            string sourceLanguage, string targetLanguage, string gameProfile, CancellationToken cancellationToken = default)
        {
            lock (Batches) Batches.Add([.. normalizedTexts]);
            if (WholeBatchFailure is not null) return Task.FromException<IReadOnlyList<CacheLookupResult>>(WholeBatchFailure);
            if (UseDefaultBatch)
                return ((ITranslationCache)this).DefaultLookupManyAsync(normalizedTexts, sourceLanguage, targetLanguage, gameProfile, cancellationToken);
            var results = normalizedTexts.Select(text => text == FailingText
                    ? new CacheLookupResult(null, new IOException("disk I/O error"))
                    : new CacheLookupResult(inner.LookupAsync(text, sourceLanguage, targetLanguage, gameProfile).Result))
                .ToArray();
            return Task.FromResult<IReadOnlyList<CacheLookupResult>>(results);
        }

        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) => inner.StoreAsync(entry, cancellationToken);
        public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            inner.SaveManualCorrectionAsync(entry, cancellationToken);
        public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default) => inner.GetStatsAsync(cancellationToken);
        public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            inner.ClearAsync(keepManualCorrections, cancellationToken);
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            inner.DeleteOlderThanAsync(cutoff, keepManualCorrections, cancellationToken);
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => inner.ExportJsonAsync(cancellationToken);
        public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default) => inner.ImportJsonAsync(json, cancellationToken);
    }

    private static async Task<(RecordingCache Cache, InMemoryTranslationCache Inner)> CreateCacheAsync(params string[] cached)
    {
        var inner = new InMemoryTranslationCache();
        foreach (var text in cached)
            await inner.StoreAsync(new NewCacheEntry(text, text, "en", "pl", "Z cache: " + text, "Echo", Context: Text.TextReflow.FormatVersion));
        return (new RecordingCache(inner), inner);
    }

    private static TranslationPipeline Create(ITranslationCache cache, EchoProvider provider, UsageTracker? usage = null,
        IGlossaryService? glossary = null) =>
        new(glossary ?? new GlossaryService(), cache, provider, usage ?? new UsageTracker(), new TranslationPipelineOptions());

    [Fact]
    public async Task Cala_klatka_idzie_do_cache_jedna_partia_bez_powtorzen_pustych_i_znanych_tekstow()
    {
        var (cache, _) = await CreateCacheAsync("Inventory", "Map");
        var pipeline = Create(cache, new EchoProvider());
        string[] texts = ["Inventory", "  ", "Map", "Inventory", "Quest  log", "Quest log", "Known"];
        var known = new TranslationOutcome?[texts.Length];
        known[6] = new TranslationOutcome("Known", "Known", "Znane", TranslationOrigin.Cache);

        var outcomes = await pipeline.TranslateAsync(texts, "en", "pl", known);

        // Jedna partia: każdy niepusty tekst raz, w kolejności pierwszego wystąpienia; znany z próby
        // lokalnej pominięty. Ponowny odczyt pojedynczy tylko dla tekstu, który poszedł do dostawcy.
        var batch = Assert.Single(cache.Batches);
        Assert.Equal(["Inventory", "Map", "Quest log"], batch);
        Assert.Equal(["Quest log"], cache.SingleLookups);
        Assert.Equal(texts.Length, outcomes.Count);
        Assert.Equal("Z cache: Inventory", outcomes[0].TranslatedText);
        Assert.Equal("Z cache: Inventory", outcomes[3].TranslatedText);
        Assert.Equal("Inventory", outcomes[3].SourceText);
        Assert.Equal(TranslationOrigin.Unavailable, outcomes[1].Origin);
        Assert.Equal("  ", outcomes[1].SourceText);
        Assert.Equal("PL:Quest log", outcomes[4].TranslatedText);
        Assert.Equal("Quest  log", outcomes[4].SourceText);
        Assert.Equal("Quest log", outcomes[5].SourceText);
        Assert.Equal("Znane", outcomes[6].TranslatedText);
    }

    [Fact]
    public async Task Proba_lokalna_tez_pyta_jedna_partia()
    {
        var (cache, _) = await CreateCacheAsync("Inventory");
        var pipeline = Create(cache, new EchoProvider());

        var local = await pipeline.TranslateLocalAsync(["Inventory", "", "Inventory", "Unknown"], "en", "pl");

        Assert.Equal(["Inventory", "Unknown"], Assert.Single(cache.Batches));
        Assert.Empty(cache.SingleLookups);
        Assert.Equal("Z cache: Inventory", local[0]!.TranslatedText);
        Assert.Equal("Z cache: Inventory", local[2]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Unavailable, local[1]!.Origin);
        Assert.Null(local[3]);
    }

    [Fact]
    public async Task Blad_jednego_odczytu_w_partii_nie_psuje_pozostalych_trafien()
    {
        var (cache, _) = await CreateCacheAsync("Inventory", "Map");
        cache.FailingText = "Map";
        var provider = new EchoProvider();
        var pipeline = Create(cache, provider);

        var outcomes = await pipeline.TranslateAsync(["Inventory", "Map"], "en", "pl");

        Assert.Equal(TranslationOrigin.Cache, outcomes[0].Origin);
        Assert.Equal("PL:Map", outcomes[1].TranslatedText);
        Assert.True(pipeline.IsCacheDegraded);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Blad_calej_partii_jest_traktowany_jak_blad_kazdego_odczytu()
    {
        var (cache, _) = await CreateCacheAsync("Inventory");
        cache.WholeBatchFailure = new InvalidOperationException("database is locked");
        var provider = new EchoProvider();
        var pipeline = Create(cache, provider);

        var outcomes = await pipeline.TranslateAsync(["Inventory", "Map"], "en", "pl");

        // Oba teksty potraktowane jak pudło z błędem bazy; ponowne sprawdzenie pojedynczym
        // odczytem przed wysłaniem (jak dotąd) znajduje „Inventory”, do dostawcy idzie tylko „Map”.
        Assert.True(pipeline.IsCacheDegraded);
        Assert.Equal(["Inventory", "Map"], cache.SingleLookups);
        Assert.Equal(["Z cache: Inventory", "PL:Map"], outcomes.Select(static o => o.TranslatedText));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Anulowanie_przed_odczytem_partii_przerywa_tlumaczenie()
    {
        var (cache, _) = await CreateCacheAsync("Inventory");
        var pipeline = Create(cache, new EchoProvider());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.TranslateAsync(["Inventory"], "en", "pl", cts.Token));
        Assert.Empty(cache.Batches);
    }

    [Fact]
    public async Task Domyslna_partia_interfejsu_pyta_po_kolei_i_zbiera_bledy_pojedynczych_odczytow()
    {
        var (cache, _) = await CreateCacheAsync("Inventory", "Map");
        cache.UseDefaultBatch = true;
        cache.FailingText = "Map";
        ITranslationCache asInterface = cache;

        var results = await asInterface.LookupManyAsync(["Map", "Inventory", "Nothing"], "en", "pl", "");

        Assert.Equal(["Map", "Inventory", "Nothing"], cache.SingleLookups);
        Assert.IsType<IOException>(results[0].Error);
        Assert.Null(results[0].Translation);
        Assert.Equal("Z cache: Inventory", results[1].Translation!.TranslatedText);
        Assert.Null(results[1].Error);
        Assert.Null(results[2].Translation);
        Assert.Null(results[2].Error);
    }

    [Fact]
    public async Task Partia_cache_w_pamieci_daje_to_samo_co_pojedyncze_odczyty_i_liczy_uzycia()
    {
        var single = new InMemoryTranslationCache();
        var batched = new InMemoryTranslationCache();
        foreach (var cache in new[] { single, batched })
        {
            await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Globalne", "Echo"));
            await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Z profilu", "Echo", GameProfile: "poe2"));
            await cache.SaveManualCorrectionAsync(new NewCacheEntry("Bye", "Bye", "en", "pl", "Ręczna", "Echo"));
            await cache.StoreAsync(new NewCacheEntry("Bye", "Bye", "en", "pl", "Z profilu", "Echo", GameProfile: "poe2"));
        }
        string[] texts = ["Hello", "Bye", "Missing", "Hello"];

        var expected = new List<CachedTranslation?>();
        foreach (var text in texts) expected.Add(await single.LookupAsync(text, "en", "pl", "poe2"));
        var actual = await batched.LookupManyAsync(texts, "en", "pl", "poe2");

        Assert.Equal(expected.Select(static e => (e?.TranslatedText, e?.UseCount, e?.GameProfile)),
            actual.Select(static a => (a.Translation?.TranslatedText, a.Translation?.UseCount, a.Translation?.GameProfile)));
        Assert.Equal("Ręczna", actual[1].Translation!.TranslatedText);
        Assert.Equal(3, actual[3].Translation!.UseCount);
        Assert.All(actual, static a => Assert.Null(a.Error));
    }
}

internal static class CacheTestExtensions
{
    /// <summary>Wywołuje domyślną implementację interfejsu (bez nadpisania w klasie testowej).</summary>
    public static Task<IReadOnlyList<CacheLookupResult>> DefaultLookupManyAsync(this ITranslationCache cache,
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, string gameProfile,
        CancellationToken cancellationToken) =>
        new DefaultBatchView(cache).AsInterface.LookupManyAsync(texts, sourceLanguage, targetLanguage, gameProfile, cancellationToken);

    private sealed class DefaultBatchView(ITranslationCache inner) : ITranslationCache
    {
        public ITranslationCache AsInterface => this;
        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default) =>
            inner.LookupAsync(normalizedText, sourceLanguage, targetLanguage, gameProfile, cancellationToken);
        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
