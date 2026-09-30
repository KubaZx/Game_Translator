using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationQualityGateTests
{
    public static TheoryData<string, string, TranslationQualityFlags> Przypadki => new()
    {
        // Liczby: separatory, godziny, zakresy, porządkowe.
        { "It is 10:30.", "Jest 10:30.", TranslationQualityFlags.None },
        { "It is 10:30.", "Jest 10.30.", TranslationQualityFlags.None },
        { "It is 10:30.", "Jest 11:30.", TranslationQualityFlags.NumbersChanged },
        { "Wait 5–10 minutes.", "Poczekaj 5–10 minut.", TranslationQualityFlags.None },
        { "Wait 5–10 minutes.", "Poczekaj od 5 do 10 minut.", TranslationQualityFlags.None },
        { "Wait 5–10 minutes.", "Poczekaj 5 minut.", TranslationQualityFlags.NumbersChanged },
        { "Levels 3-7 unlocked", "Odblokowano poziomy 3-7", TranslationQualityFlags.None },
        { "It costs 1,000 gold.", "Kosztuje 1000 złota.", TranslationQualityFlags.None },
        { "It costs 1,000 gold.", "Kosztuje 1 000 złota.", TranslationQualityFlags.None },
        { "It costs 1,000 gold.", "Kosztuje 1 000 złota.", TranslationQualityFlags.None },
        { "It costs 1,000 gold.", "Kosztuje 1.000 złota.", TranslationQualityFlags.None },
        { "It costs 1,000 gold.", "Kosztuje 100 złota.", TranslationQualityFlags.NumbersChanged },
        { "It costs 1000 gold.", "Kosztuje 1 000 złota.", TranslationQualityFlags.None },
        { "Speed x2.5", "Prędkość x2,5", TranslationQualityFlags.None },
        { "Speed x2.5", "Prędkość x25", TranslationQualityFlags.NumbersChanged },
        { "Total: 1,234.5 XP", "Razem: 1234,5 PD", TranslationQualityFlags.None },
        { "You finished 1st!", "Zająłeś 1. miejsce!", TranslationQualityFlags.None },
        { "Take 2 potions", "Weź 2 mikstury i 3 bandaże", TranslationQualityFlags.None },
        { "Take 2 potions", "Weź dwie mikstury", TranslationQualityFlags.NumbersChanged },
        { "Take 5 10 times", "Weź 5 razy 10", TranslationQualityFlags.None },
        { "No numbers here", "Tu nie ma liczb", TranslationQualityFlags.None },

        // Brak tłumaczenia: identyczny wynik z angielskimi słowami funkcyjnymi.
        { "Talk to the blacksmith", "Talk to the blacksmith", TranslationQualityFlags.Untranslated },
        { "Talk to the\nblacksmith", "talk to the blacksmith", TranslationQualityFlags.Untranslated },
        { "Tokyo Drift Club", "Tokyo Drift Club", TranslationQualityFlags.None },
        { "Go away", "Go away", TranslationQualityFlags.None },
        { "OK", "OK", TranslationQualityFlags.None },

        // „Rozgadany” wynik.
        { "Yes", "Tak", TranslationQualityFlags.None },
        { "Yes", "Tak. (Uwaga: w tym kontekście „yes” oznacza zgodę gracza na zadanie.)",
            TranslationQualityFlags.Runaway },
        { "Take 2 potions", "Weź mikstury. Uwaga tłumacza: oryginał mówi o dwóch, ale to nieistotne dla fabuły.",
            TranslationQualityFlags.NumbersChanged | TranslationQualityFlags.Runaway },

        // Pusty wynik.
        { "Hello", "", TranslationQualityFlags.Empty },
        { "Hello", "   \n ", TranslationQualityFlags.Empty },
    };

    [Theory]
    [MemberData(nameof(Przypadki))]
    public void Kontrola_jakosci_zwraca_oczekiwane_flagi(string source, string translated, TranslationQualityFlags expected)
    {
        Assert.Equal(expected, TranslationQualityGate.Check(source, translated));
    }

    [Fact]
    public void Brak_problemow_nie_ma_opisu_a_problemy_maja_polski_opis()
    {
        Assert.Null(TranslationQualityGate.Describe(TranslationQualityFlags.None));
        Assert.Equal(TranslationQualityGate.EmptyResultMessage, TranslationQualityGate.Describe(TranslationQualityFlags.Empty));
        var description = TranslationQualityGate.Describe(TranslationQualityFlags.NumbersChanged | TranslationQualityFlags.Runaway);
        Assert.Contains("liczby", description);
        Assert.Contains("długie", description);
    }

    [Fact]
    public void Pusty_wynik_jest_zawsze_gorszy_od_innych_problemow()
    {
        var all = TranslationQualityFlags.NumbersChanged | TranslationQualityFlags.Untranslated | TranslationQualityFlags.Runaway;
        Assert.True(TranslationQualityGate.Severity(all) < TranslationQualityGate.Severity(TranslationQualityFlags.Empty));
        Assert.True(TranslationQualityGate.Severity(TranslationQualityFlags.None)
            < TranslationQualityGate.Severity(TranslationQualityFlags.NumbersChanged));
    }
}

public class TranslationCacheContextTests
{
    [Theory]
    [InlineData(null, "One line", false)]
    [InlineData(null, "Two\nlines", true)]
    [InlineData("reflow-1", "Two\nlines", false)]
    [InlineData("reflow-0", "Two\nlines", true)]
    [InlineData("reflow-1;qa=numbers", "One line", true)]
    [InlineData("reflow-1;qa=numbers", "Two\nlines", true)]
    [InlineData("reflow-1;qa=numbers;qa-final", "One line", false)]
    [InlineData("reflow-1;qa=numbers;qa-final", "Two\nlines", false)]
    [InlineData("reflow-1;qa=future-flag", "One line", true)]
    public void Wpis_jest_nieaktualny_dla_starego_formatu_albo_niesprawdzonego_problemu(
        string? context, string normalized, bool expected)
    {
        Assert.Equal(expected, TranslationCacheContext.IsStale(context, normalized));
    }

    [Fact]
    public void Znacznik_bez_problemow_to_sama_wersja_formatu()
    {
        Assert.Equal(TextReflow.FormatVersion, TranslationCacheContext.Build(TranslationQualityFlags.None));
        Assert.Equal(TextReflow.FormatVersion, TranslationCacheContext.Build(TranslationQualityFlags.None, final: true));
    }

    [Fact]
    public void Znacznik_problemow_przechodzi_w_obie_strony()
    {
        var flags = TranslationQualityFlags.NumbersChanged | TranslationQualityFlags.Runaway;
        var marker = TranslationCacheContext.Build(flags);
        Assert.Equal("reflow-1;qa=numbers,runaway", marker);

        var parsed = TranslationCacheContext.Parse(marker);
        Assert.Equal(TextReflow.FormatVersion, parsed.Format);
        Assert.Equal(flags, parsed.QualityIssues);
        Assert.True(parsed.NeedsQualityRetry);

        var final = TranslationCacheContext.Parse(TranslationCacheContext.Build(flags, final: true));
        Assert.Equal(flags, final.QualityIssues);
        Assert.False(final.NeedsQualityRetry);
    }
}
