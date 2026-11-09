using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class EarlyReadGateTests
{
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public void Bez_zmiany_obrazu_wczesny_odczyt_nie_jest_nalezny()
    {
        var gate = new EarlyReadGate(Ms(80));

        gate.Observe(frameChanged: false, Ms(0));

        Assert.False(gate.IsArmed);
        Assert.False(gate.IsDue(Ms(500)));
    }

    [Fact]
    public void Po_zmianie_odczyt_jest_nalezny_dopiero_po_opoznieniu()
    {
        var gate = new EarlyReadGate(Ms(80));
        gate.Observe(frameChanged: true, Ms(100));

        Assert.False(gate.IsDue(Ms(179)));
        Assert.True(gate.IsDue(Ms(180)));
    }

    [Fact]
    public void Kolejna_zmiana_przesuwa_termin()
    {
        var gate = new EarlyReadGate(Ms(80));
        gate.Observe(frameChanged: true, Ms(100));
        gate.Observe(frameChanged: true, Ms(160));

        Assert.False(gate.IsDue(Ms(200)));
        Assert.True(gate.IsDue(Ms(240)));
    }

    [Fact]
    public void Proba_jest_jedna_na_serie_zmian()
    {
        var gate = new EarlyReadGate(Ms(80));
        gate.Observe(frameChanged: true, Ms(100));
        Assert.True(gate.IsDue(Ms(200)));

        gate.Clear();

        Assert.False(gate.IsDue(Ms(400)));
        gate.Observe(frameChanged: true, Ms(500));
        Assert.True(gate.IsDue(Ms(580)));
    }

    [Fact]
    public void Zerowe_opoznienie_wylacza_bramke()
    {
        var gate = new EarlyReadGate(TimeSpan.Zero);

        gate.Observe(frameChanged: true, Ms(100));

        Assert.False(gate.Enabled);
        Assert.False(gate.IsDue(Ms(900)));
        Assert.Equal(Ms(120), gate.GetPollingDelay(Ms(100), Ms(120)));
    }

    [Fact]
    public void Budzik_skraca_czekanie_do_terminu_wczesnego_odczytu()
    {
        var gate = new EarlyReadGate(Ms(80));
        gate.Observe(frameChanged: true, Ms(100));

        Assert.Equal(Ms(40), gate.GetPollingDelay(Ms(140), Ms(127)));
    }

    [Fact]
    public void Budzik_nie_wydluza_zaplanowanego_czekania_i_nie_kreci_petli_po_terminie()
    {
        var gate = new EarlyReadGate(Ms(80));
        gate.Observe(frameChanged: true, Ms(100));

        Assert.Equal(Ms(20), gate.GetPollingDelay(Ms(140), Ms(20)));
        Assert.Equal(Ms(127), gate.GetPollingDelay(Ms(180), Ms(127)));
        Assert.Equal(Ms(127), gate.GetPollingDelay(Ms(300), Ms(127)));
        Assert.Equal(TimeSpan.Zero, gate.GetPollingDelay(Ms(140), TimeSpan.Zero));
    }

    [Fact]
    public void Bez_uzbrojenia_budzik_zostawia_plan()
    {
        var gate = new EarlyReadGate(Ms(80));

        Assert.Equal(Ms(127), gate.GetPollingDelay(Ms(140), Ms(127)));
    }
}
