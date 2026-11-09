using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class LiveStartConfirmationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(LiveTargetReason.RememberedProcess, false)]
    [InlineData(LiveTargetReason.ProfileProcess, false)]
    [InlineData(LiveTargetReason.Foreground, true)]
    public void Znana_gra_startuje_od_razu(LiveTargetReason reason, bool known)
    {
        var confirmation = new LiveStartConfirmation();
        Assert.Equal(LiveStartDecision.Start, confirmation.Evaluate(10, reason, known, T0));
    }

    [Fact]
    public void Nieznane_okno_wymaga_dwoch_wcisniec()
    {
        var confirmation = new LiveStartConfirmation();
        Assert.Equal(LiveStartDecision.AskForConfirmation, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0));
        Assert.Equal(LiveStartDecision.Start, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Drugie_wcisniecie_dokladnie_po_szesciu_sekundach_jeszcze_startuje()
    {
        var confirmation = new LiveStartConfirmation();
        confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0);
        Assert.Equal(LiveStartDecision.Start, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0 + LiveStartConfirmation.ConfirmationWindow));
    }

    [Fact]
    public void Drugie_wcisniecie_po_czasie_zaczyna_liczenie_od_nowa()
    {
        var confirmation = new LiveStartConfirmation();
        confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0);
        var late = T0 + LiveStartConfirmation.ConfirmationWindow + TimeSpan.FromMilliseconds(1);
        Assert.Equal(LiveStartDecision.AskForConfirmation, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, late));
        Assert.Equal(LiveStartDecision.Start, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, late.AddSeconds(1)));
    }

    [Fact]
    public void Inne_okno_miedzy_wcisnieciami_zaczyna_liczenie_od_nowa()
    {
        var confirmation = new LiveStartConfirmation();
        confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0);
        Assert.Equal(LiveStartDecision.AskForConfirmation, confirmation.Evaluate(20, LiveTargetReason.Foreground, false, T0.AddSeconds(1)));
        Assert.Equal(LiveStartDecision.AskForConfirmation, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0.AddSeconds(2)));
        Assert.Equal(LiveStartDecision.Start, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0.AddSeconds(3)));
    }

    [Fact]
    public void Po_starcie_kolejne_wcisniecie_znowu_pyta()
    {
        var confirmation = new LiveStartConfirmation();
        confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0);
        confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0.AddSeconds(1));
        Assert.Equal(LiveStartDecision.AskForConfirmation, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Znana_gra_miedzy_wcisnieciami_kasuje_oczekujace_potwierdzenie()
    {
        var confirmation = new LiveStartConfirmation();
        confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0);
        confirmation.Evaluate(30, LiveTargetReason.ProfileProcess, false, T0.AddSeconds(1));
        Assert.Equal(LiveStartDecision.AskForConfirmation, confirmation.Evaluate(10, LiveTargetReason.Foreground, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Komunikat_podaje_czas_i_wymaga_ponownego_skrotu()
    {
        Assert.Contains("6 s", LiveStartConfirmation.Message);
        Assert.Contains("skrót", LiveStartConfirmation.Message);
    }
}
