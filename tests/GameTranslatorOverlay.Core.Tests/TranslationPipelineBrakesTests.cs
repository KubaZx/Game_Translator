using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationPipelineBrakesTests
{
    private static readonly string[] Words =
    [
        "Apple", "Banana", "Cherry", "Damson", "Elder", "Fig", "Grape", "Hazel", "Iris", "Juniper",
        "Kiwi", "Lemon", "Mango", "Nectar", "Olive", "Peach", "Quince", "Rowan", "Sloe", "Thyme",
    ];

    private sealed class FakeProvider : ITranslationProvider
    {
        private int _calls;

        public Func<IReadOnlyList<string>, IReadOnlyList<string>> Respond { get; set; } =
            static texts => texts.Select(static t => "PL:" + t).ToList();

        public TranslationException? Throw { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public string Name => "Fake";
        public bool RequiresApiKey => true;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Requests.Add(texts.ToList());
            if (Throw is not null) throw Throw;
            return Task.FromResult(Respond(texts));
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static TranslationPipeline Create(FakeProvider provider, ManualClock clock, UsageTracker? usage = null) =>
        new(new GlossaryService(), new InMemoryTranslationCache(), provider, usage ?? new UsageTracker(),
            new TranslationPipelineOptions(), timeProvider: clock);

    private static async Task<TranslationOutcome> One(TranslationPipeline pipeline, string text) =>
        (await pipeline.TranslateAsync([text], "en", "pl"))[0];

    [Fact]
    public async Task Trwaly_blad_wysyla_do_dostawcy_dokladnie_jedno_zapytanie()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.InvalidApiKey, "odrzucony") };
        var pipeline = Create(provider, clock);

        var outcomes = new List<TranslationOutcome>();
        for (var i = 0; i < 5; i++) outcomes.Add(await One(pipeline, "Apple"));
        foreach (var word in Words.Skip(1).Take(10)) outcomes.Add(await One(pipeline, word));
        outcomes.AddRange(await pipeline.TranslateAsync(["Quince", "Rowan", "Sloe"], "en", "pl"));
        clock.Advance(TimeSpan.FromHours(3));
        outcomes.Add(await One(pipeline, "Thyme"));

        Assert.Equal(1, provider.Calls);
        Assert.All(outcomes, static o =>
        {
            Assert.Null(o.TranslatedText);
            Assert.Equal(OutcomeIssue.Provider, o.Issue);
            Assert.Equal(TranslationFailureKind.InvalidApiKey, o.FailureKind);
        });
        Assert.Equal(ProviderPauseKind.Permanent, pipeline.ProviderPause.Kind);
    }

    [Fact]
    public async Task Wstrzymanie_nie_dotyczy_slownika_i_cache()
    {
        var clock = new ManualClock();
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Banana", "Banana", "en", "pl", "Banan", "Fake"));
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Cherry", "Wiśnia", Priority: 10));
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.QuotaExceeded, "limit") };
        var pipeline = new TranslationPipeline(glossary, cache, provider, new UsageTracker(), new TranslationPipelineOptions(),
            timeProvider: clock);

        await One(pipeline, "Apple");
        var cached = await One(pipeline, "Banana");
        var termed = await One(pipeline, "Cherry");

        Assert.Equal("Banan", cached.TranslatedText);
        Assert.Equal("Wiśnia", termed.TranslatedText);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Blad_chwilowy_trzyma_harmonogram_5_15_60_s()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.NetworkError, "offline") };
        var pipeline = Create(provider, clock);
        var next = 0;
        async Task<int> Attempt() => (await One(pipeline, Words[next++ % Words.Length])).FailureKind is not null ? provider.Calls : -1;

        Assert.Equal(1, await Attempt());
        clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(1, await Attempt());
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, await Attempt());

        clock.Advance(TimeSpan.FromSeconds(14.9));
        Assert.Equal(2, await Attempt());
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(3, await Attempt());

        clock.Advance(TimeSpan.FromSeconds(59.9));
        Assert.Equal(3, await Attempt());
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(4, await Attempt());

        clock.Advance(TimeSpan.FromSeconds(59.9));
        Assert.Equal(4, await Attempt());
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(5, await Attempt());
    }

    [Fact]
    public async Task Tekst_w_czasie_przerwy_dostaje_ten_sam_rodzaj_bledu()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.RateLimited, "tempo") };
        var pipeline = Create(provider, clock);

        await One(pipeline, "Apple");
        var blocked = await One(pipeline, "Banana");

        Assert.Equal(1, provider.Calls);
        Assert.Equal(TranslationFailureKind.RateLimited, blocked.FailureKind);
        Assert.Equal(OutcomeIssue.Provider, blocked.Issue);
        Assert.Equal(ProviderPauseKind.Temporary, pipeline.ProviderPause.Kind);
    }

    [Fact]
    public async Task Udana_odpowiedz_zeruje_harmonogram()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.Timeout, "wolno") };
        var pipeline = Create(provider, clock);

        await One(pipeline, "Apple");
        clock.Advance(TimeSpan.FromSeconds(5));
        await One(pipeline, "Banana");
        Assert.Equal(2, provider.Calls);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(15), pipeline.ProviderPause.ResumeAt);

        clock.Advance(TimeSpan.FromSeconds(15));
        provider.Throw = null;
        var recovered = await One(pipeline, "Cherry");
        Assert.Equal("PL:Cherry", recovered.TranslatedText);
        Assert.False(pipeline.ProviderPause.IsPaused);

        provider.Throw = new TranslationException(TranslationFailureKind.Timeout, "wolno");
        await One(pipeline, "Damson");
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(5), pipeline.ProviderPause.ResumeAt);
    }

    [Fact]
    public async Task Po_przerwie_wychodzi_jedno_zapytanie_probne_z_cala_partia()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.ServiceUnavailable, "503") };
        var pipeline = Create(provider, clock);
        await One(pipeline, "Apple");
        clock.Advance(TimeSpan.FromSeconds(6));

        var probe = Task.Run(() => pipeline.TranslateAsync(["Banana", "Cherry", "Damson"], "en", "pl"));
        var results = await probe;

        Assert.Equal(2, provider.Calls);
        Assert.Equal(3, provider.Requests[1].Count);
        Assert.All(results, static o => Assert.Equal(TranslationFailureKind.ServiceUnavailable, o.FailureKind));
    }

    [Fact]
    public async Task Jawne_wznowienie_zdejmuje_wstrzymanie()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.InvalidApiKey, "x") };
        var pipeline = Create(provider, clock);
        await One(pipeline, "Apple");
        await One(pipeline, "Banana");
        Assert.Equal(1, provider.Calls);

        provider.Throw = null;
        pipeline.ResumeProvider();
        var outcome = await One(pipeline, "Banana");

        Assert.Equal("PL:Banana", outcome.TranslatedText);
        Assert.Equal(2, provider.Calls);
        Assert.False(pipeline.ProviderPause.IsPaused);
    }

    [Fact]
    public async Task Nowy_pipeline_po_przebudowie_nie_dziedziczy_wstrzymania()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Throw = new TranslationException(TranslationFailureKind.ModelNotFound, "model") };
        var first = Create(provider, clock);
        await One(first, "Apple");
        Assert.True(first.ProviderPause.IsPaused);

        var rebuilt = Create(provider, clock);
        await One(rebuilt, "Apple");

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Pusty_wynik_dwa_razy_z_rzedu_wstrzymuje_ponowne_wysylanie_tekstu_na_5_minut()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Respond = static texts => texts.Select(static _ => string.Empty).ToList() };
        var pipeline = Create(provider, clock);

        var first = await One(pipeline, "Apple");
        var second = await One(pipeline, "Apple");
        var third = await One(pipeline, "Apple");
        var other = await One(pipeline, "Banana");

        Assert.Equal(3, provider.Calls);
        Assert.Equal(OutcomeIssue.EmptyResult, first.Issue);
        Assert.Equal(OutcomeIssue.EmptyResult, second.Issue);
        Assert.Equal(OutcomeIssue.EmptyResult, third.Issue);
        Assert.Equal(OutcomeIssue.EmptyResult, other.Issue);

        clock.Advance(FailedTextMemory.Quarantine - TimeSpan.FromSeconds(1));
        await One(pipeline, "Apple");
        Assert.Equal(3, provider.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await One(pipeline, "Apple");
        Assert.Equal(4, provider.Calls);
    }

    [Fact]
    public async Task Odmowa_dwa_razy_z_rzedu_blokuje_tylko_ten_tekst_i_nie_wstrzymuje_dostawcy()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider();
        provider.Respond = texts => texts.Contains("Apple")
            ? throw new TranslationException(TranslationFailureKind.ContentRefused, "odmowa")
            : texts.Select(static t => "PL:" + t).ToList();
        var pipeline = Create(provider, clock);

        await One(pipeline, "Apple");
        await One(pipeline, "Apple");
        var blocked = await One(pipeline, "Apple");
        var fine = await One(pipeline, "Banana");

        Assert.Equal(3, provider.Calls);
        Assert.Equal(TranslationFailureKind.ContentRefused, blocked.FailureKind);
        Assert.Equal("PL:Banana", fine.TranslatedText);
        Assert.False(pipeline.ProviderPause.IsPaused);
    }

    [Fact]
    public async Task Udane_tlumaczenie_czysci_pamiec_porazek_tekstu()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Respond = static texts => texts.Select(static _ => string.Empty).ToList() };
        var pipeline = Create(provider, clock);
        await One(pipeline, "Apple");

        provider.Respond = static texts => texts.Select(static t => "PL:" + t).ToList();
        var good = await One(pipeline, "Apple");
        Assert.Equal("PL:Apple", good.TranslatedText);

        provider.Respond = static texts => texts.Select(static _ => string.Empty).ToList();
        provider.Throw = null;
        await One(pipeline, "Apple");
        Assert.Equal("PL:Apple", (await One(pipeline, "Apple")).TranslatedText);
    }

    [Theory]
    [InlineData("+25%")]
    [InlineData("12:30")]
    [InlineData("100")]
    [InlineData("3/10")]
    public async Task Tekst_bez_litery_nigdy_nie_idzie_do_dostawcy(string text)
    {
        var provider = new FakeProvider();
        var pipeline = Create(provider, new ManualClock());

        var outcome = await One(pipeline, text);

        Assert.Equal(0, provider.Calls);
        Assert.Null(outcome.TranslatedText);
        Assert.Equal(OutcomeIssue.Skipped, outcome.Issue);
        Assert.Null(outcome.FailureKind);
    }

    [Fact]
    public async Task Zegar_60_wariantow_to_najwyzej_3_wywolania_a_wynik_ma_aktualne_cyfry()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider
        {
            Respond = static texts => texts.Select(static t => "Czas " + t["Time ".Length..]).ToList(),
        };
        var pipeline = Create(provider, clock);

        var outcomes = new List<TranslationOutcome>();
        for (var second = 0; second < 60; second++)
        {
            outcomes.Add(await One(pipeline, $"Time 01:{second:00}"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.InRange(provider.Calls, 1, 3);
        for (var second = 0; second < 60; second++)
            Assert.Equal($"Czas 01:{second:00}", outcomes[second].TranslatedText);
        Assert.Contains(outcomes, static o => o.Notices.HasFlag(OutcomeNotice.CounterSkipped));
    }

    [Fact]
    public async Task Liczba_przy_slowach_po_wyciszeniu_dostaje_tlumaczenie_z_nowymi_cyframi()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider
        {
            Respond = static texts => texts.Select(static t => "Znaleziono " + t.Split(' ')[2] + " kluczy").ToList(),
        };
        var pipeline = Create(provider, clock);

        var outcomes = new List<TranslationOutcome>();
        var counts = Enumerable.Range(1, VolatileCounterGate.DistinctWordedTextsToMute + 2).Select(static i => i * 2 + 1).ToArray();
        foreach (var count in counts)
        {
            outcomes.Add(await One(pipeline, $"You found {count} keys"));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        var sent = VolatileCounterGate.DistinctWordedTextsToMute - 1;
        Assert.Equal(sent, provider.Calls);
        for (var i = 0; i < counts.Length; i++)
            Assert.Equal($"Znaleziono {counts[i]} kluczy", outcomes[i].TranslatedText);
        Assert.All(outcomes.Skip(sent), static o => Assert.True(o.Notices.HasFlag(OutcomeNotice.CounterSkipped)));
    }

    [Fact]
    public async Task Dwa_dialogi_z_liczba_w_odstepie_minuty_nie_wyciszaja_niczego()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider();
        var pipeline = Create(provider, clock);

        var outcomes = new List<TranslationOutcome>();
        foreach (var chapter in new[] { 2, 3, 4, 5 })
        {
            outcomes.Add(await One(pipeline, $"Chapter {chapter} begins"));
            clock.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Equal(4, provider.Calls);
        Assert.All(outcomes, static o => Assert.NotNull(o.TranslatedText));
    }

    [Fact]
    public async Task Wyciszenie_licznika_wygasa()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider
        {
            Respond = static texts => texts.Select(static t => "You found: " + t).ToList(),
        };
        var pipeline = Create(provider, clock);
        var sent = VolatileCounterGate.DistinctWordedTextsToMute - 1;
        for (var count = 1; count <= sent + 1; count++)
        {
            await One(pipeline, $"You found {count} keys");
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(sent, provider.Calls);

        clock.Advance(VolatileCounterGate.Mute);
        var after = await One(pipeline, "You found 110 keys");

        Assert.Equal(sent + 1, provider.Calls);
        Assert.NotNull(after.TranslatedText);
    }

    [Fact]
    public async Task Wyciszony_licznik_w_partii_z_innymi_tekstami_nie_blokuje_reszty()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider();
        var pipeline = Create(provider, clock);
        for (var count = 1; count < VolatileCounterGate.DistinctWordedTextsToMute; count++)
        {
            await One(pipeline, $"You found {count} keys");
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        var outcomes = await pipeline.TranslateAsync(["You found 90 keys", "Press any key"], "en", "pl");

        Assert.Equal("PL:You found 90 keys", outcomes[0].TranslatedText);
        Assert.Equal("PL:Press any key", outcomes[1].TranslatedText);
        Assert.Equal(["Press any key"], provider.Requests[^1]);
    }

    [Fact]
    public async Task Ostrzezenie_o_limicie_przy_80_procentach_dostaje_tylko_pierwszy_wynik()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        var provider = new FakeProvider();
        var pipeline = Create(provider, new ManualClock(), usage);

        var low = await One(pipeline, "Apple");
        var near = await One(pipeline, new string('b', 80) + " Banana");
        var after = await One(pipeline, "Cherry");

        Assert.Equal(OutcomeNotice.None, low.Notices);
        Assert.Equal(OutcomeNotice.SessionLimitNear, near.Notices);
        Assert.Equal(OutcomeNotice.None, after.Notices);
        Assert.Equal(SessionLimitLevel.Near, usage.LimitStatus.Level);
    }

    [Fact]
    public async Task Komunikat_o_wyczerpanym_limicie_wskazuje_pole_w_sekcji_Tlumaczenie()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 3 };
        var pipeline = Create(new FakeProvider(), new ManualClock(), usage);

        var outcome = await One(pipeline, "Apple");

        Assert.Equal(OutcomeIssue.SessionLimit, outcome.Issue);
        Assert.Contains("Limit znaków na sesję", outcome.ErrorMessage);
        Assert.Contains("Tłumaczenie", outcome.ErrorMessage);
    }
}
