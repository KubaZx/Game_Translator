using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationPipelineTests
{
    private sealed class FakeProvider : ITranslationProvider
    {
        private int _callCount;
        public int CallCount => _callCount;
        public List<string> SentTexts { get; } = [];
        public TranslationException? AlwaysThrow { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public string Name => "Fake";
        public bool RequiresApiKey => false;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }
            if (AlwaysThrow is not null) throw AlwaysThrow;

            lock (SentTexts)
            {
                SentTexts.AddRange(texts);
            }
            return texts.Select(static t => "PL:" + t).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static (TranslationPipeline Pipeline, FakeProvider Provider, InMemoryTranslationCache Cache, UsageTracker Usage, GlossaryService Glossary)
        CreatePipeline(TranslationPipelineOptions? options = null)
    {
        var glossary = new GlossaryService();
        var cache = new InMemoryTranslationCache();
        var provider = new FakeProvider();
        var usage = new UsageTracker();
        var pipeline = new TranslationPipeline(glossary, cache, provider, usage, options ?? new TranslationPipelineOptions());
        return (pipeline, provider, cache, usage, glossary);
    }

    [Fact]
    public async Task Slownik_ma_pierwszenstwo_przed_API()
    {
        var (pipeline, provider, _, usage, glossary) = CreatePipeline();
        glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));

        var outcomes = await pipeline.TranslateAsync(["Armour"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Glossary, outcome.Origin);
        Assert.Equal("Pancerz", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(1, usage.GlossaryHits);
    }

    [Fact]
    public async Task Trafienie_w_cache_nie_wywoluje_API()
    {
        var (pipeline, provider, cache, usage, _) = CreatePipeline();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Fake"));

        var outcomes = await pipeline.TranslateAsync(["Hello"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Equal("Cześć", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(1, usage.CacheHits);
    }

    [Fact]
    public async Task Wynik_z_API_trafia_do_cache_i_nie_jest_wysylany_ponownie()
    {
        var (pipeline, provider, _, _, _) = CreatePipeline();

        var first = await pipeline.TranslateAsync(["Hello world"], "en", "pl");
        var second = await pipeline.TranslateAsync(["Hello world"], "en", "pl");

        Assert.Equal(TranslationOrigin.Provider, first[0].Origin);
        Assert.Equal(TranslationOrigin.Cache, second[0].Origin);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Duplikaty_w_jednej_partii_wysylaja_tekst_tylko_raz()
    {
        var (pipeline, provider, _, _, _) = CreatePipeline();

        var outcomes = await pipeline.TranslateAsync(["Hello", "World", "Hello"], "en", "pl");

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(outcomes[0].TranslatedText, outcomes[2].TranslatedText);
        Assert.Equal(2, provider.SentTexts.Count);
    }

    [Fact]
    public async Task Rownolegle_zadania_tego_samego_tekstu_deduplikuja_zapytania()
    {
        var (pipeline, provider, _, _, _) = CreatePipeline();
        provider.Delay = TimeSpan.FromMilliseconds(120);

        var first = pipeline.TranslateAsync(["Concurrent text"], "en", "pl");
        var second = pipeline.TranslateAsync(["Concurrent text"], "en", "pl");
        var results = await Task.WhenAll(first, second);

        Assert.Equal("PL:Concurrent text", results[0][0].TranslatedText);
        Assert.Equal("PL:Concurrent text", results[1][0].TranslatedText);
        Assert.Single(provider.SentTexts);
    }

    [Fact]
    public async Task Tryb_CacheOnly_niczego_nie_wysyla()
    {
        var (pipeline, provider, _, _, _) = CreatePipeline(new TranslationPipelineOptions { CacheOnlyMode = true });

        var outcomes = await pipeline.TranslateAsync(["Nieznany tekst"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
        Assert.NotNull(outcome.ErrorMessage);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Limit_znakow_sesji_blokuje_wysylke()
    {
        var (pipeline, provider, _, usage, _) = CreatePipeline();
        usage.SessionCharacterLimit = 5;

        var outcomes = await pipeline.TranslateAsync(["Za długi tekst na limit"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Blad_dostawcy_zwraca_czytelny_komunikat_zamiast_wyjatku()
    {
        var (pipeline, provider, _, usage, _) = CreatePipeline();
        provider.AlwaysThrow = new TranslationException(TranslationFailureKind.InvalidApiKey, "403");

        var outcomes = await pipeline.TranslateAsync(["Hello"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
        Assert.Contains("klucz", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, usage.FailedRequests);
    }

    [Fact]
    public async Task Anulowanie_przerywa_tlumaczenie()
    {
        var (pipeline, provider, _, _, _) = CreatePipeline();
        provider.Delay = TimeSpan.FromSeconds(30);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.TranslateAsync(["Hello"], "en", "pl", cts.Token));
    }

    [Fact]
    public async Task Licznik_znakow_API_rosnie_po_wysylce()
    {
        var (pipeline, _, _, usage, _) = CreatePipeline();

        await pipeline.TranslateAsync(["Hello"], "en", "pl");

        Assert.Equal(1, usage.ApiRequests);
        Assert.Equal(5, usage.ApiCharacters);
    }

    [Fact]
    public async Task Reczna_korekta_wygrywa_ze_slownikiem()
    {
        var (pipeline, provider, cache, _, glossary) = CreatePipeline();
        glossary.AddTerm(new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));
        await cache.SaveManualCorrectionAsync(new NewCacheEntry("Energy Shield", "Energy Shield", "en", "pl", "Bariera energii", "manual"));

        var outcomes = await pipeline.TranslateAsync(["Energy Shield"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Equal("Bariera energii", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Wyniki_API_trafiaja_do_cache_globalnego_takze_przy_aktywnym_profilu()
    {
        var glossary = new GlossaryService();
        var cache = new InMemoryTranslationCache();
        var provider = new FakeProvider();
        var usage = new UsageTracker();
        var withProfile = new TranslationPipeline(glossary, cache, provider, usage,
            new TranslationPipelineOptions { GameProfile = "poe2" });
        var withoutProfile = new TranslationPipeline(glossary, cache, provider, usage,
            new TranslationPipelineOptions());

        await withProfile.TranslateAsync(["Hello"], "en", "pl");
        var second = await withoutProfile.TranslateAsync(["Hello"], "en", "pl");

        Assert.Equal(TranslationOrigin.Cache, second[0].Origin);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Wpisy_Mocka_nie_udaja_tlumaczen_po_przelaczeniu_na_prawdziwego_dostawce()
    {
        var (pipeline, provider, cache, _, _) = CreatePipeline();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "[PL] Hello", MockTranslationProvider.ProviderName));

        var outcomes = await pipeline.TranslateAsync(["Hello"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
        Assert.Equal("PL:Hello", outcome.TranslatedText);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Wpisy_Mocka_dalej_dzialaja_gdy_Mock_jest_aktywnym_dostawca()
    {
        var glossary = new GlossaryService();
        var cache = new InMemoryTranslationCache();
        var usage = new UsageTracker();
        var pipeline = new TranslationPipeline(glossary, cache, new MockTranslationProvider(), usage, new TranslationPipelineOptions());
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "[PL] Hello", MockTranslationProvider.ProviderName));

        var outcomes = await pipeline.TranslateAsync(["Hello"], "en", "pl");

        Assert.Equal(TranslationOrigin.Cache, outcomes[0].Origin);
    }

    [Fact]
    public async Task Mock_provider_dziala_deterministycznie()
    {
        var mock = new MockTranslationProvider();

        var result = await mock.TranslateBatchAsync(["Hello"], "en", "pl");

        Assert.Equal("[PL] Hello", result[0]);
        var status = await mock.TestConnectionAsync();
        Assert.True(status.IsOk);
    }

    private sealed class ContextualProvider : IContextualTranslationProvider
    {
        public List<TranslationContext> Contexts { get; } = [];
        public int PlainCalls { get; private set; }

        public string Name => "Contextual";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            PlainCalls++;
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());
        }

        public Task<IReadOnlyList<string>> TranslateWithContextAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            TranslationContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "CTX:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    [Fact]
    public async Task Dostawca_kontekstowy_dostaje_nazwe_gry_i_terminy_z_partii()
    {
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));
        glossary.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"));
        var provider = new ContextualProvider();
        var pipeline = new TranslationPipeline(
            glossary, new InMemoryTranslationCache(), provider, new UsageTracker(),
            new TranslationPipelineOptions { GameName = "Path of Exile 2" });

        var outcomes = await pipeline.TranslateAsync(["+40 to maximum Energy Shield"], "en", "pl");

        Assert.Equal("CTX:+40 to maximum Energy Shield", outcomes[0].TranslatedText);
        Assert.Equal(0, provider.PlainCalls);
        var context = Assert.Single(provider.Contexts);
        Assert.Equal("Path of Exile 2", context.GameName);
        var term = Assert.Single(context.Terms);
        Assert.Equal("Tarcza energetyczna", term.Target);
    }

    [Fact]
    public async Task Caly_slownik_trafia_do_kontekstu_tylko_gdy_partia_zawiera_termin()
    {
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));
        glossary.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"));
        var provider = new ContextualProvider();
        var pipeline = new TranslationPipeline(
            glossary, new InMemoryTranslationCache(), provider, new UsageTracker(), new TranslationPipelineOptions());

        await pipeline.TranslateAsync(["Nothing to see here"], "en", "pl");
        await pipeline.TranslateAsync(["+40 to maximum Energy Shield"], "en", "pl");

        Assert.Empty(provider.Contexts[0].GlossaryTerms);
        Assert.Equal(2, provider.Contexts[1].GlossaryTerms.Count);
    }

    [Fact]
    public async Task Ostatnie_wyslane_linie_trafiaja_jako_kontekst_kolejnej_partii()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("From cache", "From cache", "en", "pl", "Z cache", "Contextual"));
        var provider = new ContextualProvider();
        var pipeline = new TranslationPipeline(
            new GlossaryService(), cache, provider, new UsageTracker(), new TranslationPipelineOptions());

        await pipeline.TranslateAsync(["Where were you?"], "en", "pl");
        await pipeline.TranslateAsync(["From cache", "I'm ready."], "en", "pl");
        await pipeline.TranslateAsync(["I'm ready.", "Let's go."], "en", "pl");

        Assert.Empty(provider.Contexts[0].RecentTexts);
        // Tekst z cache nie był wysłany — nie trafia do kontekstu.
        Assert.Equal(["Where were you?"], provider.Contexts[1].RecentTexts);
        // „I'm ready.” pochodzi już z cache — do dostawcy idzie tylko „Let's go.”,
        // a poprzednia kwestia jest dla niej kontekstem.
        Assert.Equal(["Where were you?", "I'm ready."], provider.Contexts[2].RecentTexts);
    }

    [Fact]
    public async Task Kontekst_ostatnich_linii_ma_limit()
    {
        var provider = new ContextualProvider();
        var pipeline = new TranslationPipeline(
            new GlossaryService(), new InMemoryTranslationCache(), provider, new UsageTracker(),
            new TranslationPipelineOptions { MaxRecentContextTexts = 3 });

        for (var i = 1; i <= 5; i++) await pipeline.TranslateAsync([$"Line {i}"], "en", "pl");

        Assert.Equal(["Line 2", "Line 3", "Line 4"], provider.Contexts[^1].RecentTexts);
    }

    [Fact]
    public async Task Dostawca_kontekstowy_bez_profilu_i_terminow_dostaje_pusty_kontekst()
    {
        var provider = new ContextualProvider();
        var pipeline = new TranslationPipeline(
            new GlossaryService(), new InMemoryTranslationCache(), provider, new UsageTracker(),
            new TranslationPipelineOptions());

        await pipeline.TranslateAsync(["Hello there"], "en", "pl");

        Assert.True(Assert.Single(provider.Contexts).IsEmpty);
    }
}
