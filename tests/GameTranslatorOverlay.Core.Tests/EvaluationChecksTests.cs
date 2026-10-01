using GameTranslatorOverlay.Core.Evaluation;
using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Tests;

public class EvaluationChecksTests
{
    // ---- Liczby ----

    [Theory]
    [InlineData("+15% increased Attack Speed", "+15% zwiększonej szybkości ataku")]
    [InlineData("Regenerate 1.5% of Life per second", "Regeneruje 1,5% życia na sekundę")]
    [InlineData("Adds 10-15 Fire Damage", "Dodaje 10–15 obrażeń od ognia")]
    [InlineData("Sell value: 1,250 gold", "Wartość sprzedaży: 1250 sztuk złota")]
    [InlineData("Sell value: 1,250 gold", "Wartość sprzedaży: 1 250 sztuk złota")]
    [InlineData("Stack Size: 3/10", "Rozmiar stosu: 3/10")]
    [InlineData("Cooldown 2.50 seconds", "Odnowienie 2,5 sekundy")]
    public void Zachowane_liczby_nie_sa_zglaszane(string source, string hypothesis)
    {
        Assert.Null(TranslationChecks.CheckNumbers(source, hypothesis));
    }

    [Fact]
    public void Zmieniona_liczba_jest_zglaszana_jako_brakujaca_i_nadmiarowa()
    {
        var issue = TranslationChecks.CheckNumbers("+15% increased Attack Speed", "+51% zwiększonej szybkości ataku");

        Assert.NotNull(issue);
        Assert.Equal(TranslationCheckKind.Numbers, issue.Kind);
        Assert.Contains("brakuje 15", issue.Message);
        Assert.Contains("nadmiarowe 51", issue.Message);
    }

    [Fact]
    public void Zgubiona_powtorzona_liczba_jest_zglaszana()
    {
        // (0/5) i „5” — liczby są porównywane jako multizbiór, nie zbiór.
        var issue = TranslationChecks.CheckNumbers("Collect 5 Ember Shards (0/5)", "Zbierz Odłamki Żaru (0/5)");

        Assert.NotNull(issue);
        Assert.Contains("brakuje 5", issue.Message);
    }

    [Fact]
    public void Liczby_sa_kanonizowane()
    {
        Assert.Equal(["1.5", "1000", "1250.5", "7", "0"],
            TranslationChecks.ExtractNumbers("1,5 i 1 000 oraz 1,250.5, 007 i 0"));
    }

    // ---- Forma grzecznościowa ----

    [Theory]
    [InlineData("Are you sure you want to quit?", "Czy Pan na pewno chce wyjść?")]
    [InlineData("Can you help me?", "Czy może mi pani pomóc?")]
    [InlineData("Welcome, travelers!", "Witamy Państwa!")]
    public void Forma_grzecznosciowa_jest_zglaszana(string source, string hypothesis)
    {
        var issue = TranslationChecks.CheckFormalAddress(source, hypothesis);

        Assert.NotNull(issue);
        Assert.Equal(TranslationCheckKind.FormalAddress, issue.Kind);
    }

    [Theory]
    [InlineData("Are you sure you want to quit?", "Na pewno chcesz wyjść?", null)]
    [InlineData("Don't call me sir.", "Nie mów do mnie „panie”.", null)]
    [InlineData("The Lord of Ashes awaits.", "Pan Popiołów czeka.", null)]
    [InlineData("Bow before the Dark One.", "Pokłoń się przed Panem Ciemności.", null)]
    [InlineData("The old man is waiting.", "Starszy pan czeka.", "Starszy pan czeka.")]
    public void Tytul_nazwa_albo_forma_z_referencji_nie_jest_zglaszana(string source, string hypothesis, string? reference)
    {
        Assert.Null(TranslationChecks.CheckFormalAddress(source, hypothesis, reference));
    }

    [Fact]
    public void Slowa_zawierajace_pan_nie_sa_forma_grzecznosciowa()
    {
        Assert.Null(TranslationChecks.CheckFormalAddress("Cast the spell", "Rzuć zaklęcie panowania"));
        Assert.Null(TranslationChecks.CheckFormalAddress("Panic!", "Panika!"));
    }

    // ---- Rodzaj ----

    [Fact]
    public void Meskie_formy_przy_oczekiwanym_rodzaju_zenskim_sa_zglaszane()
    {
        var issue = TranslationChecks.CheckGender("Czekałem na ciebie całą noc.", "f");

        Assert.NotNull(issue);
        Assert.Equal(TranslationCheckKind.Gender, issue.Kind);
    }

    [Fact]
    public void Zenskie_formy_przy_oczekiwanym_rodzaju_meskim_sa_zglaszane()
    {
        Assert.NotNull(TranslationChecks.CheckGender("Jestem gotowa. Ruszajmy.", "m"));
        Assert.NotNull(TranslationChecks.CheckGender("Dobrze się spisałaś.", "m"));
    }

    [Theory]
    [InlineData("Czekałam na ciebie całą noc.", "f")]
    [InlineData("Byłam gotowa odejść.", "f")]
    [InlineData("Widziałem to na własne oczy.", "m")]
    [InlineData("Jestem gotowy.", "m")]
    [InlineData("Spakuj się. Wypływamy o świcie.", "f")]
    [InlineData("Czekałem na ciebie.", null)]
    public void Brak_dowodu_albo_zgodny_rodzaj_nie_jest_zglaszany(string hypothesis, string? expected)
    {
        Assert.Null(TranslationChecks.CheckGender(hypothesis, expected));
    }

    [Fact]
    public void Rzeczowniki_w_narzedniku_nie_sa_dowodem_rodzaju_meskiego()
    {
        // „nad stołem”, „z aniołem”, „rozdziałem” — narzędnik, nie czasownik.
        Assert.Equal((0, 0), TranslationChecks.GenderEvidence("Siedziała nad stołem z aniołem, za rozdziałem."));
        Assert.Null(TranslationChecks.CheckGender("Stała nad stołem.", "f"));
    }

    [Fact]
    public void Czasowniki_o_koncowkach_podobnych_do_rzeczownikow_sa_dowodem()
    {
        // „chciałem” kończy się na „ciałem”, „usłyszałem” na „szałem”, „niosłem” na „osłem”.
        Assert.Equal((0, 3), TranslationChecks.GenderEvidence("Chciałem, usłyszałem, niosłem."));
    }

    // ---- Słownik ----

    private static readonly GlossaryTerm EnergyShield = new("Energy Shield", "Tarcza Energii");

    [Theory]
    [InlineData("Tarcza", "tarc")]
    [InlineData("Energii", "energ")]
    [InlineData("Żaru", "ża")]
    public void Rdzen_to_min_5_i_dlugosc_minus_2_pierwszych_liter(string word, string expected)
    {
        Assert.Equal(expected, TranslationChecks.PolishStem(word));
    }

    [Fact]
    public void Krotkie_slowa_nie_maja_rdzenia()
    {
        Assert.Null(TranslationChecks.PolishStem("się"));
        Assert.Null(TranslationChecks.PolishStem("do"));
    }

    [Fact]
    public void Odmieniony_termin_slownika_jest_zaliczany()
    {
        var issues = TranslationChecks.CheckGlossary("+40 to maximum energy shield", "+40 do maksymalnej Tarczy Energii", [EnergyShield]);

        Assert.Empty(issues);
    }

    [Fact]
    public void Pominiety_termin_slownika_jest_zglaszany()
    {
        var issues = TranslationChecks.CheckGlossary("+40 to maximum Energy Shield", "+40 do maksymalnej osłony energetycznej", [EnergyShield]);

        var issue = Assert.Single(issues);
        Assert.Equal(TranslationCheckKind.Glossary, issue.Kind);
        Assert.Contains("Energy Shield", issue.Message);
    }

    [Fact]
    public void Termin_nieobecny_w_zrodle_nie_jest_sprawdzany()
    {
        Assert.Empty(TranslationChecks.CheckGlossary("+15% increased Attack Speed", "+15% szybkości ataku", [EnergyShield]));
        // Tylko całe słowa: „Energy Shielded” nie zawiera terminu „Energy Shield”.
        Assert.Empty(TranslationChecks.CheckGlossary("Energy Shielded", "Osłonięty energią", [EnergyShield]));
    }

    // ---- Wiersze i długość ----

    [Fact]
    public void Inna_liczba_wierszy_jest_zglaszana()
    {
        Assert.Null(TranslationChecks.CheckParagraphs("You must find\nthe old Waystone.", "Musisz odnaleźć\nstary Kamień Drogi."));
        var issue = TranslationChecks.CheckParagraphs("You must find\nthe old Waystone.", "Musisz odnaleźć stary Kamień Drogi.");

        Assert.NotNull(issue);
        Assert.Equal(TranslationCheckKind.Paragraphs, issue.Kind);
    }

    [Fact]
    public void Stosunek_dlugosci_liczy_znaki_bez_bialych()
    {
        // „ab cd” = 4 znaki, „abcdefgh” = 8 → 2,0
        Assert.Equal(2.0, TranslationChecks.LengthRatio("ab cd", "abcdefgh"), precision: 9);
        Assert.Equal(0, TranslationChecks.LengthRatio("", "abc"));
        Assert.Null(TranslationChecks.CheckLength(2.0));
        Assert.Null(TranslationChecks.CheckLength(0.5));
        Assert.NotNull(TranslationChecks.CheckLength(2.01));
        Assert.NotNull(TranslationChecks.CheckLength(0.3));
    }

    [Fact]
    public void Run_zbiera_wszystkie_uwagi()
    {
        var result = TranslationChecks.Run(new TranslationCheckInput(
            Source: "I was ready to spend 15 gold on the Energy Shield.\nAre you?",
            Hypothesis: "Byłem gotowy wydać 51 sztuk złota na tarczę. Czy Pan też?",
            Reference: "Byłam gotowa wydać 15 sztuk złota na Tarczę Energii.\nA ty?",
            ExpectGender: "f",
            Terms: [EnergyShield]));

        Assert.False(result.Passed);
        var kinds = result.Issues.Select(static i => i.Kind).ToHashSet();
        Assert.Equal(
            new HashSet<TranslationCheckKind>
            {
                TranslationCheckKind.Numbers, TranslationCheckKind.FormalAddress, TranslationCheckKind.Gender,
                TranslationCheckKind.Glossary, TranslationCheckKind.Paragraphs,
            },
            kinds);
    }

    [Fact]
    public void Referencje_z_przykladowego_korpusu_przechodza_wszystkie_kontrole()
    {
        // Referencje są wzorcem — jeśli kontrola zgłasza coś w referencji, to kontrola
        // albo korpus jest błędny.
        var corpus = EvalCorpus.Parse(File.ReadAllText(EvaluationTestPaths.SampleCorpus));
        foreach (var line in corpus.Lines)
        {
            var result = TranslationChecks.Run(new TranslationCheckInput(
                line.Source, line.Reference, line.Reference, line.ExpectGender, line.Terms));
            Assert.True(result.Passed, $"{line.Id}: {string.Join(" ", result.Issues.Select(static i => i.Message))}");
        }
    }
}

internal static class EvaluationTestPaths
{
    public static string SampleCorpus => Path.Combine(RepositoryRoot(), "eval", "en-pl.sample.jsonl");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameTranslatorOverlay.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (GameTranslatorOverlay.slnx).");
    }
}
