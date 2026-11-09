using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class CaptureRouteTests
{
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private sealed class Screen(bool uniform)
    {
        public int Looks { get; private set; }

        public bool Uniform { get; set; } = uniform;

        public Func<bool> Probe => () =>
        {
            Looks++;
            return Uniform;
        };
    }

    private static CaptureVerdict Frame(CaptureRoute route, PrintOutcome outcome, Screen screen, int at, bool visible = true) =>
        route.Observe(outcome, visible, Ms(at), screen.Probe);

    private static CaptureRoute ProvenRoute(Screen screen)
    {
        var route = new CaptureRoute();
        Frame(route, PrintOutcome.Content, screen, 0);
        return route;
    }

    private static CaptureRoute BrokenRoute(Screen screen, out int enteredAt)
    {
        var route = new CaptureRoute();
        for (var i = 0; i < CaptureRoute.ConfirmationFrames; i++) Frame(route, PrintOutcome.Failed, screen, i * 170);
        enteredAt = (CaptureRoute.ConfirmationFrames - 1) * 170;
        Assert.True(route.NoPrintWindow);
        return route;
    }

    [Fact]
    public void Czarny_kadr_po_zwyklych_klatkach_to_obraz_gry_bez_zrzutu_ekranu()
    {
        var screen = new Screen(uniform: false);
        var route = ProvenRoute(screen);

        for (var i = 1; i <= 40; i++)
        {
            var verdict = Frame(route, PrintOutcome.Uniform, screen, i * 170);
            Assert.Equal(CaptureFrameChoice.Printed, verdict.Choice);
            Assert.False(verdict.EnteredNoPrintWindow);
        }

        Assert.Equal(0, screen.Looks);
        Assert.False(route.NoPrintWindow);
        Assert.False(route.ReadsScreen(Ms(7000)));
    }

    [Fact]
    public void Gra_bez_PrintWindow_od_poczatku_potrzebuje_trzech_kolejnych_klatek()
    {
        var screen = new Screen(uniform: false);
        var route = new CaptureRoute();

        var first = Frame(route, PrintOutcome.Failed, screen, 0);
        var second = Frame(route, PrintOutcome.Uniform, screen, 170);
        var third = Frame(route, PrintOutcome.Failed, screen, 340);

        Assert.Equal(CaptureFrameChoice.Skip, first.Choice);
        Assert.Equal(CaptureFrameChoice.Printed, second.Choice);
        Assert.Equal(CaptureFrameChoice.Fallback, third.Choice);
        Assert.True(third.EnteredNoPrintWindow);
        Assert.True(route.NoPrintWindow);
        Assert.Equal(3, screen.Looks);
    }

    [Fact]
    public void Jednolity_wynik_PrintWindow_od_poczatku_tez_liczy_sie_do_serii()
    {
        var screen = new Screen(uniform: false);
        var route = new CaptureRoute();

        Frame(route, PrintOutcome.Uniform, screen, 0);
        Frame(route, PrintOutcome.Uniform, screen, 170);
        var third = Frame(route, PrintOutcome.Uniform, screen, 340);

        Assert.True(third.EnteredNoPrintWindow);
    }

    [Fact]
    public void Czarny_ekran_gry_na_starcie_sesji_nie_jest_oknem_bez_PrintWindow()
    {
        var screen = new Screen(uniform: true);
        var route = new CaptureRoute();

        for (var i = 0; i < 30; i++)
        {
            var verdict = Frame(route, PrintOutcome.Uniform, screen, i * 170);
            Assert.Equal(CaptureFrameChoice.Printed, verdict.Choice);
        }

        Assert.False(route.NoPrintWindow);
        Assert.False(route.PrintWindowProven);
        Assert.Equal(30, screen.Looks);
    }

    [Fact]
    public void Czarny_ekran_gry_i_nieudany_PrintWindow_pomija_klatke_bez_zmiany_zrodla()
    {
        var screen = new Screen(uniform: true);
        var route = new CaptureRoute();

        for (var i = 0; i < 10; i++)
            Assert.Equal(CaptureFrameChoice.Skip, Frame(route, PrintOutcome.Failed, screen, i * 170).Choice);

        Assert.False(route.NoPrintWindow);
    }

    [Fact]
    public void Jednolity_zrzut_porownawczy_przerywa_serie()
    {
        var screen = new Screen(uniform: false);
        var route = new CaptureRoute();

        Frame(route, PrintOutcome.Failed, screen, 0);
        Frame(route, PrintOutcome.Failed, screen, 170);
        screen.Uniform = true;
        Frame(route, PrintOutcome.Failed, screen, 340);
        screen.Uniform = false;
        Frame(route, PrintOutcome.Failed, screen, 510);
        var last = Frame(route, PrintOutcome.Failed, screen, 680);

        Assert.False(last.EnteredNoPrintWindow);
        Assert.False(route.NoPrintWindow);
    }

    [Fact]
    public void Niejednolita_klatka_PrintWindow_przerywa_serie_i_zapamietuje_dzialanie()
    {
        var screen = new Screen(uniform: false);
        var route = new CaptureRoute();

        Frame(route, PrintOutcome.Failed, screen, 0);
        Frame(route, PrintOutcome.Failed, screen, 170);
        Frame(route, PrintOutcome.Content, screen, 340);
        Frame(route, PrintOutcome.Failed, screen, 510);
        Frame(route, PrintOutcome.Failed, screen, 680);

        Assert.True(route.PrintWindowProven);
        Assert.False(route.NoPrintWindow);
    }

    [Fact]
    public void Zaslonieta_gra_nie_robi_zrzutu_porownawczego()
    {
        var screen = new Screen(uniform: false);
        var route = new CaptureRoute();

        for (var i = 0; i < 20; i++)
            Frame(route, PrintOutcome.Failed, screen, i * 170, visible: false);

        Assert.Equal(0, screen.Looks);
        Assert.False(route.NoPrintWindow);
    }

    [Fact]
    public void Zaslonieta_gra_nie_przerywa_trwajacej_serii()
    {
        var screen = new Screen(uniform: false);
        var route = new CaptureRoute();

        Frame(route, PrintOutcome.Failed, screen, 0);
        Frame(route, PrintOutcome.Failed, screen, 170);
        Frame(route, PrintOutcome.Failed, screen, 340, visible: false);
        var third = Frame(route, PrintOutcome.Failed, screen, 510);

        Assert.True(third.EnteredNoPrintWindow);
    }

    [Fact]
    public void Blad_PrintWindow_po_zwyklych_klatkach_tez_wymaga_trzech_klatek_i_zrzutu()
    {
        var screen = new Screen(uniform: false);
        var route = ProvenRoute(screen);

        Frame(route, PrintOutcome.Failed, screen, 170);
        Frame(route, PrintOutcome.Failed, screen, 340);
        Assert.False(route.NoPrintWindow);
        var third = Frame(route, PrintOutcome.Failed, screen, 510);

        Assert.True(third.EnteredNoPrintWindow);
    }

    [Fact]
    public void W_trybie_bez_PrintWindow_proba_jest_co_dziesiec_sekund()
    {
        var route = BrokenRoute(new Screen(uniform: false), out var enteredAt);

        Assert.False(route.ShouldTryPrintWindow(Ms(enteredAt + 170)));
        Assert.False(route.ShouldTryPrintWindow(Ms(enteredAt) + CaptureRoute.ProbeInterval - Ms(1)));
        Assert.True(route.ShouldTryPrintWindow(Ms(enteredAt) + CaptureRoute.ProbeInterval));
    }

    [Fact]
    public void Nieudana_proba_odsuwa_nastepna_o_dziesiec_sekund_i_daje_zrodlo_zastepcze()
    {
        var screen = new Screen(uniform: false);
        var route = BrokenRoute(screen, out var enteredAt);
        var probeAt = Ms(enteredAt) + CaptureRoute.ProbeInterval;

        var verdict = route.Observe(PrintOutcome.Uniform, true, probeAt, screen.Probe);

        Assert.Equal(CaptureFrameChoice.Fallback, verdict.Choice);
        Assert.False(route.ShouldTryPrintWindow(probeAt + Ms(9999)));
        Assert.True(route.ShouldTryPrintWindow(probeAt + CaptureRoute.ProbeInterval));
    }

    [Fact]
    public void Dwie_kolejne_niejednolite_klatki_PrintWindow_przywracaja_tryb_zwykly()
    {
        var screen = new Screen(uniform: false);
        var route = BrokenRoute(screen, out var enteredAt);
        var probeAt = Ms(enteredAt) + CaptureRoute.ProbeInterval;

        var first = route.Observe(PrintOutcome.Content, true, probeAt, screen.Probe);
        Assert.Equal(CaptureFrameChoice.Printed, first.Choice);
        Assert.False(first.LeftNoPrintWindow);
        Assert.True(route.NoPrintWindow);
        Assert.True(route.ShouldTryPrintWindow(probeAt + Ms(170)));

        var second = route.Observe(PrintOutcome.Content, true, probeAt + Ms(170), screen.Probe);

        Assert.Equal(CaptureFrameChoice.Printed, second.Choice);
        Assert.True(second.LeftNoPrintWindow);
        Assert.False(route.NoPrintWindow);
        Assert.True(route.PrintWindowProven);
        Assert.True(route.ShouldTryPrintWindow(probeAt + Ms(340)));
    }

    [Fact]
    public void Jedna_dobra_klatka_miedzy_zlymi_nie_przywraca_trybu_zwyklego()
    {
        var screen = new Screen(uniform: false);
        var route = BrokenRoute(screen, out var enteredAt);
        var probeAt = Ms(enteredAt) + CaptureRoute.ProbeInterval;

        route.Observe(PrintOutcome.Content, true, probeAt, screen.Probe);
        route.Observe(PrintOutcome.Failed, true, probeAt + Ms(170), screen.Probe);

        Assert.True(route.NoPrintWindow);
        Assert.False(route.ShouldTryPrintWindow(probeAt + Ms(340)));
        Assert.True(route.ShouldTryPrintWindow(probeAt + Ms(170) + CaptureRoute.ProbeInterval));
    }

    [Fact]
    public void Po_powrocie_PrintWindow_czarny_kadr_znowu_jest_obrazem_gry()
    {
        var screen = new Screen(uniform: false);
        var route = BrokenRoute(screen, out var enteredAt);
        var probeAt = Ms(enteredAt) + CaptureRoute.ProbeInterval;
        route.Observe(PrintOutcome.Content, true, probeAt, screen.Probe);
        route.Observe(PrintOutcome.Content, true, probeAt + Ms(170), screen.Probe);
        var looksBefore = screen.Looks;

        var verdict = route.Observe(PrintOutcome.Uniform, true, probeAt + Ms(340), screen.Probe);

        Assert.Equal(CaptureFrameChoice.Printed, verdict.Choice);
        Assert.Equal(looksBefore, screen.Looks);
    }

    [Fact]
    public void System_bez_WGC_czyta_zrzut_ekranu_i_wskaznik_trwa_do_konca_trybu()
    {
        var screen = new Screen(uniform: false);
        var route = BrokenRoute(screen, out var enteredAt);

        Assert.False(route.ReadsScreen(Ms(enteredAt)));
        for (var at = enteredAt; at < enteredAt + 9000; at += 170) route.NoteScreenFrame(Ms(at));

        Assert.True(route.ReadsScreen(Ms(enteredAt + 9000)));
        Assert.True(route.ReadsScreen(Ms(enteredAt + 9000) + CaptureRoute.ScreenReadHold - Ms(200)));
        Assert.False(route.ReadsScreen(Ms(enteredAt + 9000) + CaptureRoute.ScreenReadHold + Ms(200)));
    }

    [Fact]
    public void System_z_WGC_nie_czyta_zrzutu_ekranu_mimo_trybu_bez_PrintWindow()
    {
        var route = BrokenRoute(new Screen(uniform: false), out var enteredAt);

        Assert.True(route.NoPrintWindow);
        Assert.False(route.ReadsScreen(Ms(enteredAt + 3000)));
    }

    [Fact]
    public void Wskaznik_zrzutu_znika_od_razu_po_powrocie_PrintWindow()
    {
        var screen = new Screen(uniform: false);
        var route = BrokenRoute(screen, out var enteredAt);
        var probeAt = Ms(enteredAt) + CaptureRoute.ProbeInterval;
        route.NoteScreenFrame(probeAt);
        Assert.True(route.ReadsScreen(probeAt));

        route.Observe(PrintOutcome.Content, true, probeAt, screen.Probe);
        route.Observe(PrintOutcome.Content, true, probeAt + Ms(170), screen.Probe);

        Assert.False(route.ReadsScreen(probeAt + Ms(170)));
    }

    [Fact]
    public void Wskaznik_zrzutu_nie_dziala_przed_potwierdzeniem()
    {
        var route = new CaptureRoute();
        route.NoteScreenFrame(Ms(0));

        Assert.False(route.ReadsScreen(Ms(10)));
    }
}
