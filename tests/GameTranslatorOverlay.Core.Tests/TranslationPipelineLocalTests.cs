using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// TranslateLocalAsync: w pełni znana klatka live nie czeka na kolejkę tłumaczeń —
/// ale tylko wtedy, gdy wynik jest DOKŁADNIE taki, jaki dałby TranslateAsync bez dostawcy.
/// </summary>
public class TranslationPipelineLocalTests
{
    private const string Profile = "witcher";

    private sealed class CountingProvider(Func<string, string>? respond = null, TranslationException? failure = null)
        : ITranslationProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Name => "Counting";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (failure is not null) throw failure;
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => respond?.Invoke(t) ?? "PL:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class BrokenCache : ITranslationCache
    {
        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default) =>
            Task.FromException<CachedTranslation?>(new IOException("file is not a database"));

        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("file is not a database"));

        public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static TranslationPipeline Create(
        ITranslationCache cache, ITranslationProvider provider, UsageTracker? usage = null,
        GlossaryService? glossary = null, string profile = "", bool cacheOnly = false) =>
        new(glossary ?? new GlossaryService(), cache, provider, usage ?? new UsageTracker(),
            new TranslationPipelineOptions { GameProfile = profile, CacheOnlyMode = cacheOnly });

    private static TranslationException Offline() => new(TranslationFailureKind.NetworkError, "offline");

    [Fact]
    public async Task Cala_znana_klatka_nie_wola_dostawcy_i_zachowuje_kolejnosc()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Counting",
            Context: TextReflow.FormatVersion));
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));
        var provider = new CountingProvider();
        var pipeline = Create(cache, provider, glossary: glossary);

        var outcomes = await pipeline.TranslateLocalAsync(["Hello", "Armour", " Hello "], "en", "pl");

        Assert.Equal(0, provider.Calls);
        Assert.Equal(3, outcomes.Count);
        Assert.Equal("Cześć", outcomes[0]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, outcomes[0]!.Origin);
        Assert.Equal("Pancerz", outcomes[1]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Glossary, outcomes[1]!.Origin);
        // Duplikat po normalizacji dostaje ten sam wynik, ale ze swoim tekstem źródłowym.
        Assert.Equal("Cześć", outcomes[2]!.TranslatedText);
        Assert.Equal(" Hello ", outcomes[2]!.SourceText);
    }

    [Fact]
    public async Task Priorytet_korekta_reczna_potem_slownik_potem_cache()
    {
        var cache = new InMemoryTranslationCache();
        await cache.SaveManualCorrectionAsync(new NewCacheEntry("Energy Shield", "Energy Shield", "en", "pl", "Bariera energii", "manual"));
        await cache.StoreAsync(new NewCacheEntry("Armour", "Armour", "en", "pl", "Zbroja", "Counting"));
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));
        glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));
        var pipeline = Create(cache, new CountingProvider(), glossary: glossary);

        var outcomes = await pipeline.TranslateLocalAsync(["Energy Shield", "Armour"], "en", "pl");
        var full = await pipeline.TranslateAsync(["Energy Shield", "Armour"], "en", "pl");

        Assert.Equal("Bariera energii", outcomes[0]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, outcomes[0]!.Origin);
        Assert.Equal("Pancerz", outcomes[1]!.TranslatedText);
        Assert.Equal(TranslationOrigin.Glossary, outcomes[1]!.Origin);
        Assert.Equal(full.Select(static o => (o.TranslatedText, o.Origin)),
            outcomes.Select(static o => (o!.TranslatedText, o.Origin)));
    }

    [Fact]
    public async Task Nieznany_tekst_to_null_bez_dostawcy_i_bez_liczenia_trafien()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Counting"));
        var usage = new UsageTracker();
        var provider = new CountingProvider();
        var pipeline = Create(cache, provider, usage);

        var outcomes = await pipeline.TranslateLocalAsync(["Hello", "Brand new line"], "en", "pl");

        Assert.NotNull(outcomes[0]);
        Assert.Null(outcomes[1]);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, usage.CacheHits);

        // Wołający przechodzi wtedy przez pełną ścieżkę — trafienie liczy się dokładnie raz.
        await pipeline.TranslateAsync(["Hello", "Brand new line"], "en", "pl");
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, usage.CacheHits);
    }

    [Fact]
    public async Task Trafienia_calej_lokalnej_klatki_sa_liczone_raz()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Counting"));
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));
        var usage = new UsageTracker();
        var pipeline = Create(cache, new CountingProvider(), usage, glossary);

        await pipeline.TranslateLocalAsync(["Hello", "Armour", "Hello"], "en", "pl");

        Assert.Equal(1, usage.CacheHits);
        Assert.Equal(1, usage.GlossaryHits);
    }

    [Fact]
    public async Task Wpis_z_problemem_jakosci_nie_jest_lokalny_i_nie_zostawia_zapasu()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("You have 150 gold.", "You have 150 gold.", "en", "pl", "Masz 15 złota.", "Counting",
            Context: TranslationCacheContext.Build(TranslationQualityFlags.NumbersChanged)));
        var provider = new CountingProvider(failure: Offline());
        var pipeline = Create(cache, provider);

        var local = await pipeline.TranslateLocalAsync(["You have 150 gold."], "en", "pl");
        Assert.Null(Assert.Single(local));
        Assert.Equal(0, provider.Calls);

        // Gdyby sprawdzenie zostawiło wpis zapasowy, po zniknięciu wpisu z bazy i błędzie
        // dostawcy gracz zobaczyłby stary, podejrzany wynik zamiast komunikatu o błędzie.
        await cache.ClearAsync(keepManualCorrections: false);
        var full = Assert.Single(await pipeline.TranslateAsync(["You have 150 gold."], "en", "pl"));
        Assert.Null(full.TranslatedText);
        Assert.NotNull(full.ErrorMessage);
    }

    [Fact]
    public async Task Wieloliniowy_wpis_sprzed_sklejania_wierszy_nie_jest_lokalny()
    {
        var multiLine = TextNormalizer.Normalize("Talk to the\nblacksmith about the sword.");
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(multiLine, multiLine, "en", "pl", "Porozmawiaj z\nkowalem o mieczu.", "Counting"));
        var provider = new CountingProvider();
        var pipeline = Create(cache, provider);

        Assert.Null(Assert.Single(await pipeline.TranslateLocalAsync([multiLine], "en", "pl")));
        Assert.Equal(0, provider.Calls);

        // Pełna ścieżka dalej odświeża taki wpis u dostawcy.
        await pipeline.TranslateAsync([multiLine], "en", "pl");
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task W_trybie_cache_only_nieaktualny_wpis_jest_lokalny_jak_w_pelnej_sciezce()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("You have 150 gold.", "You have 150 gold.", "en", "pl", "Masz 15 złota.", "Counting",
            Context: TranslationCacheContext.Build(TranslationQualityFlags.NumbersChanged)));
        var pipeline = Create(cache, new CountingProvider(), cacheOnly: true);

        var local = Assert.Single(await pipeline.TranslateLocalAsync(["You have 150 gold."], "en", "pl"));
        var full = Assert.Single(await pipeline.TranslateAsync(["You have 150 gold."], "en", "pl"));

        Assert.Equal("Masz 15 złota.", local!.TranslatedText);
        Assert.Equal(full.TranslatedText, local.TranslatedText);
        Assert.Equal(full.QualityWarning, local.QualityWarning);
        Assert.NotNull(local.QualityWarning);
    }

    [Fact]
    public async Task Wpis_atrapy_Mock_nie_jest_lokalny_i_nie_zostawia_zapasu()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "[PL] Hello", MockTranslationProvider.ProviderName,
            GameProfile: Profile));
        var provider = new CountingProvider(_ => "Cześć");
        var pipeline = Create(cache, provider, profile: Profile);

        Assert.Null(Assert.Single(await pipeline.TranslateLocalAsync(["Hello"], "en", "pl")));
        Assert.Equal(0, provider.Calls);

        // Pełna ścieżka sama zapamiętuje profil atrapy i nadpisuje ten wpis prawdziwym
        // wynikiem — od tej chwili tekst jest już lokalny.
        await pipeline.TranslateAsync(["Hello"], "en", "pl");
        var again = Assert.Single(await pipeline.TranslateLocalAsync(["Hello"], "en", "pl"));
        Assert.Equal("Cześć", again!.TranslatedText);
    }

    [Fact]
    public async Task Wynik_dostawcy_zapisany_w_cache_jest_potem_lokalny()
    {
        var cache = new InMemoryTranslationCache();
        var provider = new CountingProvider();
        var pipeline = Create(cache, provider);

        Assert.Null(Assert.Single(await pipeline.TranslateLocalAsync(["Open the gate"], "en", "pl")));
        await pipeline.TranslateAsync(["Open the gate"], "en", "pl");
        var local = Assert.Single(await pipeline.TranslateLocalAsync(["Open the gate"], "en", "pl"));

        Assert.Equal("PL:Open the gate", local!.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, local.Origin);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Uszkodzona_baza_uzywa_awaryjnej_pamieci_bez_dostawcy()
    {
        var provider = new CountingProvider();
        var pipeline = Create(new BrokenCache(), provider);

        Assert.Null(Assert.Single(await pipeline.TranslateLocalAsync(["Hello"], "en", "pl")));
        Assert.True(pipeline.IsCacheDegraded);
        await pipeline.TranslateAsync(["Hello"], "en", "pl");
        var local = Assert.Single(await pipeline.TranslateLocalAsync(["Hello"], "en", "pl"));

        Assert.Equal("PL:Hello", local!.TranslatedText);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Pusta_lista_i_pusty_tekst()
    {
        var provider = new CountingProvider();
        var pipeline = Create(new InMemoryTranslationCache(), provider);

        Assert.Empty(await pipeline.TranslateLocalAsync([], "en", "pl"));
        var empty = Assert.Single(await pipeline.TranslateLocalAsync(["   "], "en", "pl"));

        Assert.NotNull(empty);
        Assert.Null(empty.TranslatedText);
        Assert.Equal(TranslationOrigin.Unavailable, empty.Origin);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Anulowanie_przerywa_sprawdzenie()
    {
        var pipeline = Create(new InMemoryTranslationCache(), new CountingProvider());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.TranslateLocalAsync(["Hello"], "en", "pl", cts.Token));
    }

    // Liczy odczyty bazy — trwały cache podbija przy każdym odczycie licznik użyć (use_count).
    private sealed class LookupCountingCache(ITranslationCache inner) : ITranslationCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _lookups = new(StringComparer.Ordinal);
        public int LookupsOf(string normalizedText) => _lookups.GetValueOrDefault(normalizedText);

        public Task<CachedTranslation?> LookupAsync(string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default)
        {
            _lookups.AddOrUpdate(normalizedText, 1, static (_, count) => count + 1);
            return inner.LookupAsync(normalizedText, sourceLanguage, targetLanguage, gameProfile, cancellationToken);
        }

        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            inner.StoreAsync(entry, cancellationToken);
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

    [Fact]
    public async Task Wynik_proby_nie_powtarza_odczytu_znanych_tekstow_z_bazy()
    {
        var inner = new InMemoryTranslationCache();
        await inner.StoreAsync(new NewCacheEntry("Inventory", "Inventory", "en", "pl", "Ekwipunek", "Counting",
            Context: TextReflow.FormatVersion));
        var cache = new LookupCountingCache(inner);
        var usage = new UsageTracker();
        var provider = new CountingProvider();
        var pipeline = Create(cache, provider, usage);
        string[] texts = ["Inventory", "A brand new line"];

        var local = await pipeline.TranslateLocalAsync(texts, "en", "pl");
        Assert.Equal(1, cache.LookupsOf("Inventory"));
        var full = await pipeline.TranslateAsync(texts, "en", "pl", local);

        // Znany tekst czytany raz (próba); nieznany ponownie — mógł w międzyczasie trafić do bazy.
        Assert.Equal(1, cache.LookupsOf("Inventory"));
        Assert.True(cache.LookupsOf("A brand new line") > 1);
        Assert.Equal("Ekwipunek", full[0].TranslatedText);
        Assert.Equal("PL:A brand new line", full[1].TranslatedText);
        Assert.Equal(1, provider.Calls);
        // Trafienie liczone dokładnie raz.
        Assert.Equal(1, usage.CacheHits);
    }

    [Fact]
    public async Task Wynik_proby_daje_to_samo_co_pelna_sciezka_i_liczy_slownik_raz()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Counting",
            Context: TextReflow.FormatVersion));
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));
        var usage = new UsageTracker();
        var pipeline = Create(cache, new CountingProvider(), usage, glossary);
        string[] texts = ["Hello", "Armour", "New line", " Hello "];

        var local = await pipeline.TranslateLocalAsync(texts, "en", "pl");
        var withHint = await pipeline.TranslateAsync(texts, "en", "pl", local);

        Assert.Equal(["Cześć", "Pancerz", "PL:New line", "Cześć"], withHint.Select(static o => o.TranslatedText));
        Assert.Equal(" Hello ", withHint[3].SourceText);
        Assert.Equal(1, usage.CacheHits);
        Assert.Equal(1, usage.GlossaryHits);
    }

    [Fact]
    public async Task Wynik_proby_dla_innych_tekstow_jest_ignorowany()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "Cześć", "Counting",
            Context: TextReflow.FormatVersion));
        var pipeline = Create(cache, new CountingProvider());

        var local = await pipeline.TranslateLocalAsync(["Hello"], "en", "pl");
        // Inna długość listy albo inny tekst na tej pozycji — próba nie pasuje, zwykła ścieżka.
        var shorter = await pipeline.TranslateAsync(["Goodbye", "Hello"], "en", "pl", local);
        var swapped = await pipeline.TranslateAsync(["Goodbye"], "en", "pl", local);

        Assert.Equal(["PL:Goodbye", "Cześć"], shorter.Select(static o => o.TranslatedText));
        Assert.Equal("PL:Goodbye", Assert.Single(swapped).TranslatedText);
    }

    [Fact]
    public async Task Nieaktualny_wpis_z_proby_dalej_ma_zapas_przy_bledzie_dostawcy()
    {
        var multiLine = TextNormalizer.Normalize("Talk to the\nblacksmith about the sword.");
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(multiLine, multiLine, "en", "pl", "Porozmawiaj z\nkowalem o mieczu.", "Counting"));
        var pipeline = Create(cache, new CountingProvider(failure: Offline()));

        var local = await pipeline.TranslateLocalAsync([multiLine], "en", "pl");
        Assert.Null(Assert.Single(local));
        var full = Assert.Single(await pipeline.TranslateAsync([multiLine], "en", "pl", local));

        // Pełna ścieżka sama odczytała nieaktualny wpis i pokazała go zamiast błędu.
        Assert.Equal("Porozmawiaj z\nkowalem o mieczu.", full.TranslatedText);
    }
}
