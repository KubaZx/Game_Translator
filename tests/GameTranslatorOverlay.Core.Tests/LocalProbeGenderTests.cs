using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Styk dwóch zmian: szybka próba lokalna (klatka w pełni znana nie czeka na kolejkę) i wpis
/// cache nieaktualny z powodu innej płci gracza. Próba nie może uznać takiego wpisu za znany —
/// inaczej klatka pokazałaby „gotowy” mimo przełączenia na postać kobiecą — i nie może zostawić
/// zapasowego wyniku, bo nic jeszcze nie wysłała.
/// </summary>
public class LocalProbeGenderTests
{
    private sealed class GenderAwareProvider(Func<string, int, string> respond)
        : DialogMemoryTests.ScriptedContextualProvider(respond), IGenderAwareTranslationProvider;

    private const string Ready = "Are you ready?";

    private static async Task<(TranslationPipeline Pipeline, GenderAwareProvider Provider)> CreateWithMaleEntry()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(Ready, Ready, "en", "pl", "Jesteś gotowy?", "Scripted",
            Context: "reflow-1;pg=m"));
        var provider = new GenderAwareProvider(static (_, _) => "Jesteś gotowa?");
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { PlayerGender = PlayerGender.Female });
        return (pipeline, provider);
    }

    [Fact]
    public async Task Proba_lokalna_nie_uznaje_wpisu_z_inna_plcia_gracza_za_znany()
    {
        var (pipeline, provider) = await CreateWithMaleEntry();

        var local = await pipeline.TranslateLocalAsync([Ready], "en", "pl");

        Assert.Null(Assert.Single(local));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Po_probie_lokalnej_pelna_sciezka_tlumaczy_raz_z_nowa_plcia()
    {
        var (pipeline, provider) = await CreateWithMaleEntry();

        await pipeline.TranslateLocalAsync([Ready], "en", "pl");
        var outcome = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));
        var again = Assert.Single(await pipeline.TranslateAsync([Ready], "en", "pl"));

        Assert.Equal("Jesteś gotowa?", outcome.TranslatedText);
        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
        Assert.Equal("Jesteś gotowa?", again.TranslatedText);
        Assert.Equal(1, provider.CallCount);
    }
}
