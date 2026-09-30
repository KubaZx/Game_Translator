using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationPipelineCacheFailureTests
{
    /// <summary>Cache, którego baza jest niedostępna (pełny dysk, blokada, plik tylko do odczytu).</summary>
    private sealed class BrokenCache(Exception lookupError) : ITranslationCache
    {
        public int Lookups;

        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromException<CachedTranslation?>(lookupError);
        }

        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("disk full"));

        public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    [Fact]
    public async Task Blad_odczytu_cache_jest_traktowany_jak_brak_wpisu()
    {
        var cache = new BrokenCache(new InvalidOperationException("database is locked"));
        var provider = new CountingProvider();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(), new TranslationPipelineOptions());

        var first = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));
        var second = Assert.Single(await pipeline.TranslateAsync(["General Kenobi"], "en", "pl"));

        Assert.Equal("PL:Hello there", first.TranslatedText);
        Assert.Equal(TranslationOrigin.Provider, first.Origin);
        Assert.Equal("PL:General Kenobi", second.TranslatedText);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task W_trybie_cache_only_zepsuty_cache_nie_wysyla_nic_do_dostawcy()
    {
        var cache = new BrokenCache(new IOException("disk I/O error"));
        var provider = new CountingProvider();
        var options = new TranslationPipelineOptions { CacheOnlyMode = true };
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(), options);

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));

        Assert.Null(outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Slownik_dziala_mimo_zepsutego_cache()
    {
        var cache = new BrokenCache(new IOException("disk I/O error"));
        var provider = new CountingProvider();
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));
        var pipeline = new TranslationPipeline(glossary, cache, provider, new UsageTracker(), new TranslationPipelineOptions());

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Armour"], "en", "pl"));

        Assert.Equal("Pancerz", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Anulowanie_odczytu_cache_nie_jest_polykane()
    {
        var cache = new BrokenCache(new OperationCanceledException());
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, new CountingProvider(), new UsageTracker(),
            new TranslationPipelineOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.TranslateAsync(["Hello"], "en", "pl"));
    }
}
