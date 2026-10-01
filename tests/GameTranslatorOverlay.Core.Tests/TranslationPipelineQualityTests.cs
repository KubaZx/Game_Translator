using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationPipelineQualityTests
{
    /// <summary>Dostawca z odpowiedziami ustalanymi w teście: (tekst, numer wywołania) → wynik.</summary>
    private class ScriptedProvider(Func<string, int, string> respond) : ITranslationProvider
    {
        public int CallCount { get; private set; }
        public List<IReadOnlyList<string>> Requests { get; } = [];
        public TranslationException? ThrowOnCall { get; set; }
        public int ThrowFromCall { get; set; } = int.MaxValue;

        public string Name => "Scripted";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(texts.ToList());
            if (ThrowOnCall is not null && CallCount >= ThrowFromCall) throw ThrowOnCall;
            var call = CallCount;
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => respond(t, call)).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class RetryableScriptedProvider(Func<string, int, string> respond)
        : ScriptedProvider(respond), IRetryableTranslationProvider;

    private static (TranslationPipeline Pipeline, InMemoryTranslationCache Cache, UsageTracker Usage) Create(
        ITranslationProvider provider, TranslationPipelineOptions? options = null, UsageTracker? usage = null)
    {
        var cache = new InMemoryTranslationCache();
        usage ??= new UsageTracker();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, usage, options ?? new TranslationPipelineOptions());
        return (pipeline, cache, usage);
    }

    private const string Gold = "You have 150 gold.";

    [Fact]
    public async Task Pusty_wynik_nie_trafia_do_cache_i_liczy_sie_jako_blad()
    {
        var provider = new ScriptedProvider((_, _) => "   ");
        var (pipeline, cache, usage) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));

        Assert.Null(outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
        Assert.Equal("Dostawca zwrócił pusty wynik.", outcome.ErrorMessage);
        Assert.Null(await cache.LookupAsync("Hello there", "en", "pl", ""));
        Assert.Equal(1, usage.FailedRequests);
        Assert.Equal(1, usage.QualityIssues.Empty);
        // Pusty wynik nie jest ponawiany ani u zwykłego dostawcy, ani zapisany.
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Pusty_wynik_nie_jest_ponawiany_nawet_u_dostawcy_z_ponowieniem()
    {
        var provider = new RetryableScriptedProvider((_, _) => "");
        var (pipeline, _, usage) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Hello there"], "en", "pl"));

        Assert.Null(outcome.TranslatedText);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, usage.QualityIssues.Retries);
    }

    [Fact]
    public async Task Zmienione_liczby_sa_ponawiane_raz_u_dostawcy_z_ponowieniem()
    {
        var provider = new RetryableScriptedProvider((_, call) => call == 1 ? "Masz 15 złota." : "Masz 150 złota.");
        var (pipeline, cache, usage) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold, "Hello"], "en", "pl"), o => o.SourceText == Gold);

        Assert.Equal("Masz 150 złota.", outcome.TranslatedText);
        Assert.Null(outcome.QualityWarning);
        Assert.Equal(2, provider.CallCount);
        // Ponawiany jest tylko tekst z problemem, a ponowienie liczy się jak każde zapytanie.
        Assert.Equal([Gold], provider.Requests[1]);
        Assert.Equal(2, usage.ApiRequests);
        Assert.Equal(Gold.Length + "Hello".Length + Gold.Length, usage.ApiCharacters);
        Assert.Equal(0, usage.ReservedApiCharacters);
        Assert.Equal(1, usage.QualityIssues.Retries);
        Assert.Equal(0, usage.QualityIssues.Total);

        var cached = await cache.LookupAsync(Gold, "en", "pl", "");
        Assert.Equal("Masz 150 złota.", cached!.TranslatedText);
        Assert.Equal(TextReflow.FormatVersion, cached.Context);
    }

    [Fact]
    public async Task Zwykly_dostawca_nie_jest_ponawiany_a_wynik_dostaje_znacznik_problemu()
    {
        var provider = new ScriptedProvider((_, _) => "Masz 15 złota.");
        var (pipeline, cache, usage) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 15 złota.", outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
        Assert.NotNull(outcome.QualityWarning);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, usage.QualityIssues.NumbersChanged);
        Assert.Equal(0, usage.QualityIssues.Retries);
        Assert.Equal("reflow-1;qa=numbers", (await cache.LookupAsync(Gold, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Ponowienie_zostawia_wariant_z_mniejsza_liczba_problemow()
    {
        var provider = new RetryableScriptedProvider((_, call) => call == 1
            ? "Masz 15 złota."
            : "Masz złoto. Uwaga: oryginał podaje liczbę, której nie przetłumaczono, bo była nieistotna.");
        var (pipeline, _, usage) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 15 złota.", outcome.TranslatedText);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, usage.QualityIssues.NumbersChanged);
        Assert.Equal(0, usage.QualityIssues.Runaway);
    }

    [Fact]
    public async Task Ponowienie_rezerwuje_znaki_i_nie_przekracza_limitu_sesji()
    {
        var provider = new RetryableScriptedProvider((_, _) => "Masz 15 złota.");
        var usage = new UsageTracker { SessionCharacterLimit = Gold.Length };
        var (pipeline, _, _) = Create(provider, usage: usage);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        // Limit wystarczył tylko na pierwsze zapytanie — wynik jest pokazany z ostrzeżeniem.
        Assert.Equal("Masz 15 złota.", outcome.TranslatedText);
        Assert.NotNull(outcome.QualityWarning);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(Gold.Length, usage.ApiCharacters);
        Assert.Equal(0, usage.QualityIssues.Retries);
    }

    [Fact]
    public async Task Blad_ponowienia_zostawia_pierwszy_wynik()
    {
        var provider = new RetryableScriptedProvider((_, _) => "Masz 15 złota.")
        {
            ThrowOnCall = new TranslationException(TranslationFailureKind.RateLimited, "429"),
            ThrowFromCall = 2,
        };
        var (pipeline, _, usage) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 15 złota.", outcome.TranslatedText);
        Assert.Equal(0, usage.FailedRequests);
        Assert.Equal(1, usage.ApiRequests);
        Assert.Equal(0, usage.ReservedApiCharacters);
    }

    [Fact]
    public async Task Wpis_z_problemem_jest_tlumaczony_ponownie_tylko_raz()
    {
        var provider = new ScriptedProvider((_, _) => "Masz 15 złota.");
        var (pipeline, cache, _) = Create(provider);

        await pipeline.TranslateAsync([Gold], "en", "pl");
        var second = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));
        var third = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));
        var fourth = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal(TranslationOrigin.Provider, second.Origin);
        Assert.Equal(TranslationOrigin.Cache, third.Origin);
        Assert.Equal(TranslationOrigin.Cache, fourth.Origin);
        Assert.NotNull(third.QualityWarning);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal("reflow-1;qa=numbers;qa-final", (await cache.LookupAsync(Gold, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Naprawiony_przy_ponownym_tlumaczeniu_wpis_dostaje_czysty_znacznik()
    {
        var provider = new ScriptedProvider((_, call) => call == 1 ? "Masz 15 złota." : "Masz 150 złota.");
        var (pipeline, cache, _) = Create(provider);

        await pipeline.TranslateAsync([Gold], "en", "pl");
        var second = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));
        var third = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 150 złota.", second.TranslatedText);
        Assert.Null(second.QualityWarning);
        Assert.Equal(TranslationOrigin.Cache, third.Origin);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(TextReflow.FormatVersion, (await cache.LookupAsync(Gold, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Gdy_dostawca_zawiedzie_wpis_z_problemem_jest_pokazany_jako_zapasowy()
    {
        var provider = new ScriptedProvider((_, _) => "Masz 15 złota.")
        {
            ThrowOnCall = new TranslationException(TranslationFailureKind.NetworkError, "offline"),
            ThrowFromCall = 2,
        };
        var (pipeline, _, _) = Create(provider);

        await pipeline.TranslateAsync([Gold], "en", "pl");
        var second = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 15 złota.", second.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, second.Origin);
        Assert.Null(second.ErrorMessage);
        Assert.NotNull(second.QualityWarning);
    }

    [Fact]
    public async Task W_trybie_cache_only_wpis_z_problemem_jest_uzywany_bez_zapytania()
    {
        var provider = new ScriptedProvider((_, _) => "nie powinno paść");
        var options = new TranslationPipelineOptions { CacheOnlyMode = true };
        var (pipeline, cache, _) = Create(provider, options);
        await cache.StoreAsync(new NewCacheEntry(Gold, Gold, "en", "pl", "Masz 15 złota.", "Scripted",
            Context: "reflow-1;qa=numbers"));

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz 15 złota.", outcome.TranslatedText);
        Assert.NotNull(outcome.QualityWarning);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Reczna_korekta_z_liczbami_nigdy_nie_jest_nieaktualna()
    {
        var provider = new ScriptedProvider((_, _) => "nie powinno paść");
        var (pipeline, cache, _) = Create(provider);
        await cache.SaveManualCorrectionAsync(new NewCacheEntry(Gold, Gold, "en", "pl", "Masz sporo złota.", "manual",
            Context: "reflow-1;qa=numbers"));

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));

        Assert.Equal("Masz sporo złota.", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }
}
