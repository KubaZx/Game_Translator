using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Zastępowanie wpisów cache (nieaktualnych, oznaczonych przez kontrolę jakości, atrap Mock)
/// musi się kończyć — każde niepotrzebne powtórzenie to płatne zapytanie do dostawcy.
/// </summary>
public class TranslationPipelineStaleReplacementTests
{
    private const string Profile = "latarnia";
    private const string Gold = "You have 150 gold.";
    private static readonly string MultiLine = TextNormalizer.Normalize("Talk to the\nblacksmith about the sword.");

    private class ScriptedProvider(Func<string, int, string> respond) : ITranslationProvider
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public Func<Task>? BeforeRespond { get; set; }

        public string Name => "Scripted";
        public bool RequiresApiKey => false;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (BeforeRespond is not null) await BeforeRespond().ConfigureAwait(false);
            return texts.Select(t => respond(t, call)).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class RetryableScriptedProvider(Func<string, int, string> respond)
        : ScriptedProvider(respond), IRetryableTranslationProvider;

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

    private static TranslationPipeline Create(ITranslationCache cache, ITranslationProvider provider, string profile = "",
        bool cacheOnly = false) =>
        new(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { GameProfile = profile, CacheOnlyMode = cacheOnly });

    [Fact]
    public async Task Wpis_atrapy_Mock_w_profilu_jest_zastepowany_tylko_raz()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "[PL] Hello", MockTranslationProvider.ProviderName,
            GameProfile: Profile));
        var provider = new ScriptedProvider((_, _) => "Cześć");
        var pipeline = Create(cache, provider, Profile);

        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 3; i++) outcomes.Add(Assert.Single(await pipeline.TranslateAsync(["Hello"], "en", "pl")));

        Assert.Equal(1, provider.CallCount);
        Assert.All(outcomes, o => Assert.Equal("Cześć", o.TranslatedText));
        var stored = await cache.LookupAsync("Hello", "en", "pl", Profile);
        Assert.Equal("Scripted", stored!.Provider);
        Assert.Equal(Profile, stored.GameProfile);
    }

    [Fact]
    public async Task Wynik_atrapy_nie_jest_pokazywany_jako_zapasowy_gdy_dostawca_zawiedzie()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Hello", "Hello", "en", "pl", "[PL] Hello", MockTranslationProvider.ProviderName,
            GameProfile: Profile));
        var provider = new ScriptedProvider((_, _) => throw new TranslationException(TranslationFailureKind.NetworkError, "offline"));

        var outcome = Assert.Single(await Create(cache, provider, Profile).TranslateAsync(["Hello"], "en", "pl"));

        Assert.Null(outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
    }

    [Fact]
    public async Task Trwaly_problem_jakosci_kosztuje_u_LLM_najwyzej_trzy_zapytania()
    {
        var provider = new RetryableScriptedProvider((_, _) => "Masz 15 złota.");
        var cache = new InMemoryTranslationCache();
        var pipeline = Create(cache, provider);

        for (var i = 0; i < 5; i++) await pipeline.TranslateAsync([Gold], "en", "pl");

        // Pierwsze wystąpienie: zapytanie + ponowienie; drugie (wpis oznaczony): jedno
        // zapytanie bez ponowienia w partii; potem już tylko cache.
        Assert.Equal(3, provider.CallCount);
        Assert.Equal("reflow-1;qa=numbers;qa-final", (await cache.LookupAsync(Gold, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Pusty_wynik_przy_ponownym_tlumaczeniu_wpisu_z_problemem_konczy_ponawianie()
    {
        var provider = new ScriptedProvider((_, call) => call == 1 ? "Masz 15 złota." : "");
        var cache = new InMemoryTranslationCache();
        var pipeline = Create(cache, provider);

        await pipeline.TranslateAsync([Gold], "en", "pl");
        var second = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));
        var third = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 15 złota.", second.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, second.Origin);
        Assert.Equal("Masz 15 złota.", third.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, third.Origin);
        Assert.NotNull(third.QualityWarning);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal("reflow-1;qa=numbers;qa-final", (await cache.LookupAsync(Gold, "en", "pl", ""))!.Context);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Profile)]
    public async Task Pusty_wynik_dla_wpisu_w_starym_formacie_kosztuje_najwyzej_jedno_ponowienie(string profile)
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "Porozmawiaj z\nkowalem.", "DeepL",
            GameProfile: profile));
        var provider = new RetryableScriptedProvider((_, _) => "");

        var outcomes = new List<TranslationOutcome>();
        var pipeline = Create(cache, provider, profile);
        for (var i = 0; i < 4; i++) outcomes.Add(Assert.Single(await pipeline.TranslateAsync([MultiLine], "en", "pl")));
        outcomes.Add(Assert.Single(await Create(cache, provider, profile).TranslateAsync([MultiLine], "en", "pl")));

        Assert.Equal(1, provider.CallCount);
        Assert.All(outcomes, static o => Assert.Equal("Porozmawiaj z\nkowalem.", o.TranslatedText));
        Assert.All(outcomes, static o => Assert.Equal(TranslationOrigin.Cache, o.Origin));
        var stored = await cache.LookupAsync(MultiLine, "en", "pl", profile);
        Assert.Equal("reflow-final", stored!.Context);
        Assert.Equal("DeepL", stored.Provider);
        Assert.Equal(profile, stored.GameProfile);
    }

    [Fact]
    public async Task Udane_ponowienie_wpisu_w_starym_formacie_zapisuje_nowy_format()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "Porozmawiaj z\nkowalem.", "DeepL"));
        var provider = new ScriptedProvider((_, _) => "Porozmawiaj z kowalem o mieczu.");
        var pipeline = Create(cache, provider);

        for (var i = 0; i < 3; i++) await pipeline.TranslateAsync([MultiLine], "en", "pl");

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(TextReflow.FormatVersion, (await cache.LookupAsync(MultiLine, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Pusty_wynik_dla_starego_formatu_z_inna_plcia_ponawia_raz_na_pipeline()
    {
        var ready = TextNormalizer.Normalize("Are you ready to\nleave the harbor?");
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(ready, ready, "en", "pl", "Czy jesteś gotowy,\nby opuścić port?", "LLM"));
        var provider = new GenderAwareProvider((_, _) => "");
        TranslationPipeline Female() => new(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { PlayerGender = PlayerGender.Female });

        var first = Female();
        for (var i = 0; i < 3; i++) await first.TranslateAsync([ready], "en", "pl");
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("reflow-final", (await cache.LookupAsync(ready, "en", "pl", ""))!.Context);

        var second = Female();
        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 3; i++) outcomes.Add(Assert.Single(await second.TranslateAsync([ready], "en", "pl")));
        Assert.Equal(2, provider.CallCount);
        Assert.All(outcomes, static o => Assert.Equal("Czy jesteś gotowy,\nby opuścić port?", o.TranslatedText));
    }

    [Fact]
    public async Task Pusty_wynik_jednostek_bloku_w_starym_formacie_konczy_ponawianie_calego_bloku()
    {
        var block = TextNormalizer.Normalize("Options\nQuit");
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(block, block, "en", "pl", "Opcje\nWyjdź", "DeepL"));
        var provider = new ScriptedProvider((_, _) => "");
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { SplitParagraphs = true });

        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 4; i++) outcomes.Add(Assert.Single(await pipeline.TranslateAsync([block], "en", "pl")));

        Assert.Equal(1, provider.CallCount);
        Assert.All(outcomes, static o => Assert.Equal("Opcje\nWyjdź", o.TranslatedText));
        Assert.Equal("reflow-final", (await cache.LookupAsync(block, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Pusty_wynik_jednostek_bloku_z_inna_plcia_ponawia_raz_na_pipeline()
    {
        var block = TextNormalizer.Normalize("Options\nAre you ready?");
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(block, block, "en", "pl", "Opcje\nCzy jesteś gotowy?", "LLM",
            Context: "reflow-1;pg=m"));
        var provider = new GenderAwareProvider((_, _) => "");
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { SplitParagraphs = true, PlayerGender = PlayerGender.Female });

        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 4; i++) outcomes.Add(Assert.Single(await pipeline.TranslateAsync([block], "en", "pl")));

        Assert.Equal(1, provider.CallCount);
        Assert.All(outcomes, static o => Assert.Equal("Opcje\nCzy jesteś gotowy?", o.TranslatedText));
    }

    [Fact]
    public async Task Pusty_wynik_jednostek_bloku_z_problemem_jakosci_konczy_ponawianie_calego_bloku()
    {
        var block = TextNormalizer.Normalize("Level 20\nQuit");
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(block, block, "en", "pl", "Poziom 2\nWyjdź", "Scripted",
            Context: "reflow-1;qa=numbers"));
        var provider = new ScriptedProvider((_, _) => "");
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { SplitParagraphs = true });

        for (var i = 0; i < 3; i++) await pipeline.TranslateAsync([block], "en", "pl");

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("reflow-1;qa=numbers;qa-final", (await cache.LookupAsync(block, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Blad_dostawcy_dla_wpisu_w_starym_formacie_nie_konczy_ponawiania()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "Porozmawiaj z\nkowalem.", "DeepL"));
        var provider = new ScriptedProvider((_, _) => throw new TranslationException(TranslationFailureKind.NetworkError, "offline"));
        var pipeline = Create(cache, provider);

        for (var i = 0; i < 2; i++) await pipeline.TranslateAsync([MultiLine], "en", "pl");

        Assert.Equal(2, provider.CallCount);
        Assert.Null((await cache.LookupAsync(MultiLine, "en", "pl", ""))!.Context);
    }

    [Theory]
    [InlineData(null, "reflow-final")]
    [InlineData("reflow-final", "reflow-final")]
    [InlineData("pg=m", "pg=m;reflow-final")]
    public void Znacznik_ostatecznosci_formatu_zachowuje_reszte_znacznika(string? context, string expected)
    {
        var marked = TranslationCacheContext.MarkReflowFinal(context);

        Assert.Equal(expected, marked);
        Assert.True(TranslationCacheContext.Parse(marked).ReflowFinal);
        Assert.Null(TranslationCacheContext.Parse(marked).Format);
        Assert.False(TranslationCacheContext.IsStale(marked, MultiLine));
        Assert.False(TranslationCacheContext.IsStaleForFormat(marked, MultiLine));
    }

    [Fact]
    public void Ostatecznosc_formatu_nie_wylacza_ponowienia_jakosci_ani_plci()
    {
        const string ready = "Are you ready to\nleave the harbor?";

        Assert.True(TranslationCacheContext.IsStaleForFormat(null, MultiLine));
        Assert.True(TranslationCacheContext.IsStaleForFormat("pg=m", MultiLine));
        Assert.True(TranslationCacheContext.IsStale("reflow-final;qa=numbers", MultiLine));
        Assert.True(TranslationCacheContext.IsStale("reflow-final", ready, PlayerGender.Female));
        Assert.False(TranslationCacheContext.IsStale("reflow-final;pg=f", ready, PlayerGender.Female));
    }

    private sealed class GenderAwareProvider(Func<string, int, string> respond)
        : ScriptedProvider(respond), IGenderAwareTranslationProvider;

    [Fact]
    public async Task Rownolegle_tlumaczenie_nieaktualnego_wpisu_profilu_nadpisuje_ten_wpis()
    {
        // Czekający na ten sam tekst kończą się zaraz po zwolnieniu przez właściciela
        // i sprzątają wspólny słownik nieaktualnych wpisów — zapis właściciela i tak
        // musi trafić do profilu. Kilka powtórzeń, bo to wyścig wątków.
        for (var round = 0; round < 25; round++)
        {
            var cache = new InMemoryTranslationCache();
            await cache.StoreAsync(new NewCacheEntry(MultiLine, MultiLine, "en", "pl", "Porozmawiaj z\nkowalem.", "DeepL",
                GameProfile: Profile));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new ScriptedProvider((_, _) => "Porozmawiaj z kowalem o mieczu.") { BeforeRespond = () => gate.Task };
            var pipeline = Create(cache, provider, Profile);

            var calls = Enumerable.Range(0, 6)
                .Select(_ => Task.Run(() => pipeline.TranslateAsync([MultiLine], "en", "pl")))
                .ToList();
            while (provider.CallCount == 0) await Task.Delay(1);
            await Task.Delay(5);
            gate.SetResult();
            await Task.WhenAll(calls);

            var stored = await cache.LookupAsync(MultiLine, "en", "pl", Profile);
            Assert.Equal(Profile, stored!.GameProfile);
            Assert.Equal(TextReflow.FormatVersion, stored.Context);
            // Wątek uruchomiony przez Task.Run może dotrzeć dopiero po zakończeniu właściciela
            // (wolny runner CI) i wtedy słusznie wysyła własne zapytanie — liczba wywołań zależy
            // od harmonogramu wątków. Sama deduplikacja ma deterministyczny test
            // (TranslationOutcomeIssueTests.Czekajacy_na_to_samo_zapytanie_dostaje_ten_sam_rodzaj_bledu).
            Assert.InRange(provider.CallCount, 1, calls.Count);
        }
    }

    [Fact]
    public async Task Przy_zepsutym_cache_ten_sam_tekst_nie_jest_wysylany_ponownie()
    {
        var provider = new ScriptedProvider((t, _) => "PL:" + t);
        var pipeline = Create(new BrokenCache(), provider);

        var first = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));
        var second = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));

        Assert.True(pipeline.IsCacheDegraded);
        Assert.Equal(TranslationOrigin.Provider, first.Origin);
        Assert.Equal("PL:Hello there", second.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, second.Origin);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Sprawny_cache_nie_jest_oznaczony_jako_zepsuty()
    {
        var pipeline = Create(new InMemoryTranslationCache(), new ScriptedProvider((t, _) => "PL:" + t));

        await pipeline.TranslateAsync(["Hello there"], "en", "pl");

        Assert.False(pipeline.IsCacheDegraded);
    }
}
