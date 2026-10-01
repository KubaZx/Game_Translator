using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Wpis już ostateczny (qa-final) tłumaczony ponownie tylko z powodu innej płci gracza nie
/// może zaczynać od nowa cyklu ponowień kontroli jakości — każde to płatne zapytanie.
/// </summary>
public class GenderRetranslationQualityFinalTests
{
    private sealed class GenderAwareRetryableProvider(Func<string, int, string> respond)
        : DialogMemoryTests.ScriptedContextualProvider(respond), IGenderAwareTranslationProvider, IRetryableTranslationProvider;

    private const string Deadline = "You have until 10:30 to leave.";

    [Fact]
    public async Task Ponowienie_z_powodu_plci_zachowuje_ostatecznosc_znacznika_jakosci()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(Deadline, Deadline, "en", "pl", "Masz czas do 22:30, żeby wyjść.", "Scripted",
            Context: "reflow-1;qa=numbers;qa-final"));
        var provider = new GenderAwareRetryableProvider((_, _) => "Masz czas do 22:30, żeby wyjść.");
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { PlayerGender = PlayerGender.Female });

        for (var i = 0; i < 3; i++) await pipeline.TranslateAsync([Deadline], "en", "pl");

        Assert.Equal(1, provider.CallCount);
        var context = (await cache.LookupAsync(Deadline, "en", "pl", ""))!.Context!;
        Assert.Contains("qa-final", context);
        Assert.Contains("pg=f", context);
    }

    [Fact]
    public async Task Wpis_qa_final_bez_problemu_po_ponowieniu_traci_znacznik_jakosci()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry(Deadline, Deadline, "en", "pl", "Masz czas do 22:30, żeby wyjść.", "Scripted",
            Context: "reflow-1;qa=numbers;qa-final"));
        var provider = new GenderAwareRetryableProvider((_, _) => "Masz czas do 10:30, żeby wyjść.");
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(),
            new TranslationPipelineOptions { PlayerGender = PlayerGender.Female });

        await pipeline.TranslateAsync([Deadline], "en", "pl");

        var context = (await cache.LookupAsync(Deadline, "en", "pl", ""))!.Context!;
        Assert.DoesNotContain("qa", context);
        Assert.Contains("pg=f", context);
    }
}
