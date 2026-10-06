using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationPipelineCorpusTests
{
    private const string Profile = "lighthouse-game";
    private const string Sentence = "The lighthouse keeper left a note under the blue lamp.";
    private const string SentenceRead = "The 1ighthouse keeper Ieft a note\nunder the bIue lamp.";

    private sealed class RecordingProvider : ITranslationProvider
    {
        public List<IReadOnlyList<string>> Batches { get; } = [];
        public IEnumerable<string> Sent => Batches.SelectMany(static b => b);
        public string Name => "Recording";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            lock (Batches) Batches.Add(texts.ToList());
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class FailingProvider : ITranslationProvider
    {
        public int Calls { get; private set; }
        public string Name => "Failing";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new TranslationException(TranslationFailureKind.NetworkError, "Brak połączenia.");
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(false, "brak"));
    }

    private sealed class CountingCache(InMemoryTranslationCache inner) : ITranslationCache
    {
        public List<string> Looked { get; } = [];

        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default)
        {
            Looked.Add(normalizedText);
            return inner.LookupAsync(normalizedText, sourceLanguage, targetLanguage, gameProfile, cancellationToken);
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

    private sealed record Setup(TranslationPipeline Pipeline, RecordingProvider Provider, InMemoryTranslationCache Cache, UsageTracker Usage, GlossaryService Glossary);

    private static Setup Create(bool corpus = true, bool split = false, bool cacheOnly = false, ITranslationCache? cacheOverride = null,
        InMemoryTranslationCache? cache = null)
    {
        cache ??= new InMemoryTranslationCache();
        var provider = new RecordingProvider();
        var usage = new UsageTracker();
        var glossary = new GlossaryService();
        var pipeline = new TranslationPipeline(glossary, cacheOverride ?? cache, provider, usage, new TranslationPipelineOptions
        {
            GameProfile = Profile,
            CacheOnlyMode = cacheOnly,
            Corpus = corpus ? TranslationUnitPlannerTests.Snapper() : null,
            SplitParagraphs = split || corpus,
        });
        return new Setup(pipeline, provider, cache, usage, glossary);
    }

    private static Task StoreCorpus(InMemoryTranslationCache cache, string text, string translation) =>
        cache.StoreAsync(new NewCacheEntry(text, CorpusTranslationKey.Normalize(text), "en", "pl", translation, "Recording",
            GameProfile: Profile, Context: TranslationCacheContext.Build(TranslationQualityFlags.None, source: TranslationCacheContext.CorpusSource)));

    [Fact]
    public async Task Bez_korpusu_blok_wieloakapitowy_idzie_do_dostawcy_jak_dotad()
    {
        var setup = Create(corpus: false, split: false);

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["Options\nQuit"], "en", "pl"));

        Assert.Equal(["Options\nQuit"], setup.Provider.Sent);
        Assert.Equal("PL:Options\nQuit", outcome.TranslatedText);
        Assert.Null(outcome.Parts);
        Assert.Null(outcome.CacheKey);
        Assert.False(setup.Pipeline.IsExactCorpusText("Inspect"));
    }

    [Fact]
    public async Task Klucze_po_akapitach_tlumacza_akapity_osobno_i_trafiaja_w_innym_ukladzie_bloku()
    {
        var setup = Create(corpus: false, split: true);

        var first = Assert.Single(await setup.Pipeline.TranslateAsync(["Options\nQuit"], "en", "pl"));
        var second = Assert.Single(await setup.Pipeline.TranslateAsync(["Quit\nOptions"], "en", "pl"));

        Assert.Equal(["Options", "Quit"], setup.Provider.Sent);
        Assert.Equal("PL:Options\nPL:Quit", first.TranslatedText);
        Assert.Equal(TranslationOrigin.Provider, first.Origin);
        Assert.Equal("PL:Quit\nPL:Options", second.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, second.Origin);
        Assert.Equal("Quit\nOptions", second.NormalizedText);
    }

    [Fact]
    public async Task Odczyt_z_bledami_trafia_w_tlumaczenie_korpusu_i_zachowuje_wiersze()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, Sentence, "Latarnik zostawił notatkę pod niebieską lampą.");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));

        Assert.Empty(setup.Provider.Batches);
        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Equal(2, outcome.TranslatedText!.Split('\n').Length);
        Assert.Equal("Latarnik zostawił notatkę pod niebieską lampą.", outcome.TranslatedText.Replace('\n', ' '));
        Assert.Equal(SentenceRead, outcome.NormalizedText);
        Assert.Null(outcome.CacheKey);
        var part = Assert.Single(outcome.Parts!);
        Assert.True(part.FromCorpus);
        Assert.Equal(1, setup.Usage.CacheHits);
    }

    [Fact]
    public async Task Brak_w_cache_wysyla_tekst_kanoniczny_i_kolejny_wariant_odczytu_jest_lokalny()
    {
        var setup = Create();

        var first = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));
        var second = Assert.Single(await setup.Pipeline.TranslateAsync(["The lighthouse keeper left a nole under the blue lamp"], "en", "pl"));

        Assert.Equal([Sentence], setup.Provider.Sent);
        Assert.Equal(TranslationOrigin.Provider, first.Origin);
        Assert.Equal(TranslationOrigin.Cache, second.Origin);
        Assert.Equal("PL:" + Sentence, second.TranslatedText);
        var stored = await setup.Cache.LookupAsync(Sentence, "en", "pl", string.Empty);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task Korekta_reczna_bloku_wygrywa_z_korpusem()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, Sentence, "Z korpusu");
        await setup.Cache.SaveManualCorrectionAsync(new NewCacheEntry(SentenceRead, SentenceRead, "en", "pl", "Korekta gracza", "manual", Profile));

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));

        Assert.Equal("Korekta gracza", outcome.TranslatedText);
        Assert.Empty(setup.Provider.Batches);
    }

    [Fact]
    public async Task Korekta_zapisana_pod_kluczem_kanonicznym_dziala_dla_innych_odczytow()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, Sentence, "Z korpusu");
        await setup.Cache.SaveManualCorrectionAsync(new NewCacheEntry(Sentence, Sentence, "en", "pl", "Poprawione zdanie", "manual", Profile));

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["The lighthouse keeper left a nole under the blue lamp"], "en", "pl"));

        Assert.Equal("Poprawione zdanie", outcome.TranslatedText);
        Assert.Null(outcome.CacheKey);
    }

    [Fact]
    public async Task Korekta_bloku_dopasowanego_dokladnie_idzie_pod_klucz_kanoniczny()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, Sentence, "Z korpusu");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["THE LIGHTHOUSE KEEPER LEFT A NOTE\nUNDER THE BLUE LAMP."], "en", "pl"));

        Assert.Equal(Sentence, outcome.CacheKey);
        Assert.True(Assert.Single(outcome.Parts!).FromCorpus);
    }

    [Fact]
    public async Task Korekta_bloku_dopasowanego_przyblizeniem_idzie_pod_klucz_odczytu()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, Sentence, "Z korpusu");
        var read = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));
        Assert.True(Assert.Single(read.Parts!).FromCorpus);

        var key = read.CacheKey ?? read.NormalizedText;
        await setup.Cache.SaveManualCorrectionAsync(new NewCacheEntry(key, key, "en", "pl", "Korekta odczytu", "manual", Profile));
        var same = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));
        var other = Assert.Single(await setup.Pipeline.TranslateAsync(["The lighthouse keeper left a nole under the blue lamp"], "en", "pl"));
        var exact = Assert.Single(await setup.Pipeline.TranslateAsync([Sentence], "en", "pl"));

        Assert.Equal(SentenceRead, key);
        Assert.Equal("Korekta odczytu", same.TranslatedText);
        Assert.Equal("Z korpusu", other.TranslatedText);
        Assert.Equal("Z korpusu", exact.TranslatedText);
    }

    [Fact]
    public async Task Nieaktualny_wpis_calego_odczytu_jest_zapasem_przy_bledzie_dostawcy()
    {
        foreach (var probeFirst in new[] { false, true })
        {
            var cache = new InMemoryTranslationCache();
            await cache.StoreAsync(new NewCacheEntry(SentenceRead, SentenceRead, "en", "pl", "Stare tłumaczenie", "Failing", GameProfile: Profile));
            var provider = new FailingProvider();
            var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(), new TranslationPipelineOptions
            {
                GameProfile = Profile,
                Corpus = TranslationUnitPlannerTests.Snapper(),
                SplitParagraphs = true,
            });
            var texts = new[] { SentenceRead };

            var local = probeFirst ? await pipeline.TranslateLocalAsync(texts, "en", "pl") : null;
            var outcome = Assert.Single(await pipeline.TranslateAsync(texts, "en", "pl", local));

            Assert.Null(local?[0]);
            Assert.Equal(1, provider.Calls);
            Assert.Equal("Stare tłumaczenie", outcome.TranslatedText);
            Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
            Assert.Null(outcome.ErrorMessage);
            Assert.Equal(OutcomeIssue.None, outcome.Issue);
            Assert.Null(outcome.CacheKey);
        }
    }

    [Fact]
    public async Task Nieaktualny_wpis_calego_odczytu_nie_zastepuje_nowego_tlumaczenia_jednostek()
    {
        var setup = Create();
        await setup.Cache.StoreAsync(new NewCacheEntry(SentenceRead, SentenceRead, "en", "pl", "Stare tłumaczenie", "Recording", GameProfile: Profile));

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));

        Assert.Equal([Sentence], setup.Provider.Sent);
        Assert.Equal("PL:" + Sentence, outcome.TranslatedText!.Replace('\n', ' '));
        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
    }

    [Fact]
    public async Task Stary_wpis_calego_odczytu_jest_zapasem_gdy_jednostki_nie_sa_lokalne()
    {
        var setup = Create();
        await setup.Cache.StoreAsync(new NewCacheEntry(SentenceRead, SentenceRead, "en", "pl", "Stare tłumaczenie", "Recording",
            Context: TextReflow.FormatVersion));

        var fallback = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));
        Assert.Equal("Stare tłumaczenie", fallback.TranslatedText);
        Assert.Empty(setup.Provider.Batches);

        await StoreCorpus(setup.Cache, Sentence, "Z korpusu");
        var preferred = Assert.Single(await setup.Pipeline.TranslateAsync([SentenceRead], "en", "pl"));
        Assert.Equal("Z korpusu", preferred.TranslatedText!.Replace('\n', ' '));
    }

    [Fact]
    public async Task Blok_mieszany_wysyla_tylko_nieznana_czesc()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, "Items", "Przedmioty");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["Items\nSomething completely new appears here"], "en", "pl"));

        Assert.Equal(["Something completely new appears here"], setup.Provider.Sent);
        Assert.Equal("Przedmioty\nPL:Something completely new appears here", outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
        Assert.Null(outcome.CacheKey);
        Assert.Equal([TranslationOrigin.Cache, TranslationOrigin.Provider], outcome.Parts!.Select(static p => p.Origin!.Value));
    }

    [Fact]
    public async Task Cache_only_zglasza_brak_dla_calego_bloku()
    {
        var setup = Create(cacheOnly: true);
        await StoreCorpus(setup.Cache, "Items", "Przedmioty");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["Items\nSomething completely new appears here"], "en", "pl"));

        Assert.Null(outcome.TranslatedText);
        Assert.Equal(OutcomeIssue.CacheOnlyMiss, outcome.Issue);
        Assert.Equal("Items\nSomething completely new appears here", outcome.NormalizedText);
        Assert.Empty(setup.Provider.Batches);
    }

    [Fact]
    public async Task Proba_lokalna_przekazuje_jednostki_do_tlumaczenia_bez_drugiego_odczytu()
    {
        var inner = new InMemoryTranslationCache();
        var counting = new CountingCache(inner);
        var setup = Create(cacheOverride: counting, cache: inner);
        await StoreCorpus(inner, "Items", "Przedmioty");
        var texts = new[] { "Items\nSomething completely new appears here" };

        var local = await setup.Pipeline.TranslateLocalAsync(texts, "en", "pl");
        Assert.Null(local[0]);
        var lookedBefore = counting.Looked.Count(static t => t == "Items");
        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(texts, "en", "pl", local));

        Assert.Equal(1, lookedBefore);
        Assert.Equal(1, counting.Looked.Count(static t => t == "Items"));
        Assert.Equal("Przedmioty\nPL:Something completely new appears here", outcome.TranslatedText);
        Assert.Equal(1, setup.Usage.CacheHits);
    }

    [Fact]
    public async Task W_pelni_lokalny_blok_liczy_trafienia_kazdej_czesci()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, "Items", "Przedmioty");
        await StoreCorpus(setup.Cache, "Back", "Wstecz");
        setup.Glossary.AddTerm(new GlossaryTerm("Inspect", "Zbadaj"));

        var local = await setup.Pipeline.TranslateLocalAsync(["E Inspect\nItems\nBack"], "en", "pl");

        Assert.Equal("E Zbadaj\nPrzedmioty\nWstecz", local[0]!.TranslatedText);
        Assert.Equal([true, false, false, false], local[0]!.Parts!.Select(static p => p.IsLiteral));
        Assert.Equal(2, setup.Usage.CacheHits);
        Assert.Equal(1, setup.Usage.GlossaryHits);
    }

    [Fact]
    public async Task Prefiks_mowcy_zostaje_w_wyswietlanym_tekscie()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, "Watch out for the falling rocks above the tunnel entrance.", "Uważaj na spadające skały nad wejściem do tunelu.");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(
            ["Captain: Watch out for the falling rocks above the tunnel entrance."], "en", "pl"));

        Assert.Equal("Captain: Uważaj na spadające skały nad wejściem do tunelu.", outcome.TranslatedText);
        Assert.Null(outcome.CacheKey);
    }

    [Fact]
    public void Dokladny_tekst_korpusu_moze_ominac_filtr_smieci()
    {
        var withCorpus = Create().Pipeline;
        var withoutCorpus = Create(corpus: false).Pipeline;

        Assert.True(withCorpus.IsExactCorpusText("ITEMS"));
        Assert.True(withCorpus.IsExactCorpusText("  Pick   Up "));
        Assert.False(withCorpus.IsExactCorpusText("Item"));
        Assert.False(withCorpus.IsExactCorpusText("Welcome back, {0}! Ready for the next exam?"));
        Assert.False(withCorpus.IsExactCorpusText(""));
        Assert.False(withoutCorpus.IsExactCorpusText("Items"));
    }

    [Fact]
    public async Task Ten_sam_tekst_co_w_korpusie_idzie_dawna_sciezka_bez_czesci()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, "Inspect", "Zbadaj");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["Inspect"], "en", "pl"));

        Assert.Equal("Zbadaj", outcome.TranslatedText);
        Assert.Null(outcome.Parts);
    }

    [Fact]
    public async Task Bez_aktywnego_profilu_wpisy_korpusu_nie_sa_czytane()
    {
        var cache = new InMemoryTranslationCache();
        await StoreCorpus(cache, Sentence, "Z korpusu");
        var provider = new RecordingProvider();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { Corpus = TranslationUnitPlannerTests.Snapper(), SplitParagraphs = true });

        var outcome = Assert.Single(await pipeline.TranslateAsync([SentenceRead], "en", "pl"));

        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
        Assert.Equal([Sentence], provider.Sent);
    }
}
