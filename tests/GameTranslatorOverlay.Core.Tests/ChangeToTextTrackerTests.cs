using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class ChangeToTextTrackerTests
{
    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public void Liczy_sie_pierwsza_probka_zmiany_a_nie_kolejne_klatki_animacji()
    {
        var tracker = new ChangeToTextTracker();

        tracker.ObserveChange(Ms(100));
        tracker.ObserveChange(Ms(267));
        tracker.ObserveChange(Ms(434));

        Assert.Equal(Ms(100), tracker.PendingSince);
        var origin = tracker.BeginProcessing();
        Assert.Equal(Ms(100), origin);
        Assert.Equal(1150, ChangeToTextTracker.Measure(origin, Ms(1250)));
    }

    [Fact]
    public void Przetworzenie_zabiera_zmiane_a_zmiana_w_trakcie_zaczyna_nowy_pomiar()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(100));

        var first = tracker.BeginProcessing();
        Assert.Null(tracker.PendingSince);
        tracker.ObserveChange(Ms(600));

        Assert.Equal(Ms(100), first);
        Assert.Equal(Ms(600), tracker.BeginProcessing());
    }

    [Fact]
    public void Przebieg_bez_zmiany_niczego_nie_mierzy()
    {
        var tracker = new ChangeToTextTracker();

        var origin = tracker.BeginProcessing();

        Assert.Null(origin);
        Assert.Null(ChangeToTextTracker.Measure(origin, Ms(5000)));
    }

    [Fact]
    public void Powtorka_bez_napisu_mierzy_od_pierwotnej_zmiany()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(100));
        var origin = tracker.BeginProcessing();
        // W trakcie OCR zauważono dalszy ciąg tej samej zmiany.
        tracker.ObserveChange(Ms(400));

        tracker.CarryOver(origin);

        Assert.Equal(Ms(100), tracker.BeginProcessing());
    }

    [Fact]
    public void Przeniesienie_nie_przesuwa_wczesniejszej_zmiany_ani_pustego_poczatku()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(50));

        tracker.CarryOver(Ms(100));
        tracker.CarryOver(null);

        Assert.Equal(Ms(50), tracker.PendingSince);
    }

    [Fact]
    public void Reset_zapomina_zmiane()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(50));

        tracker.Reset();

        Assert.Null(tracker.BeginProcessing());
    }

    [Fact]
    public void Czas_sprzed_zmiany_nie_daje_ujemnego_pomiaru()
    {
        Assert.Null(ChangeToTextTracker.Measure(Ms(500), Ms(400)));
        Assert.Equal(0, ChangeToTextTracker.Measure(Ms(500), Ms(500)));
    }

    [Fact]
    public void Ukonczona_klatka_bez_napisu_nie_zawyza_pomiaru_nowej_linii_z_jej_trakcie()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(0));
        var origin = tracker.BeginProcessing();
        // W trakcie OCR (który nic nie znalazł) pojawia się nowa linia.
        tracker.ObserveChange(Ms(500));

        tracker.FinishProcessing(origin, frameCompleted: true, rereadRequested: false);

        Assert.Equal(Ms(500), tracker.PendingSince);
    }

    [Fact]
    public void Porzucona_klatka_przenosi_pierwotny_poczatek()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(0));
        var origin = tracker.BeginProcessing();
        tracker.ObserveChange(Ms(500));

        tracker.FinishProcessing(origin, frameCompleted: false, rereadRequested: false);

        Assert.Equal(Ms(0), tracker.PendingSince);
    }

    [Fact]
    public void Celowa_powtorka_odczytu_przenosi_pierwotny_poczatek_nawet_po_ukonczonej_klatce()
    {
        var tracker = new ChangeToTextTracker();
        tracker.ObserveChange(Ms(100));
        var origin = tracker.BeginProcessing();

        tracker.FinishProcessing(origin, frameCompleted: true, rereadRequested: true);

        Assert.Equal(Ms(100), tracker.PendingSince);
    }
}
