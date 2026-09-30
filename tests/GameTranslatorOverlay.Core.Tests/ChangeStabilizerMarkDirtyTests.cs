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
    public void Limit_ciaglych_zmian_liczy_sie_od_zaobserwowanej_zmiany()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(500), Ms(2000));

        // Tekst czeka od 500 ms, a obraz dalej się zmienia — limit 600 ms już minął,
        // więc animowane tło nie odracza przetworzenia o kolejne 600 ms.
        Assert.True(stabilizer.Update(frameChanged: true, Ms(2010)));
    }

    [Fact]
    public void Swieza_zmiana_nie_wymusza_przetworzenia_przed_limitem()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(Ms(1900), Ms(2000));

        Assert.False(stabilizer.Update(frameChanged: true, Ms(2100)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(2499)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(2500)));
    }

    [Fact]
    public void Juz_brudny_stabilizator_zachowuje_wczesniejszy_poczatek_brudu()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(0)));

        stabilizer.MarkDirty(Ms(300), Ms(400));

        // Limit 600 ms liczy się nadal od 0 ms — MarkDirty go nie odnawia.
        Assert.False(stabilizer.Update(frameChanged: true, Ms(599)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(600)));
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
    public void Wczesniejsza_zmiana_moze_przyspieszyc_limit_juz_brudnego_stabilizatora()
    {
        var stabilizer = Create();
        stabilizer.ForceDirty(Ms(1000));

        stabilizer.MarkDirty(Ms(500), Ms(1000));

        // Początek brudu to najwcześniejsza znana zmiana (500 ms), więc limit 600 ms
        // mija przy 1100 ms, a termin stabilności z ForceDirty (1250 ms) zostaje.
        Assert.False(stabilizer.Update(frameChanged: false, Ms(1100)));
        Assert.True(stabilizer.Update(frameChanged: true, Ms(1100)));
    }
}
