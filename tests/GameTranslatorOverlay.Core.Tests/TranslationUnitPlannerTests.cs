using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationUnitPlannerTests
{
    internal static CorpusEntry E(string text, CorpusEntryKind kind = CorpusEntryKind.Ui, string? speaker = null) => new()
    {
        Key = text,
        En = text,
        Kind = kind,
        Speaker = speaker,
        Source = "synthetic",
    };

    internal static readonly CorpusEntry[] Synthetic =
    [
        E("Inspect"),
        E("Pick Up"),
        E("Items"),
        E("Back"),
        E("The Old Lighthouse"),
        E("The lighthouse keeper left a note under the blue lamp.", CorpusEntryKind.Dialog),
        E("Watch out for the falling rocks above the tunnel entrance.", CorpusEntryKind.Subtitle, "Captain"),
        E("Stay close to me. The tunnel is very dark tonight.", CorpusEntryKind.Dialog, "Captain"),
        E("Press [X] to open the old wooden chest."),
        E("Welcome back, {0}! Ready for the next exam?"),
        E(@"Dear student,\nThe exam starts at noon sharp."),
    ];

    internal static CorpusSnapper Snapper() => new(CorpusIndex.Build(Synthetic));

    [Fact]
    public void Bez_korpusu_i_bez_akapitow_blok_zostaje_jednym_tekstem()
    {
        Assert.Null(TranslationUnitPlanner.Plan("Options\nQuit", null, splitParagraphs: false));
        Assert.Null(TranslationUnitPlanner.Plan("Hello", null, splitParagraphs: true));
        Assert.Null(TranslationUnitPlanner.Plan("You need\nthree keys to open it.", null, splitParagraphs: true));
    }

    [Fact]
    public void Klucze_po_akapitach_dziela_blok_na_twarde_podzialy()
    {
        var plan = TranslationUnitPlanner.Plan("Options\nQuit\nYou need\nthree keys.", null, splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.Equal(["Options", "Quit", "You need\nthree keys."], plan.Units.Select(static u => u.Key));
        Assert.Equal(["", "\n", "\n"], plan.Units.Select(static u => u.Separator));
        Assert.All(plan.Units, static u => Assert.False(u.Literal || u.FromCorpus));
        Assert.Null(plan.SingleKey);
    }

    [Fact]
    public void Smieciowy_akapit_zostaje_doslowny_gdy_jest_co_tlumaczyc()
    {
        var plan = TranslationUnitPlanner.Plan("Map Room:\n00", null, splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.False(plan.Units[0].Literal);
        Assert.True(plan.Units[1].Literal);
        Assert.Null(plan.SingleKey);

        var allJunk = TranslationUnitPlanner.Plan("10\n20", null, splitParagraphs: true);
        Assert.NotNull(allJunk);
        Assert.All(allJunk.Units, static u => Assert.False(u.Literal));
    }

    [Fact]
    public void Odczyt_z_bledami_ocr_dostaje_kanoniczny_klucz_korpusu()
    {
        var plan = TranslationUnitPlanner.Plan("The 1ighthouse keeper Ieft a note\nunder the bIue lamp.", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        var unit = Assert.Single(plan.Units);
        Assert.True(unit.FromCorpus);
        Assert.Equal("The lighthouse keeper left a note under the blue lamp.", unit.Key);
        Assert.Equal("The 1ighthouse keeper Ieft a note\nunder the bIue lamp.", unit.ScreenText);
        Assert.Equal(unit.Key, plan.SingleKey);
    }

    [Fact]
    public void Identyczny_tekst_korpusu_idzie_dawna_sciezka()
    {
        Assert.Null(TranslationUnitPlanner.Plan("Inspect", Snapper(), splitParagraphs: true));

        var upper = TranslationUnitPlanner.Plan("INSPECT", Snapper(), splitParagraphs: true);
        Assert.NotNull(upper);
        Assert.Equal("Inspect", Assert.Single(upper.Units).Key);
    }

    [Fact]
    public void Prefiks_mowcy_zostaje_doslowny()
    {
        var plan = TranslationUnitPlanner.Plan("Captain: Watch out for the falling rocks above the tunnel entrance.", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Units.Count);
        Assert.True(plan.Units[0].Literal);
        Assert.Equal("Captain:", plan.Units[0].ScreenText);
        Assert.Equal("Watch out for the falling rocks above the tunnel entrance.", plan.Units[1].Key);
        Assert.Equal(" ", plan.Units[1].Separator);
        Assert.Null(plan.SingleKey);
    }

    [Fact]
    public void Prefiks_mowcy_w_osobnym_wierszu_daje_nowy_wiersz()
    {
        var plan = TranslationUnitPlanner.Plan("[CAPTAIN]\nStay close to me. The tunnel is very dark tonight.", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.Equal(["[CAPTAIN]", "Stay close to me. The tunnel is very dark tonight."], plan.Units.Select(static u => u.Key));
        Assert.True(plan.Units[0].Literal);
        Assert.Equal("\n", plan.Units[1].Separator);
    }

    [Fact]
    public void Okruch_przed_etykieta_w_wierszu_zostaje_doslowny()
    {
        var plan = TranslationUnitPlanner.Plan("E Inspect", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Units.Count);
        Assert.True(plan.Units[0].Literal);
        Assert.Equal("E", plan.Units[0].ScreenText);
        Assert.Equal("Inspect", plan.Units[1].Key);
        Assert.True(plan.Units[1].FromCorpus);
        Assert.Equal(" ", plan.Units[1].Separator);
    }

    [Fact]
    public void Etykieta_i_zdanie_w_jednym_wierszu_to_dwie_jednostki()
    {
        var plan = TranslationUnitPlanner.Plan("Inspect The lighthouse keeper left a note under the blue lamp.", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.Equal(["Inspect", "The lighthouse keeper left a note under the blue lamp."], plan.Units.Select(static u => u.Key));
        Assert.All(plan.Units, static u => Assert.True(u.FromCorpus));
        Assert.Equal(" ", plan.Units[1].Separator);
    }

    [Fact]
    public void Blok_mieszany_laczy_jednostki_korpusu_i_odczytu()
    {
        var plan = TranslationUnitPlanner.Plan("Items\nSomething completely new appears here", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Units.Count);
        Assert.True(plan.Units[0].FromCorpus);
        Assert.False(plan.Units[1].FromCorpus);
        Assert.Equal("Something completely new appears here", plan.Units[1].Key);
        Assert.Equal("\n", plan.Units[1].Separator);
    }

    [Fact]
    public void Wpis_z_podstawieniem_albo_brakujacym_znacznikiem_przycisku_nie_jest_uzywany()
    {
        Assert.False(TranslationUnitPlanner.IsUsable(E("Welcome back, {0}! Ready for the next exam?"), "Welcome back, Kuba! Ready for the next exam?"));
        Assert.False(TranslationUnitPlanner.IsUsable(E("Found %d coins"), "Found 3 coins"));
        Assert.False(TranslationUnitPlanner.IsUsable(E("Press [X] to open the old wooden chest."), "Press X to open the old wooden chest."));
        Assert.True(TranslationUnitPlanner.IsUsable(E("Press [X] to open the old wooden chest."), "Press [x] to open the old wooden chest."));
        Assert.True(TranslationUnitPlanner.IsUsable(E("Open the chest"), "Open the chest"));
    }

    [Fact]
    public void Doslowne_backslash_n_w_korpusie_jest_lamaniem_wiersza()
    {
        var entry = E(@"Dear student,\nThe exam starts at noon sharp.");

        Assert.Equal("Dear student,\nThe exam starts at noon sharp.", CorpusTranslationKey.For(entry));
        Assert.Equal("dear student, the exam starts at noon sharp.", CorpusText.MatchKey(entry.En));
        Assert.Null(TranslationUnitPlanner.Plan("Dear student,\nThe exam starts at noon sharp.", Snapper(), splitParagraphs: true));
    }

    [Fact]
    public void Wyswietlanie_zachowuje_uklad_wierszy_odczytu()
    {
        var laidOut = TranslationUnitPlanner.ToScreenLayout(
            "Latarnik zostawił notatkę pod niebieską lampą.",
            "The lighthouse keeper left a note under the blue lamp.",
            "The 1ighthouse keeper Ieft a note\nunder the bIue lamp.");

        Assert.Equal(2, laidOut.Split('\n').Length);
        Assert.Equal("Latarnik zostawił notatkę pod niebieską lampą.", laidOut.Replace('\n', ' '));

        var twoParagraphs = TranslationUnitPlanner.ToScreenLayout(
            "Drogi uczniu,\nEgzamin zaczyna się w południe.", "Dear student,\nThe exam starts at noon sharp.", "Dear student, The exam starts at noon sharp.");
        Assert.Equal("Drogi uczniu, Egzamin zaczyna się w południe.", twoParagraphs);
    }

    [Fact]
    public void Wielkie_litery_odczytu_przechodza_na_tlumaczenie()
    {
        var unit = new TranslationUnit("The Old Lighthouse", "THE OLD LIGHTHOUSE", string.Empty, Literal: false, FromCorpus: true);

        Assert.Equal("STARA LATARNIA ŁÓDŹ", TranslationUnitPlanner.Display(unit, "Stara latarnia łódź"));
        Assert.True(TranslationUnitPlanner.IsUpperCase("PASS THE EXAM"));
        Assert.False(TranslationUnitPlanner.IsUpperCase("I"));
        Assert.False(TranslationUnitPlanner.IsUpperCase("Pass The Exam"));
    }

    [Fact]
    public void Skladanie_wstawia_separatory_i_tekst_doslowny()
    {
        var plan = TranslationUnitPlanner.Plan("E Inspect\nItems", Snapper(), splitParagraphs: true);

        Assert.NotNull(plan);
        var composed = TranslationUnitPlanner.Compose(plan, [null, "Zbadaj", "Przedmioty"]);
        Assert.Equal("E Zbadaj\nPrzedmioty", composed);
    }
}
