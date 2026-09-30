using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class PlayerGenderTests
{
    /// <summary>Dostawca świadomy płci gracza (jak dostawcy LLM).</summary>
    private sealed class GenderAwareProvider(Func<string, int, string> respond)
        : DialogMemoryTests.ScriptedContextualProvider(respond), IGenderAwareTranslationProvider;

    private const string Ready = "Are you ready?";
    private const string Gate = "The gate is open.";

    private static (TranslationPipeline Pipeline, InMemoryTranslationCache Cache) Create(
        ITranslationProvider provider, PlayerGender gender, bool cacheOnly = false)
    {
        var cache = new InMemoryTranslationCache();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { PlayerGender = gender, CacheOnlyMode = cacheOnly });
        return (pipeline, cache);
    }

    private static Task StoreAuto(InMemoryTranslationCache cache, string text, string translated, string? context) =>
        cache.StoreAsync(new NewCacheEntry(text, text, "en", "pl", translated, "Scripted", Context: context));

    // --- Ustawienie i wykrywanie zwrotu do gracza ---

    [Theory]
    [InlineData("female", PlayerGender.Female)]
    [InlineData("FEMALE", PlayerGender.Female)]
    [InlineData(" male ", PlayerGender.Male)]
    [InlineData("unknown", PlayerGender.Unknown)]
    [InlineData("kobieta", PlayerGender.Unknown)]
    [InlineData("", PlayerGender.Unknown)]
    [InlineData(null, PlayerGender.Unknown)]
    public void Ustawienie_plci_jest_czytane_bezpiecznie(string? setting, PlayerGender expected)
    {
        Assert.Equal(expected, PlayerGenders.Parse(setting));
    }

    [Theory]
    [InlineData(PlayerGender.Unknown, "unknown")]
    [InlineData(PlayerGender.Male, "male")]
    [InlineData(PlayerGender.Female, "female")]
    public void Plec_przechodzi_w_obie_strony_przez_ustawienie(PlayerGender gender, string setting)
    {
        Assert.Equal(setting, PlayerGenders.ToSetting(gender));
        Assert.Equal(gender, PlayerGenders.Parse(PlayerGenders.ToSetting(gender)));
    }

    [Theory]
    [InlineData("Are you ready?", true)]
    [InlineData("YOU did it!", true)]
    [InlineData("Is this your sword?", true)]
    [InlineData("The choice is yours.", true)]
    [InlineData("Take care of yourself.", true)]
    [InlineData("You're late.", true)]
    [InlineData("Thank\nyou", true)]
    [InlineData("The gate is open.", false)]
    [InlineData("Talk to the youngster.", false)]
    [InlineData("Bayou Village", false)]
    [InlineData("Yourselves", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Zwrot_do_gracza_to_osobne_slowo_you(string? text, bool expected)
    {
        Assert.Equal(expected, PlayerGenders.AddressesPlayer(text));
    }

    // --- Znacznik cache ---

    [Fact]
    public void Znacznik_plci_przechodzi_w_obie_strony()
    {
        Assert.Equal("reflow-1;pg=f", TranslationCacheContext.Build(TranslationQualityFlags.None, playerGender: PlayerGender.Female));
        Assert.Equal("reflow-1;pg=m", TranslationCacheContext.Build(TranslationQualityFlags.None, final: true, playerGender: PlayerGender.Male));
        Assert.Equal(TextReflow.FormatVersion, TranslationCacheContext.Build(TranslationQualityFlags.None, playerGender: PlayerGender.Unknown));

        var marker = TranslationCacheContext.Build(TranslationQualityFlags.NumbersChanged, final: true, playerGender: PlayerGender.Female);
        Assert.Equal("reflow-1;qa=numbers;qa-final;pg=f", marker);
        var parsed = TranslationCacheContext.Parse(marker);
        Assert.Equal(TextReflow.FormatVersion, parsed.Format);
        Assert.Equal(TranslationQualityFlags.NumbersChanged, parsed.QualityIssues);
        Assert.True(parsed.QualityFinal);
        Assert.Equal(PlayerGender.Female, parsed.PlayerGender);

        Assert.Equal(PlayerGender.Male, TranslationCacheContext.Parse("reflow-1;qa=numbers;pg=M").PlayerGender);
        Assert.True(TranslationCacheContext.Parse("reflow-1;qa=numbers;pg=m").NeedsQualityRetry);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reflow-1")]
    [InlineData("reflow-1;qa=numbers")]
    [InlineData("reflow-1;pg=x")]
    [InlineData("reflow-1;pg=")]
    public void Znacznik_bez_plci_albo_z_nieznana_to_Unknown(string? context)
    {
        Assert.Equal(PlayerGender.Unknown, TranslationCacheContext.Parse(context).PlayerGender);
    }

    [Theory]
    [InlineData("reflow-1", Ready, PlayerGender.Female, true)]
    [InlineData(null, Ready, PlayerGender.Male, true)]
    [InlineData("reflow-1;pg=m", Ready, PlayerGender.Female, true)]
    [InlineData("reflow-1;pg=f", Ready, PlayerGender.Female, false)]
    [InlineData("reflow-1;pg=f", Ready, PlayerGender.Unknown, false)]
    [InlineData("reflow-1", Ready, PlayerGender.Unknown, false)]
    [InlineData("reflow-1", Gate, PlayerGender.Female, false)]
    [InlineData("reflow-1;pg=m", Gate, PlayerGender.Female, false)]
    [InlineData("reflow-1;qa=numbers;qa-final;pg=m", Ready, PlayerGender.Female, true)]
    [InlineData("reflow-1;qa=numbers;qa-final;pg=f", Ready, PlayerGender.Female, false)]
    public void Wpis_z_inna_plcia_jest_nieaktualny_tylko_dla_zwrotu_do_gracza(
        string? context, string text, PlayerGender current, bool expected)
    {
        Assert.Equal(expected, TranslationCacheContext.IsStale(context, text, current));
    }

    [Fact]
    public void Stara_wersja_IsStale_ignoruje_plec()
    {
        Assert.False(TranslationCacheContext.IsStale("reflow-1;pg=m", Ready));
        Assert.False(TranslationCacheContext.IsStale("reflow-1", Ready));
    }

    // --- Pipeline ---

    [Fact]
    public async Task Dostawca_swiadomy_plci_zapisuje_znacznik_plci()
    {
        var provider = new GenderAwareProvider(static (text, _) => "PL " + text);
        var (pipeline, cache) = Create(provider, PlayerGender.Female);

        await pipeline.TranslateAsync([Ready, Gate], "en", "pl");

        Assert.Equal("reflow-1;pg=f", (await cache.LookupAsync(Ready, "en", "pl", ""))!.Context);
        // Znacznik zależy od dostawcy i ustawienia, nie od treści — tekst bez „you” też go ma.
        Assert.Equal("reflow-1;pg=f", (await cache.LookupAsync(Gate, "en", "pl", ""))!.Context);
        Assert.Equal(PlayerGender.Female, Assert.Single(provider.Contexts).PlayerGender);
    }

    [Fact]
    public async Task Dostawca_nieswiadomy_plci_i_nieznana_plec_nie_dopisuja_znacznika()
    {
        var plain = new DialogMemoryTests.ScriptedContextualProvider(static (text, _) => "PL " + text);
        var (plainPipeline, plainCache) = Create(plain, PlayerGender.Female);
        await plainPipeline.TranslateAsync([Ready], "en", "pl");
        Assert.Equal(TextReflow.FormatVersion, (await plainCache.LookupAsync(Ready, "en", "pl", ""))!.Context);

        var aware = new GenderAwareProvider(static (text, _) => "PL " + text);
        var (awarePipeline, awareCache) = Create(aware, PlayerGender.Unknown);
        await awarePipeline.TranslateAsync([Ready], "en", "pl");
        Assert.Equal(TextReflow.FormatVersion, (await awareCache.LookupAsync(Ready, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Wpis_z_inna_plcia_jest_tlumaczony_ponownie_raz()
    {
        var provider = new GenderAwareProvider(static (_, _) => "Jesteś gotowa?");
        var (pipeline, cache) = Create(provider, PlayerGender.Female);
        await StoreAuto(cache, Ready, "Jesteś gotowy?", "reflow-1;pg=m");

        var first = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));
        var second = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowa?", first.TranslatedText);
        Assert.Equal(TranslationOrigin.Provider, first.Origin);
        Assert.Equal("Jesteś gotowa?", second.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, second.Origin);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("reflow-1;pg=f", (await cache.LookupAsync(Ready, "en", "pl", ""))!.Context);
    }

    [Fact]
    public async Task Stary_wpis_bez_plci_zwracajacy_sie_do_gracza_jest_odswiezany()
    {
        var provider = new GenderAwareProvider(static (_, _) => "Jesteś gotowy?");
        var (pipeline, cache) = Create(provider, PlayerGender.Male);
        await StoreAuto(cache, Ready, "Gotowa?", null);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowy?", outcome.TranslatedText);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Tekst_bez_zwrotu_do_gracza_zostaje_w_cache_mimo_innej_plci()
    {
        var provider = new GenderAwareProvider(static (text, _) => "PL " + text);
        var (pipeline, cache) = Create(provider, PlayerGender.Female);
        await StoreAuto(cache, Gate, "Brama jest otwarta.", "reflow-1;pg=m");

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gate], "en", "pl"));

        Assert.Equal("Brama jest otwarta.", outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Dostawca_nieswiadomy_plci_nigdy_nie_uznaje_wpisu_za_nieaktualny()
    {
        var provider = new DialogMemoryTests.ScriptedContextualProvider(static (text, _) => "PL " + text);
        var (pipeline, cache) = Create(provider, PlayerGender.Female);
        await StoreAuto(cache, Ready, "Jesteś gotowy?", "reflow-1;pg=m");

        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowy?", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Nieznana_plec_nigdy_nie_uznaje_wpisu_za_nieaktualny()
    {
        var provider = new GenderAwareProvider(static (text, _) => "PL " + text);
        var (pipeline, cache) = Create(provider, PlayerGender.Unknown);
        await StoreAuto(cache, Ready, "Jesteś gotowy?", "reflow-1;pg=m");

        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowy?", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Reczna_korekta_nigdy_nie_jest_nieaktualna_przez_plec()
    {
        var provider = new GenderAwareProvider(static (text, _) => "PL " + text);
        var (pipeline, cache) = Create(provider, PlayerGender.Female);
        await cache.SaveManualCorrectionAsync(new NewCacheEntry(Ready, Ready, "en", "pl", "Gotów?", "manual"));

        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Gotów?", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Cache_only_pokazuje_wpis_z_inna_plcia_bez_wysylania()
    {
        var provider = new GenderAwareProvider(static (text, _) => "PL " + text);
        var (pipeline, cache) = Create(provider, PlayerGender.Female, cacheOnly: true);
        await StoreAuto(cache, Ready, "Jesteś gotowy?", "reflow-1;pg=m");

        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowy?", outcome.TranslatedText);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Blad_dostawcy_przy_zmianie_plci_pokazuje_stary_wynik()
    {
        var provider = new GenderAwareProvider(static (text, _) => "PL " + text)
        {
            ThrowOnCall = new TranslationException(TranslationFailureKind.NetworkError, "offline"),
        };
        var (pipeline, cache) = Create(provider, PlayerGender.Female);
        await StoreAuto(cache, Ready, "Jesteś gotowy?", "reflow-1;pg=m");

        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowy?", outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Null(outcome.ErrorMessage);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Nowy_wynik_nadpisuje_wpis_profilu_z_inna_plcia()
    {
        var provider = new GenderAwareProvider(static (_, _) => "Jesteś gotowa?");
        var cache = new InMemoryTranslationCache();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { PlayerGender = PlayerGender.Female, GameProfile = "rpg" });
        await cache.StoreAsync(new NewCacheEntry(Ready, Ready, "en", "pl", "Jesteś gotowy?", "Scripted", GameProfile: "rpg",
            Context: "reflow-1;pg=m"));

        await pipeline.TranslateAsync([Ready], "en", "pl");
        var again = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowa?", again.TranslatedText);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("reflow-1;pg=f", (await cache.LookupAsync(Ready, "en", "pl", "rpg"))!.Context);
    }
}
