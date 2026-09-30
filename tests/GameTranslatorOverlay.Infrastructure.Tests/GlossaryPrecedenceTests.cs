using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// Glosariusz DeepL i lokalne tłumaczenie terminów muszą wybierać tego samego zwycięzcę —
/// inaczej ten sam termin w dymku i w zdaniu miałby dwa różne tłumaczenia.
/// </summary>
public class GlossaryPrecedenceTests
{
    [Fact]
    public void Przy_remisie_DeepL_bierze_ten_sam_termin_co_tlumaczenie_lokalne()
    {
        var service = new GlossaryService();
        service.LoadDocument(new GlossaryDocument { Name = "global", Terms = [new GlossaryTerm("Armour", "Zbroja")] });
        service.LoadDocument(new GlossaryDocument { Name = "profil", Terms = [new GlossaryTerm("Armour", "Pancerz")] });

        var entry = Assert.Single(DeepLGlossaryManager.BuildEntries(service.PersistableTerms));

        Assert.True(service.TryTranslateExact("Armour", out var local));
        Assert.Equal("Pancerz", local);
        Assert.Equal(("Armour", "Pancerz"), entry);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Kazdy_wpis_DeepL_zgadza_sie_z_TryTranslateExact(int seed)
    {
        // Losowe zestawy z wieloma kolizjami: te same źródła w różnej wielkości liter,
        // z rozróżnianiem i bez, z remisami i różnymi priorytetami.
        var random = new Random(seed);
        string[] spellings = ["Armour", "armour", "ARMOUR", "Energy Shield", "energy shield", "Rune", "rune", "Waystone"];
        var service = new GlossaryService();
        for (var round = 0; round < 25; round++)
        {
            var terms = Enumerable.Range(0, random.Next(1, 8))
                .Select(i => new GlossaryTerm(
                    spellings[random.Next(spellings.Length)],
                    $"T{round}_{i}",
                    CaseSensitive: random.Next(2) == 0,
                    Priority: random.Next(3) * 5))
                .ToList();
            service.LoadDocument(new GlossaryDocument { Name = $"d{round}", Terms = terms });

            foreach (var (source, target) in DeepLGlossaryManager.BuildEntries(service.PersistableTerms))
            {
                Assert.True(service.TryTranslateExact(source, out var local), $"Brak lokalnego tłumaczenia „{source}”.");
                Assert.Equal(local, target);
            }
        }
    }

    [Fact]
    public void Etykiety_nie_trafiaja_do_glosariusza_DeepL()
    {
        var entries = DeepLGlossaryManager.BuildEntries(
        [
            new GlossaryTerm("Save", "Zapisz", Scope: GlossaryScope.Label),
            new GlossaryTerm("Waystone", "Kamień drogi", Scope: GlossaryScope.Any),
            new GlossaryTerm("Rune", "Runa"),
        ]);

        Assert.Equal([("Rune", "Runa"), ("Waystone", "Kamień drogi")], entries);
    }

    [Fact]
    public void Prompt_LLM_prosi_o_odmiane_terminow_zamiast_sztywnej_podmiany()
    {
        var context = new TranslationContext(null, [new GlossaryTerm("Waystone", "Kamień drogi")]);

        var prompt = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", context);

        Assert.Contains("Waystone => Kamień drogi", prompt);
        Assert.Contains("inflected to fit Polish grammar", prompt);
        Assert.DoesNotContain("always use these translations", prompt);
    }
}
