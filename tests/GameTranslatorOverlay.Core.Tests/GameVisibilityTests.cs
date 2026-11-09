using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Tests;

public class GameVisibilityTests
{
    private const int GamePid = 100;
    private const int OtherPid = 200;

    private static readonly RectPx GameOnFirstMonitor = new(0, 0, 1920, 1080);

    private static GameVisibilityState Evaluate(RectPx game, int foregroundPid, RectPx foregroundBounds) =>
        GameVisibility.Evaluate(new WindowPlacement(GamePid, game), new WindowPlacement(foregroundPid, foregroundBounds));

    [Fact]
    public void Okno_pierwszoplanowe_procesu_gry_to_gra_widoczna()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, GamePid, new RectPx(0, 0, 1920, 1080)));
    }

    [Fact]
    public void Okno_procesu_gry_poza_oknem_gry_nadal_oznacza_widoczna()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, GamePid, new RectPx(5000, 0, 800, 600)));
    }

    [Fact]
    public void Brak_okna_pierwszoplanowego_to_gra_widoczna()
    {
        Assert.Equal(GameVisibilityState.Visible,
            GameVisibility.Evaluate(new WindowPlacement(GamePid, GameOnFirstMonitor), null));
    }

    [Fact]
    public void Przegladarka_na_pelnym_ekranie_zaslania_gre()
    {
        Assert.Equal(GameVisibilityState.Covered,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(0, 0, 1920, 1080)));
    }

    [Fact]
    public void Zmaksymalizowane_okno_z_niewidzialna_ramka_zaslania_gre()
    {
        Assert.Equal(GameVisibilityState.Covered,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(-8, -8, 1936, 1096)));
    }

    [Fact]
    public void Okno_tlumacza_nad_gra_tez_ja_zaslania()
    {
        Assert.Equal(GameVisibilityState.Covered,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(300, 200, 1000, 700)));
    }

    [Fact]
    public void Pasek_zadan_u_dolu_nie_zaslania_gry()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(0, 1032, 1920, 48)));
    }

    [Fact]
    public void Pasek_zadan_przy_oknie_gry_na_calym_monitorze_4K_nie_zaslania_gry()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(new RectPx(0, 0, 3840, 2160), OtherPid, new RectPx(0, 2088, 3840, 72)));
    }

    [Fact]
    public void Drugi_monitor_nie_zaslania_gry_nawet_na_calym_ekranie()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(1920, 0, 1920, 1080)));
    }

    [Fact]
    public void Drugi_monitor_po_lewej_ze_ujemnymi_wspolrzednymi_nie_zaslania_gry()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(-1920, 0, 1920, 1080)));
    }

    [Theory]
    [InlineData(1920, 1080, 0.14, GameVisibilityState.Visible)]
    [InlineData(1920, 1080, 0.15, GameVisibilityState.Covered)]
    [InlineData(1920, 1080, 0.16, GameVisibilityState.Covered)]
    [InlineData(1920, 1080, 0.0, GameVisibilityState.Visible)]
    public void Granica_pokrycia_to_pietnascie_procent_powierzchni_gry(int width, int height, double fraction, GameVisibilityState expected)
    {
        var strip = (int)Math.Round(width * (double)height * fraction / width);
        var foreground = strip <= 0 ? new RectPx(0, 0, 0, 0) : new RectPx(0, 0, width, strip);

        Assert.Equal(expected, Evaluate(new RectPx(0, 0, width, height), OtherPid, foreground));
    }

    [Fact]
    public void Okno_zaslaniajace_tylko_rog_gry_ponizej_progu_nie_zaslania()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(1500, 800, 420, 280)));
    }

    [Fact]
    public void Okno_zaslaniajace_polowe_gry_zaslania()
    {
        Assert.Equal(GameVisibilityState.Covered,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(960, 0, 960, 1080)));
    }

    [Fact]
    public void Puste_prostokaty_nie_zaslaniaja()
    {
        Assert.Equal(GameVisibilityState.Visible, Evaluate(default, OtherPid, new RectPx(0, 0, 100, 100)));
        Assert.Equal(GameVisibilityState.Visible, Evaluate(GameOnFirstMonitor, OtherPid, default));
    }

    [Fact]
    public void Zminimalizowane_okno_z_ujemnymi_wspolrzednymi_nie_zaslania()
    {
        Assert.Equal(GameVisibilityState.Visible,
            Evaluate(GameOnFirstMonitor, OtherPid, new RectPx(-32000, -32000, 160, 28)));
    }
}

public class GamePauseGateTests
{
    [Fact]
    public void Pauza_dopiero_po_dwoch_kolejnych_cyklach_zaslonieca()
    {
        var gate = new GamePauseGate();

        Assert.Equal(GamePauseTransition.None, gate.Observe(GameVisibilityState.Covered));
        Assert.False(gate.IsPaused);
        Assert.Equal(GamePauseTransition.Paused, gate.Observe(GameVisibilityState.Covered));
        Assert.True(gate.IsPaused);
    }

    [Fact]
    public void Przerwany_stan_zaslonieta_zaczyna_liczenie_od_nowa()
    {
        var gate = new GamePauseGate();

        gate.Observe(GameVisibilityState.Covered);
        gate.Observe(GameVisibilityState.Visible);

        Assert.Equal(GamePauseTransition.None, gate.Observe(GameVisibilityState.Covered));
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void Wznowienie_jest_natychmiastowe_po_pierwszym_cyklu_z_widoczna_gra()
    {
        var gate = new GamePauseGate();
        gate.Observe(GameVisibilityState.Covered);
        gate.Observe(GameVisibilityState.Covered);

        Assert.Equal(GamePauseTransition.Resumed, gate.Observe(GameVisibilityState.Visible));
        Assert.False(gate.IsPaused);
        Assert.Equal(GamePauseTransition.None, gate.Observe(GameVisibilityState.Visible));
    }

    [Fact]
    public void Pauza_zglasza_sie_raz_mimo_dalszego_zaslaniania()
    {
        var gate = new GamePauseGate();
        var transitions = Enumerable.Range(0, 10).Select(_ => gate.Observe(GameVisibilityState.Covered)).ToList();

        Assert.Single(transitions, GamePauseTransition.Paused);
    }

    [Fact]
    public void Po_wznowieniu_kolejne_zaslonienie_znow_wymaga_dwoch_cykli()
    {
        var gate = new GamePauseGate();
        gate.Observe(GameVisibilityState.Covered);
        gate.Observe(GameVisibilityState.Covered);
        gate.Observe(GameVisibilityState.Visible);

        Assert.Equal(GamePauseTransition.None, gate.Observe(GameVisibilityState.Covered));
        Assert.Equal(GamePauseTransition.Paused, gate.Observe(GameVisibilityState.Covered));
    }
}
