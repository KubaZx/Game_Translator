using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class ChangeStabilizerQuickWindowTests
{
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    private static ChangeStabilizer Create() => new(Ms(250), quickDelay: Ms(160));

    [Fact]
    public void Pojedyncza_zmiana_po_spokoju_jest_stabilna_po_krotszym_oknie()
    {
        var stabilizer = Create();

        Assert.False(stabilizer.Update(frameChanged: true, Ms(1000)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(1159)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1160)));
        Assert.False(stabilizer.IsDirty);
    }

    [Fact]
    public void Druga_zmiana_w_serii_przywraca_pelne_okno()
    {
        var stabilizer = Create();
        stabilizer.Update(frameChanged: true, Ms(1000));
        stabilizer.Update(frameChanged: true, Ms(1080));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1080 + 160)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(1080 + 249)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1080 + 250)));
    }

    [Fact]
    public void Zmiana_tuz_po_poprzedniej_czeka_pelne_okno()
    {
        var stabilizer = Create();
        stabilizer.Update(frameChanged: true, Ms(1000));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1160)));

        stabilizer.Update(frameChanged: true, Ms(1400));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1400 + 160)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1400 + 250)));
    }

    [Fact]
    public void Po_dluzszym_spokoju_krotsze_okno_wraca()
    {
        var stabilizer = Create();
        stabilizer.Update(frameChanged: true, Ms(1000));
        stabilizer.Update(frameChanged: true, Ms(1100));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1350)));

        stabilizer.Update(frameChanged: true, Ms(1100 + 600));

        Assert.True(stabilizer.Update(frameChanged: false, Ms(1700 + 160)));
    }

    [Fact]
    public void Wymuszone_czytanie_zawsze_czeka_pelne_okno()
    {
        var stabilizer = Create();

        stabilizer.ForceDirty(Ms(1000));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1160)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1250)));
    }

    [Fact]
    public void Zmiana_po_wymuszonym_czytaniu_nie_jest_pojedynczym_skokiem()
    {
        var stabilizer = Create();
        stabilizer.ForceDirty(Ms(1000));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1250)));

        stabilizer.Update(frameChanged: true, Ms(1300));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1460)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1550)));
    }

    [Fact]
    public void Zmiana_zgloszona_wstecz_czeka_pelne_okno_od_chwili_zmiany()
    {
        var stabilizer = Create();

        stabilizer.MarkDirty(lastChangeAt: Ms(1000), now: Ms(1100));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1160)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1250)));
    }

    [Fact]
    public void Budzik_celuje_w_koniec_krotszego_okna()
    {
        var stabilizer = Create();
        stabilizer.Update(frameChanged: true, Ms(1000));

        Assert.Equal(Ms(120), stabilizer.GetPollingDelay(Ms(1040), Ms(127), Ms(167)));
    }

    [Fact]
    public void Budzik_po_drugiej_zmianie_celuje_w_pelne_okno()
    {
        var stabilizer = Create();
        stabilizer.Update(frameChanged: true, Ms(1000));
        stabilizer.Update(frameChanged: true, Ms(1080));

        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(1090), Ms(167), Ms(167)));
        Assert.Equal(Ms(40), stabilizer.GetPollingDelay(Ms(1290), Ms(127), Ms(167)));
    }

    [Fact]
    public void Bez_krotszego_okna_zachowanie_jest_jak_dotad()
    {
        var stabilizer = new ChangeStabilizer(Ms(250));
        stabilizer.Update(frameChanged: true, Ms(1000));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1160)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1250)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(400)]
    public void Krotsze_okno_poza_zakresem_jest_wylaczone(int quickMs)
    {
        var stabilizer = new ChangeStabilizer(Ms(250), quickDelay: Ms(quickMs));
        stabilizer.Update(frameChanged: true, Ms(1000));

        Assert.False(stabilizer.Update(frameChanged: false, Ms(1249)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(1250)));
    }
}
