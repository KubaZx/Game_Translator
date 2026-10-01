using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Awaryjna pamięć wyników przy bazie, która czyta, ale nie zapisuje (pełny dysk, plik tylko
/// do odczytu), oraz stary wynik zapasowy przy równoległych wywołaniach z tym samym tekstem.
/// </summary>
public class TranslationPipelineCombinedFixesTests
{
    private static readonly string MultiLine = TextNormalizer.Normalize("Talk to the\nblacksmith about the sword.");

    /// <summary>Odczyt działa (SELECT), zapis zawodzi (INSERT przy pełnym dysku).</summary>
    private sealed class ReadOnlyCache(InMemoryTranslationCache inner) : ITranslationCache
    {
        public int Stores;

        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default) =>
            inner.LookupAsync(normalizedText, sourceLanguage, targetLanguage, gameProfile, cancellationToken);

        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Stores);
            return Task.FromException(new IOException("disk full"));
        }

        public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class GatedProvider(Func<string, string> respond) : ITranslationProvider
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public Task Gate { get; set; } = Task.CompletedTask;

        public string Name => "Gated";
        public bool RequiresApiKey => false;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            await Gate.ConfigureAwait(false);
            return texts.Select(respond).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static TranslationPipeline Create(ITranslationCache cache, ITranslationProvider provider) =>
        new(new GlossaryService(), cache, provider, new UsageTracker(), new TranslationPipelineOptions());

    [Fact]
    public async Task Przy_bazie_bez_zapisu_ten_sam_tekst_nie_jest_wysylany_ponownie()
    {
        var cache = new ReadOnlyCache(new InMemoryTranslationCache());
        var provider = new GatedProvider(t => "PL:" + t);
        var pipeline = Create(cache, provider);

        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 3; i++) outcomes.Add(Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl")));

        Assert.True(pipeline.IsCacheDegraded);
        Assert.Equal(1, provider.CallCount);
        Assert.All(outcomes, o => Assert.Equal("PL:Hello there", o.TranslatedText));
        Assert.Equal(TranslationOrigin.Cache, outcomes[2].Origin);
    }

    [Fact]
    public async Task Przy_bazie_bez_zapisu_szybka_sciezka_live_widzi_awaryjna_pamiec()
    {
        var pipeline = Create(new ReadOnlyCache(new InMemoryTranslationCache()), new GatedProvider(t => "PL:" + t));
        await pipeline.TranslateAsync(["Hello there"], "en", "pl");

        var local = Assert.Single(await pipeline.TranslateLocalAsync(["Hello there"], "en", "pl"));

        Assert.NotNull(local);
        Assert.Equal("PL:Hello there", local.TranslatedText);
    }

    [Fact]
    public async Task Nieaktualny_wpis_przy_bazie_bez_zapisu_jest_zastepowany_tylko_raz()
    {
        var inner = new InMemoryTranslationCache();
        await inner.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "STARE", "DeepL"));
        var provider = new GatedProvider(_ => "Porozmawiaj z kowalem o mieczu.");
        var pipeline = Create(new ReadOnlyCache(inner), provider);

        for (var i = 0; i < 3; i++)
        {
            var outcome = Assert.Single(await pipeline.TranslateAsync([MultiLine], "en", "pl"));
            Assert.NotEqual("STARE", outcome.TranslatedText);
        }

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Aktualny_wpis_bazy_wygrywa_z_awaryjna_pamiecia()
    {
        var inner = new InMemoryTranslationCache();
        var cache = new ReadOnlyCache(inner);
        var pipeline = Create(cache, new GatedProvider(t => "PL:" + t));
        await pipeline.TranslateAsync(["Hello there"], "en", "pl");
        await inner.StoreAsync(new NewCacheEntry("Hello there", "Hello there", "en", "pl", "Z BAZY", "DeepL",
            Context: TextReflow.FormatVersion));

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));

        Assert.Equal("Z BAZY", outcome.TranslatedText);
    }

    [Fact]
    public async Task Rownolegli_wolajacy_z_nieaktualnym_tekstem_dostaja_stary_wynik_gdy_dostawca_zawiedzie()
    {
        // Właściciel zapytania i czekający na nie mają osobne wpisy zapasowe — wcześniej
        // wspólny słownik oddawał stary wynik tylko temu, kto pierwszy po niego sięgnął.
        for (var round = 0; round < 20; round++)
        {
            var cache = new InMemoryTranslationCache();
            await cache.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "STARE", "DeepL"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new GatedProvider(_ => throw new TranslationException(TranslationFailureKind.NetworkError, "offline"))
            {
                Gate = gate.Task,
            };
            var pipeline = Create(cache, provider);

            var owner = Task.Run(() => pipeline.TranslateAsync([MultiLine], "en", "pl"));
            while (provider.CallCount == 0) await Task.Delay(1);
            var waiter = Task.Run(() => pipeline.TranslateAsync([MultiLine], "en", "pl"));
            await Task.Delay(20);
            gate.SetResult();

            foreach (var call in new[] { owner, waiter })
            {
                var outcome = Assert.Single(await call);
                Assert.Equal("STARE", outcome.TranslatedText);
                Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
                Assert.Equal(OutcomeIssue.None, outcome.Issue);
            }
            Assert.Equal(1, provider.CallCount);
        }
    }
}
