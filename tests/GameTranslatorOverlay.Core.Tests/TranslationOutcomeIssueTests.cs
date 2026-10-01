using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Rodzaj błędu i powód braku tłumaczenia w wynikach pipeline'u — z nich nakładka buduje
/// komunikaty dla gracza, więc muszą być ustawione na każdej ścieżce porażki.
/// </summary>
public class TranslationOutcomeIssueTests
{
    private sealed class ScriptedProvider : ITranslationProvider
    {
        public TranslationException? Throw { get; set; }
        public Func<IReadOnlyList<string>, IReadOnlyList<string>>? Respond { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public int Calls;

        public string Name => "DeepL";
        public bool RequiresApiKey => true;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (Throw is not null) throw Throw;
            return Respond?.Invoke(texts) ?? texts.Select(static t => "PL:" + t).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static TranslationPipeline Create(
        ScriptedProvider provider, UsageTracker? usage = null, TranslationPipelineOptions? options = null,
        ITranslationCache? cache = null) =>
        new(new GlossaryService(), cache ?? new InMemoryTranslationCache(), provider, usage ?? new UsageTracker(),
            options ?? new TranslationPipelineOptions());

    [Theory]
    [InlineData(TranslationFailureKind.MissingApiKey)]
    [InlineData(TranslationFailureKind.QuotaExceeded)]
    [InlineData(TranslationFailureKind.NetworkError)]
    [InlineData(TranslationFailureKind.RateLimited)]
    public async Task Blad_dostawcy_ustawia_rodzaj_bledu_i_powod_Provider(TranslationFailureKind kind)
    {
        var provider = new ScriptedProvider { Throw = new TranslationException(kind, "kontrolowany błąd") };

        var outcome = Assert.Single(await Create(provider).TranslateAsync(["Hello there"], "en", "pl"));

        Assert.False(outcome.IsTranslated);
        Assert.Equal(kind, outcome.FailureKind);
        Assert.Equal(OutcomeIssue.Provider, outcome.Issue);
    }

    [Fact]
    public async Task Czekajacy_na_to_samo_zapytanie_dostaje_ten_sam_rodzaj_bledu()
    {
        var provider = new ScriptedProvider
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Throw = new TranslationException(TranslationFailureKind.QuotaExceeded, "limit konta"),
        };
        var pipeline = Create(provider);

        var owner = pipeline.TranslateAsync(["Hello there"], "en", "pl");
        while (Volatile.Read(ref provider.Calls) == 0) await Task.Delay(5);
        var waiter = pipeline.TranslateAsync(["Hello there"], "en", "pl");
        Assert.False(waiter.IsCompleted);
        provider.Gate.SetResult();

        var ownerOutcome = Assert.Single(await owner);
        var waiterOutcome = Assert.Single(await waiter);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(TranslationFailureKind.QuotaExceeded, ownerOutcome.FailureKind);
        Assert.Equal(TranslationFailureKind.QuotaExceeded, waiterOutcome.FailureKind);
        Assert.Equal(OutcomeIssue.Provider, waiterOutcome.Issue);
    }

    [Fact]
    public async Task Tryb_Cache_only_oznacza_pudlo_bez_rodzaju_bledu()
    {
        var provider = new ScriptedProvider();
        var pipeline = Create(provider, options: new TranslationPipelineOptions { CacheOnlyMode = true });

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Unknown line"], "en", "pl"));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(OutcomeIssue.CacheOnlyMiss, outcome.Issue);
        Assert.Null(outcome.FailureKind);
    }

    [Fact]
    public async Task Limit_sesji_oznacza_powod_SessionLimit_takze_u_czekajacego()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 5 };
        var provider = new ScriptedProvider();

        var outcomes = await Create(provider, usage).TranslateAsync(["A much longer line"], "en", "pl");

        var outcome = Assert.Single(outcomes);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(OutcomeIssue.SessionLimit, outcome.Issue);
        // Lokalna odmowa nie udaje błędu dostawcy (np. wyczerpanego limitu konta DeepL).
        Assert.Null(outcome.FailureKind);
    }

    [Fact]
    public async Task Czekajacy_na_tekst_odrzucony_limitem_sesji_dostaje_SessionLimit()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        var provider = new ScriptedProvider { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pipeline = Create(provider, usage, new TranslationPipelineOptions { MaxBatchSize = 1 });
        var first = new string('a', 80);
        var later = new string('b', 80);

        var owner = pipeline.TranslateAsync([first, later], "en", "pl");
        while (Volatile.Read(ref provider.Calls) == 0) await Task.Delay(5);
        var waiter = pipeline.TranslateAsync([later], "en", "pl");
        provider.Gate.SetResult();

        var ownerResults = await owner;
        var waiterOutcome = Assert.Single(await waiter);
        Assert.Equal(OutcomeIssue.SessionLimit, ownerResults[1].Issue);
        Assert.Equal(OutcomeIssue.SessionLimit, waiterOutcome.Issue);
        Assert.Null(waiterOutcome.FailureKind);
    }

    [Fact]
    public async Task Pusty_wynik_dostawcy_oznacza_powod_EmptyResult()
    {
        var provider = new ScriptedProvider { Respond = static texts => texts.Select(static _ => "   ").ToList() };

        var outcome = Assert.Single(await Create(provider).TranslateAsync(["Hello there"], "en", "pl"));

        Assert.False(outcome.IsTranslated);
        Assert.Equal(OutcomeIssue.EmptyResult, outcome.Issue);
        Assert.Null(outcome.FailureKind);
    }

    [Fact]
    public async Task Czekajacy_na_pusty_wynik_dostaje_EmptyResult()
    {
        var provider = new ScriptedProvider
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Respond = static texts => texts.Select(static _ => string.Empty).ToList(),
        };
        var pipeline = Create(provider);

        var owner = pipeline.TranslateAsync(["Hello there"], "en", "pl");
        while (Volatile.Read(ref provider.Calls) == 0) await Task.Delay(5);
        var waiter = pipeline.TranslateAsync(["Hello there"], "en", "pl");
        provider.Gate.SetResult();

        Assert.Equal(OutcomeIssue.EmptyResult, Assert.Single(await owner).Issue);
        Assert.Equal(OutcomeIssue.EmptyResult, Assert.Single(await waiter).Issue);
    }

    [Fact]
    public async Task Niezgodna_liczba_wynikow_to_blad_dostawcy_Unknown()
    {
        var provider = new ScriptedProvider { Respond = static _ => [] };

        var outcome = Assert.Single(await Create(provider).TranslateAsync(["Hello there"], "en", "pl"));

        Assert.Equal(OutcomeIssue.Provider, outcome.Issue);
        Assert.Equal(TranslationFailureKind.Unknown, outcome.FailureKind);
    }

    [Fact]
    public async Task Udane_tlumaczenie_nie_ma_powodu_ani_rodzaju_bledu()
    {
        var outcome = Assert.Single(await Create(new ScriptedProvider()).TranslateAsync(["Hello there"], "en", "pl"));

        Assert.True(outcome.IsTranslated);
        Assert.Equal(OutcomeIssue.None, outcome.Issue);
        Assert.Null(outcome.FailureKind);
    }

    [Fact]
    public async Task Zapasowy_stary_wynik_po_bledzie_dostawcy_nie_niesie_bledu()
    {
        var cache = new InMemoryTranslationCache();
        // Wpis wieloliniowy w starym formacie (bez znacznika reflow) jest odświeżany;
        // gdy dostawca zawiedzie, gracz dostaje stary wynik — bez komunikatu o błędzie.
        await cache.StoreAsync(new NewCacheEntry("Hello\nthere", "Hello\nthere", "en", "pl", "Cześć\ntam", "DeepL"));
        var provider = new ScriptedProvider { Throw = new TranslationException(TranslationFailureKind.NetworkError, "sieć") };

        var outcome = Assert.Single(await Create(provider, cache: cache).TranslateAsync(["Hello\nthere"], "en", "pl"));

        Assert.Equal(1, provider.Calls);
        Assert.True(outcome.IsTranslated);
        Assert.Equal(OutcomeIssue.None, outcome.Issue);
        Assert.Null(outcome.FailureKind);
    }
}
