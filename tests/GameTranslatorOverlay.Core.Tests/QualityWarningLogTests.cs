using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class QualityWarningLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 21, 15, 0, TimeSpan.FromHours(2));

    private static TranslationOutcome Warned(
        string source, string translated, TranslationQualityFlags issues = TranslationQualityFlags.NumbersChanged) =>
        new(source, source, translated, TranslationOrigin.Provider) { QualityIssues = issues };

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Start;
    }

    [Fact]
    public void Ostrzezenie_trafia_na_liste_z_oryginalem_tlumaczeniem_powodem_i_godzina()
    {
        var clock = new Clock();
        var log = new QualityWarningLog(() => clock.Now);

        Assert.True(log.Record(Warned("The lantern costs 150 gold.", "Latarnia kosztuje złoto.")));

        var entry = Assert.Single(log.Recent);
        Assert.Equal("The lantern costs 150 gold.", entry.SourceText);
        Assert.Equal("Latarnia kosztuje złoto.", entry.TranslatedText);
        Assert.Equal(TranslationQualityFlags.NumbersChanged, entry.Issues);
        Assert.Equal("zmienione liczby", entry.Reason);
        Assert.Contains("liczby z oryginału", entry.Hint);
        Assert.Equal(Start, entry.Time);
        Assert.False(entry.IsCorrected);
        Assert.Equal(1, log.Total);
    }

    [Fact]
    public void Pozycja_pamieta_profil_i_jezyki_z_chwili_zapisu()
    {
        var context = new QualityWarningContext("latarnia", "en", "pl");
        var log = new QualityWarningLog { ContextSource = () => context };

        log.Record(Warned("Level 20 door", "Drzwi poziomu"));
        context = new QualityWarningContext("inny", "en", "de");

        Assert.Equal(new QualityWarningContext("latarnia", "en", "pl"), Assert.Single(log.Recent).Context);
    }

    [Fact]
    public void Zapomnienie_czysci_liste_i_pamiec_par()
    {
        var log = new QualityWarningLog();
        var outcome = Warned("Level 20 door", "Drzwi poziomu");
        log.Record(outcome);

        var before = log.Generation;
        log.Forget();

        Assert.Empty(log.Recent);
        Assert.Equal(0, log.Total);
        Assert.False(log.Record(outcome, before));
        Assert.True(log.Record(outcome, log.Generation));
        Assert.Single(log.Recent);
    }

    [Fact]
    public void Ostrzezenie_z_fragmentu_bloku_mowi_o_fragmencie()
    {
        var log = new QualityWarningLog();
        var outcome = Warned("You have 150 gold.\nTalk to the keeper now.", "Masz złoto.\nTalk to the keeper now.",
            TranslationQualityFlags.Untranslated) with
        {
            Parts = [new("You have 150 gold.", null, TranslationOrigin.Provider, false), new("Talk to the keeper now.", null, TranslationOrigin.Provider, false)],
        };

        log.Record(outcome);

        var entry = Assert.Single(log.Recent);
        Assert.True(entry.PartOfBlock);
        Assert.StartsWith("Dotyczy co najmniej jednego z fragmentów bloku.", entry.Hint);
        Assert.DoesNotContain("fragmentów", TranslationQualityGate.Hint(TranslationQualityFlags.Untranslated, partOfBlock: false));
    }

    [Fact]
    public void Wynik_bez_ostrzezenia_albo_bez_tlumaczenia_nie_trafia_na_liste()
    {
        var log = new QualityWarningLog();

        Assert.False(log.Record(new TranslationOutcome("Open", "Open", "Otwórz", TranslationOrigin.Cache)));
        Assert.False(log.Record(new TranslationOutcome("Open", "Open", null, TranslationOrigin.Unavailable, "Brak wyniku.")
        {
            QualityIssues = TranslationQualityFlags.Empty,
        }));
        Assert.False(log.Record(null));

        Assert.Empty(log.Recent);
        Assert.Equal(0, log.Total);
        Assert.Equal(0, log.Version);
    }

    [Fact]
    public void Ta_sama_para_z_kolejnych_odczytow_live_liczy_sie_raz()
    {
        var log = new QualityWarningLog();
        var outcome = Warned("Level 20 door", "Drzwi poziomu");

        var added = 0;
        for (var pass = 0; pass < 12; pass++) added += log.RecordAll([outcome, outcome with { SourceText = "Level 20 door " }]);

        Assert.Equal(1, added);
        Assert.Single(log.Recent);
        Assert.Equal(1, log.Total);
    }

    [Fact]
    public void Inne_tlumaczenie_tego_samego_tekstu_to_nowa_pozycja()
    {
        var log = new QualityWarningLog();

        log.Record(Warned("Talk to the keeper", "Talk to the keeper", TranslationQualityFlags.Untranslated));
        log.Record(Warned("Talk to the keeper", "Porozmawiaj z latarnikiem. Uwaga: dosłownie „strażnik”, ale tu chodzi o latarnika.",
            TranslationQualityFlags.Runaway));

        Assert.Equal(2, log.Total);
        Assert.Equal(["za długie", "bez tłumaczenia"], log.Recent.Select(static e => e.Reason));
    }

    [Fact]
    public void Lista_trzyma_najwyzej_20_najnowszych_pozycji()
    {
        var clock = new Clock();
        var log = new QualityWarningLog(() => clock.Now);

        for (var i = 1; i <= 25; i++)
        {
            clock.Now = Start.AddSeconds(i);
            log.Record(Warned($"Floor {i} of the lighthouse", "Piętro latarni"));
        }

        var recent = log.Recent;
        Assert.Equal(QualityWarningLog.Capacity, recent.Count);
        Assert.Equal(20, recent.Count);
        Assert.Equal("Floor 25 of the lighthouse", recent[0].SourceText);
        Assert.Equal("Floor 6 of the lighthouse", recent[^1].SourceText);
        Assert.True(recent.Zip(recent.Skip(1)).All(static pair => pair.First.Time > pair.Second.Time));
        Assert.Equal(25, log.Total);
    }

    [Fact]
    public void Wyczysc_oproznia_liste_ale_zostawia_licznik_sesji_i_nie_wraca_z_tym_samym_napisem()
    {
        var log = new QualityWarningLog();
        var visible = Warned("Collect 3 keys", "Zbierz klucze");
        log.Record(visible);
        log.Record(Warned("Wait 10 seconds", "Poczekaj chwilę"));

        log.Clear();

        Assert.Empty(log.Recent);
        Assert.Equal(2, log.Total);
        Assert.False(log.Record(visible));
        Assert.Empty(log.Recent);
        Assert.True(log.Record(Warned("Open 2 doors", "Otwórz drzwi")));
        Assert.Single(log.Recent);
        Assert.Equal(3, log.Total);
    }

    [Fact]
    public void Poprawka_oznacza_pozycje_tego_tekstu_i_zmienia_wersje()
    {
        var log = new QualityWarningLog();
        log.Record(Warned("Collect 3 keys", "Zbierz klucze"));
        log.Record(Warned("Wait 10 seconds", "Poczekaj chwilę"));
        var before = log.Version;

        Assert.Equal(1, log.MarkCorrected("Collect 3 keys", "Zbierz 3 klucze"));

        Assert.True(log.Version > before);
        var corrected = log.Recent.Single(static e => e.SourceText == "Collect 3 keys");
        Assert.True(corrected.IsCorrected);
        Assert.Equal("Zbierz 3 klucze", corrected.CorrectedText);
        Assert.Equal("Zbierz klucze", corrected.TranslatedText);
        Assert.False(log.Recent.Single(static e => e.SourceText == "Wait 10 seconds").IsCorrected);
        Assert.Equal(0, log.MarkCorrected("Unknown text", "Coś"));
    }

    [Fact]
    public void Wersja_rosnie_tylko_przy_zmianie_listy()
    {
        var log = new QualityWarningLog();
        var outcome = Warned("Level 20 door", "Drzwi poziomu");

        log.Record(outcome);
        var afterFirst = log.Version;
        log.Record(outcome);
        var afterRepeat = log.Version;
        log.Clear();
        var afterClear = log.Version;
        log.Clear();

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, afterRepeat);
        Assert.Equal(2, afterClear);
        Assert.Equal(afterClear, log.Version);
    }

    [Fact]
    public void Pozycja_zachowuje_klucz_poprawki_korpusu()
    {
        var log = new QualityWarningLog();
        var outcome = Warned("[X] Unlock 2 locks", "[X] Otwórz zamki") with { CacheKey = "Unlock 2 locks" };

        log.Record(outcome);

        Assert.Equal("Unlock 2 locks", Assert.Single(log.Recent).CacheKey);
    }

    [Fact]
    public async Task Rownolegle_odczyty_z_wielu_watkow_licza_kazda_pare_raz()
    {
        var log = new QualityWarningLog();
        var outcomes = Enumerable.Range(1, 40).Select(i => Warned($"Room {i} of 40", "Pokój")).ToList();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            foreach (var outcome in outcomes) log.Record(outcome);
        })));

        Assert.Equal(40, log.Total);
        Assert.Equal(QualityWarningLog.Capacity, log.Recent.Count);
        Assert.Equal(Enumerable.Range(21, 20).Select(static i => (long)i).Reverse(), log.Recent.Select(static e => e.Id));
    }
}
