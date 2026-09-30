using GameTranslatorOverlay.Core.Evaluation;

namespace GameTranslatorOverlay.Core.Tests;

public class EvaluationChrFTests
{
    [Fact]
    public void Identyczne_teksty_daja_100()
    {
        Assert.Equal(100, ChrF.Score("Musisz odnaleźć stary Kamień Drogi.", "Musisz odnaleźć stary Kamień Drogi."), precision: 9);
    }

    [Fact]
    public void Pusta_hipoteza_daje_0()
    {
        Assert.Equal(0, ChrF.Score("", "Nowa gra"));
        Assert.Equal(0, ChrF.Score(null, "Nowa gra"));
    }

    [Fact]
    public void Brak_wspolnych_znakow_daje_0()
    {
        Assert.Equal(0, ChrF.Score("xyz", "abc"));
    }

    [Fact]
    public void Biale_znaki_i_podzialy_wierszy_nie_wplywaja_na_wynik()
    {
        // Jak sacreBLEU: whitespace jest usuwany przed liczeniem n-gramów.
        Assert.Equal(100, ChrF.Score("Musisz odnaleźć\nstary Kamień", "Musisz odnaleźć stary  Kamień"), precision: 9);
    }

    [Fact]
    public void Znany_przyklad_rownej_dlugosci_policzony_recznie()
    {
        // hyp "abc", ref "abd":
        //   1-gramy: hyp {a,b,c}, ref {a,b,d}, wspólne 2 → P = R = 2/3
        //   2-gramy: hyp {ab,bc}, ref {ab,bd}, wspólne 1 → P = R = 1/2
        //   3-gramy: hyp {abc}, ref {abd}, wspólne 0 → P = R = 0
        //   4..6-gramy: brak w obu → pominięte (rząd efektywny = 3)
        // średnie P = R = (2/3 + 1/2 + 0) / 3 = 7/18; przy P = R wynik F-beta = P → 38,888…
        Assert.Equal(100.0 * 7 / 18, ChrF.Score("abc", "abd"), precision: 9);
    }

    [Fact]
    public void Znany_przyklad_krotszej_hipotezy_policzony_recznie()
    {
        // hyp "ab", ref "abc":
        //   1-gramy: hyp 2, ref 3, wspólne 2 → P = 1, R = 2/3
        //   2-gramy: hyp {ab}, ref {ab,bc}, wspólne 1 → P = 1, R = 1/2
        //   3-gramy: hipoteza ich nie ma → pominięte
        // P = 1, R = (2/3 + 1/2) / 2 = 7/12
        // F2 = (1 + 4)·P·R / (4·P + R) = 5·(7/12) / (4 + 7/12) = 35/55 = 7/11 → 63,636…
        Assert.Equal(100.0 * 7 / 11, ChrF.Score("ab", "abc"), precision: 9);
    }

    [Fact]
    public void Pelnosc_wazy_wiecej_niz_precyzja_przy_beta_2()
    {
        // Hipoteza krótsza (gubi tekst) ma niższy wynik niż dłuższa o ten sam nadmiar.
        var missing = ChrF.Score("ab", "abc");
        var extra = ChrF.Score("abc", "ab");
        Assert.True(missing < extra, $"{missing} powinno być < {extra}");
    }

    [Fact]
    public void Wynik_korpusu_sumuje_statystyki_zamiast_usredniac_linie()
    {
        // Sumy z dwóch przykładów powyżej:
        //   1-gramy: hyp 3+2 = 5, ref 3+3 = 6, wspólne 2+2 = 4 → P = 4/5, R = 4/6
        //   2-gramy: hyp 2+1 = 3, ref 2+2 = 4, wspólne 1+1 = 2 → P = 2/3, R = 2/4
        //   3-gramy: hyp 1+0 = 1, ref 1+1 = 2, wspólne 0 → P = 0, R = 0
        // P = (4/5 + 2/3 + 0)/3 = 22/45, R = (2/3 + 1/2 + 0)/3 = 7/18
        // F2 = 5·P·R / (4·P + R)
        const double p = 22.0 / 45, r = 7.0 / 18;
        var expected = 100 * 5 * p * r / (4 * p + r);

        var corpus = ChrF.CorpusScore([("abc", "abd"), ("ab", "abc")]);

        Assert.Equal(expected, corpus, precision: 9);
        var meanOfLines = (ChrF.Score("abc", "abd") + ChrF.Score("ab", "abc")) / 2;
        Assert.NotEqual(meanOfLines, corpus, precision: 3);
    }

    [Fact]
    public void Korpus_z_nieprzetlumaczona_linia_jest_gorszy()
    {
        var full = ChrF.CorpusScore([("Nowa gra", "Nowa gra"), ("Kontynuuj", "Kontynuuj")]);
        var withMissing = ChrF.CorpusScore([("Nowa gra", "Nowa gra"), (null, "Kontynuuj")]);

        Assert.Equal(100, full, precision: 9);
        Assert.True(withMissing < full);
    }

    [Fact]
    public void Odmiana_polskiego_slowa_jest_czesciowo_nagradzana()
    {
        var inflected = ChrF.Score("do maksymalnej Tarczy Energii", "do maksymalnej Tarczy Energii");
        var otherCase = ChrF.Score("do maksymalnej Tarcza Energii", "do maksymalnej Tarczy Energii");
        var unrelated = ChrF.Score("do maksymalnej osłony", "do maksymalnej Tarczy Energii");

        Assert.True(otherCase < inflected);
        Assert.True(otherCase > unrelated);
    }

    [Fact]
    public void Rozlozone_polskie_znaki_sa_normalizowane_do_NFC()
    {
        var decomposed = "Kamień Drogi"; // „ń” jako „n” + znak łączący
        Assert.Equal(100, ChrF.Score(decomposed, "Kamień Drogi"), precision: 9);
    }

    [Fact]
    public void Niepoprawne_parametry_sa_odrzucane()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChrF.Score("a", "a", charOrder: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChrF.Score("a", "a", beta: -1));
    }
}
