using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// MarkDirty: zmiana zauważona w trakcie OCR/tłumaczenia liczy okno stabilności od chwili
/// zmiany, nie od końca oczekiwania (w przeciwieństwie do ForceDirty dla celowych powtórek).
/// </summary>
public class ChangeStabilizerMarkDirtyTests
{
    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
    private static ChangeStabilizer Create() => new(Ms(250), Ms(600));

    [Fact]
    public void Zmiana_sprzed_dlugiego_oczekiwania_jest_gotowa_od_razu_po_jego_koncu()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(lastChangeAt: Ms(500), now: Ms(2000));

        Assert.True(stabilizer.IsDirty);
        Assert.True(stabilizer.Update(frameChanged: false, Ms(2000)));
        Assert.False(stabilizer.IsDirty);
    }

    [Fact]
    public void ForceDirty_dla_porownania_nadal_czeka_pelne_okno_od_konca_oczekiwania()
    {
        var stabilizer = Create();

        stabilizer.ForceDirty(Ms(2000));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(2000)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(2249)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(2250)));
    }

    [Fact]
    public void Swieza_zmiana_czeka_tylko_na_reszte_okna_stabilnosci()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(1900), Ms(2000));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(2000)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(2150) - TimeSpan.FromTicks(1)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(2150)));
    }

    [Fact]
    public void Kolejna_zmiana_po_MarkDirty_przesuwa_termin_jak_zwykle()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(1900), Ms(2000));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2100)));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(2349)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(2350)));
    }

    [Fact]
    public void Czas_zmiany_z_przyszlosci_jest_przycinany_do_teraz()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(3000), Ms(2000));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(2249)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(2250)));
    }

    [Fact]
    public void Harmonogram_budzi_na_termin_liczony_od_zmiany()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(1900), Ms(2000));

        // 1900 + 250 = 2150, czyli 150 ms od teraz — zamiast pełnych 250 ms po ForceDirty.
        Assert.Equal(Ms(150), stabilizer.GetPollingDelay(Ms(2000), Ms(167), Ms(167)));
    }

    [Fact]
    public void Termin_juz_miniony_zostawia_zwykly_odstep_i_nastepna_klatka_go_konsumuje()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(500), Ms(2000));

        // Przeterminowany termin nie może rozkręcić pętli do zera (jak po nieudanym capture).
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(2000), Ms(167), Ms(167)));
        Assert.Equal(TimeSpan.Zero, stabilizer.GetPollingDelay(Ms(2000), TimeSpan.Zero, Ms(167)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(2167)));
    }

    [Fact]
    public void Stara_zmiana_i_cisza_nie_wymuszaja_OCR_klatki_ktora_dalej_sie_rysuje()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(500), Ms(2000));

        // Kontrola sceny widziała zmianę przy 500 ms, a potem obraz stał — to nie jest
        // ciągła animacja. Klatka, która znowu się zmienia (pisany napis, pojawiający się
        // dymek), czeka na stabilność albo na pełny limit liczony od końca oczekiwania.
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2010)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2599)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(2600)));
    }

    [Fact]
    public void Stara_zmiana_bez_kolejnych_jest_gotowa_przy_najblizszej_spokojnej_probce()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(500), Ms(2000));

        Assert.True(stabilizer.Update(frameChanged: false, Ms(2010)));
    }

    [Fact]
    public void Swieza_zmiana_nie_wymusza_przetworzenia_przed_limitem()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(1900), Ms(2000));

        // Limit ciągłych zmian liczy się od końca oczekiwania (2000 ms), jak po ForceDirty.
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2100)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2599)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(2600)));
    }

    [Fact]
    public void Juz_brudny_stabilizator_odnawia_limit_jak_ForceDirty()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(0)));
        // Wymuszony przebieg (limit ciągłych zmian) zostawia stabilizator brudnym.
        Assert.True(stabilizer.Update(frameChanged: true, Ms(600)));

        // Długie OCR/tłumaczenie: koniec przy 2000 ms, zmiana zauważona przy 900 ms.
        stabilizer.MarkDirty(Ms(900), Ms(2000));

        // Początek brudu sprzed oczekiwania nie wymusza OCR pierwszej zmienionej klatki —
        // limit liczy się od nowa, tak jak po ForceDirty przed tą zmianą.
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2010)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2599)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(2600)));
    }

    [Fact]
    public void Starsza_zmiana_nie_cofa_terminu_stabilnosci_juz_brudnego_stabilizatora()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(1000)));

        stabilizer.MarkDirty(Ms(900), Ms(1100));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1249)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1250)));
    }

    [Fact]
    public void Pozniejsza_zmiana_przesuwa_termin_juz_brudnego_stabilizatora()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(1000)));

        stabilizer.MarkDirty(Ms(1200), Ms(1300));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1300)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(1449)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1450)));
    }

    [Fact]
    public void Wczesniejsza_zmiana_nie_przyspiesza_limitu_juz_brudnego_stabilizatora()
    {
        var stabilizer = Create();
        stabilizer.ForceDirty(Ms(1000));

        stabilizer.MarkDirty(Ms(500), Ms(1000));

        // Termin stabilności z ForceDirty (1250 ms) zostaje, limit 600 ms liczy się od 1000 ms.
        Assert.False(stabilizer.Update(frameChanged: true, Ms(1100)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(1599)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(1600)));
    }
}
