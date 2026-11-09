using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class VolatileCounterGateTests
{
    private const string Marker = "\u0001";

    [Theory]
    [InlineData("Time 01:24", "Time " + Marker + ":" + Marker)]
    [InlineData("You found 3 keys", "You found " + Marker + " keys")]
    [InlineData("12345", Marker)]
    [InlineData("No digits here", "No digits here")]
    public void Maska_zastepuje_kazdy_ciag_cyfr_jednym_znacznikiem(string text, string expected)
    {
        Assert.Equal(expected, VolatileCounterGate.Mask(text));
    }

    [Theory]
    [InlineData("12:34", true)]
    [InlineData("1:02:03", true)]
    [InlineData("3/10", true)]
    [InlineData("45%", true)]
    [InlineData("Time 01:24 left, 50% done", true)]
    [InlineData("3 keys", false)]
    [InlineData("12/05/2024", false)]
    [InlineData("Level 5: 3/10", false)]
    [InlineData("no digits", false)]
    public void Liczby_zegara_ulamka_i_procentu_sa_rozpoznawane_tylko_gdy_to_jedyne_cyfry(string text, bool expected)
    {
        Assert.Equal(expected, VolatileCounterGate.ClockFractionPercentNumbers(text) is not null);
    }

    [Fact]
    public void Podstawienie_zachowuje_kolejnosc_liczb()
    {
        var result = VolatileCounterGate.Substitute("Wave 4/10 - 02:30", "Wave 3/10 - 01:15", "Fala 3/10 - 01:15");

        Assert.Equal("Fala 4/10 - 02:30", result);
    }

    [Theory]
    [InlineData("Wave 4/10", "Wave 3/10", "Fala 10/3")]
    [InlineData("Wave 4/10", "Wave 3/10", "Fala 3")]
    [InlineData("You found 5 keys", "You found 3 keys", "Znaleziono 3 klucze")]
    [InlineData("Time 01:25", "Time 01:24", "Czas 1:24")]
    [InlineData("Time 01:25 of 20", "Time 01:24", "Czas 01:24")]
    public void Podstawienie_jest_pomijane_gdy_liczby_nie_pasuja(string newText, string source, string translation)
    {
        Assert.Null(VolatileCounterGate.Substitute(newText, source, translation));
    }

    [Theory]
    [InlineData("+25%", false)]
    [InlineData("12:30", false)]
    [InlineData("100", false)]
    [InlineData("-- / --", false)]
    [InlineData("1st", true)]
    [InlineData("x2 Bonus", true)]
    public void Tekst_bez_litery_jest_rozpoznawany(string text, bool hasLetter)
    {
        Assert.Equal(hasLetter, VolatileCounterGate.HasLetter(text));
    }

    [Fact]
    public void Trzeci_rozny_tekst_o_tej_samej_masce_w_30_s_wycisza_maske()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);

        Assert.Equal(CounterAction.Send, gate.Evaluate("Time 01:24", 1).Action);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(CounterAction.Send, gate.Evaluate("Time 01:28", 2).Action);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(CounterAction.Skip, gate.Evaluate("Time 01:32", 3).Action);
        Assert.True(gate.IsMuted("Time 01:40"));
    }

    [Fact]
    public void Powtorzony_ten_sam_tekst_nie_liczy_sie_jako_nowy_wariant()
    {
        var gate = new VolatileCounterGate(new ManualClock());

        for (var batch = 1; batch <= 10; batch++)
        {
            Assert.Equal(CounterAction.Send, gate.Evaluate("Time 01:24", batch).Action);
            Assert.Equal(CounterAction.Send, gate.Evaluate("Time 01:25", batch).Action);
        }
    }

    [Fact]
    public void Warianty_z_jednej_partii_to_jedno_zdarzenie()
    {
        var gate = new VolatileCounterGate(new ManualClock());

        for (var i = 0; i < 40; i++) Assert.Equal(CounterAction.Send, gate.Evaluate($"Slot {i}", batch: 1).Action);
        for (var batch = 2; batch < VolatileCounterGate.DistinctWordedTextsToMute; batch++)
            Assert.Equal(CounterAction.Send, gate.Evaluate($"Slot {98 + batch}", batch).Action);
        Assert.Equal(CounterAction.Skip, gate.Evaluate("Slot 200", VolatileCounterGate.DistinctWordedTextsToMute).Action);
    }

    [Fact]
    public void Dwa_dialogi_z_liczba_w_odstepie_minuty_niczego_nie_wyciszaja()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);

        for (var i = 1; i <= 6; i++)
        {
            Assert.Equal(CounterAction.Send, gate.Evaluate($"Chapter {i} begins", i).Action);
            clock.Advance(TimeSpan.FromSeconds(60));
        }
        Assert.False(gate.IsMuted("Chapter 9 begins"));
    }

    [Fact]
    public void Wyciszenie_wygasa_po_10_minutach_a_kazdy_wariant_je_przedluza()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);
        gate.Evaluate("Time 01:01", 1);
        gate.Evaluate("Time 01:02", 2);
        Assert.Equal(CounterAction.Skip, gate.Evaluate("Time 01:03", 3).Action);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(CounterAction.Skip, gate.Evaluate("Time 01:04", 4).Action);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(CounterAction.Skip, gate.Evaluate("Time 01:05", 5).Action);

        clock.Advance(VolatileCounterGate.Mute);
        Assert.False(gate.IsMuted("Time 01:06"));
        Assert.Equal(CounterAction.Send, gate.Evaluate("Time 01:06", 6).Action);
    }

    [Fact]
    public void Wyciszony_zegar_dostaje_ostatnie_tlumaczenie_z_nowymi_liczbami()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);
        gate.Evaluate("Time 01:01", 1);
        gate.Evaluate("Time 01:02", 2);
        gate.RecordTranslated("Time 01:02", "Czas 01:02");

        var decision = gate.Evaluate("Time 01:03", 3);

        Assert.Equal(CounterAction.Substitute, decision.Action);
        Assert.Equal("Czas 01:03", decision.Translation);
    }

    [Fact]
    public void Zmienione_liczby_w_tlumaczeniu_wylaczaja_podstawianie()
    {
        var gate = new VolatileCounterGate(new ManualClock());
        gate.Evaluate("Time 10:30 PM", 1);
        gate.Evaluate("Time 10:31 PM", 2);
        gate.RecordTranslated("Time 10:31 PM", "Czas 22:31");

        Assert.Equal(CounterAction.Skip, gate.Evaluate("Time 10:32 PM", 3).Action);
    }

    [Fact]
    public void Liczba_przy_slowach_jest_wyciszana_dopiero_przy_osmym_wariancie_i_dostaje_nowe_cyfry()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);
        for (var batch = 1; batch < VolatileCounterGate.DistinctWordedTextsToMute; batch++)
        {
            Assert.Equal(CounterAction.Send, gate.Evaluate($"Requires Level {batch}", batch).Action);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        gate.RecordTranslated("Requires Level 7", "Wymaga poziomu 7");

        var decision = gate.Evaluate("Requires Level 36", 100);

        Assert.Equal(CounterAction.Substitute, decision.Action);
        Assert.Equal("Wymaga poziomu 36", decision.Translation);
    }

    [Fact]
    public void Kilka_podpowiedzi_z_liczba_w_pol_minuty_idzie_do_dostawcy()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);

        foreach (var level in new[] { 12, 24, 36, 48, 60 })
        {
            Assert.Equal(CounterAction.Send, gate.Evaluate($"Requires Level {level}", level).Action);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.False(gate.IsMuted("Requires Level 72"));
    }

    [Fact]
    public void Wyciszenie_liczby_przy_slowach_nie_jest_przedluzane_kolejnymi_wariantami()
    {
        var clock = new ManualClock();
        var gate = new VolatileCounterGate(clock);
        for (var batch = 1; batch <= VolatileCounterGate.DistinctWordedTextsToMute; batch++) gate.Evaluate($"Score {batch}", batch);
        Assert.True(gate.IsMuted("Score 99"));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(CounterAction.Skip, gate.Evaluate("Score 500", 500).Action);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(gate.IsMuted("Score 501"));
        Assert.Equal(CounterAction.Send, gate.Evaluate("Score 501", 501).Action);
    }

    [Fact]
    public void Liczby_zapisane_inaczej_w_tlumaczeniu_wylaczaja_podstawianie()
    {
        Assert.Null(VolatileCounterGate.SubstituteDigitRuns("You found 7 keys", "You found 2 keys", "Znaleziono dwa klucze"));
        Assert.Null(VolatileCounterGate.SubstituteDigitRuns("Wave 4 of 10", "Wave 3 of 10", "Fala 10 z 3"));
        Assert.Equal("Nagroda: 2 500 szt.", VolatileCounterGate.SubstituteDigitRuns("Reward: 2,500 pcs", "Reward: 1,000 pcs", "Nagroda: 1 000 szt."));
    }

    [Fact]
    public void Sprawdzenie_bez_zatwierdzenia_niczego_nie_zapamietuje()
    {
        var gate = new VolatileCounterGate(new ManualClock());

        for (var batch = 1; batch <= 10; batch++)
            Assert.Equal(CounterAction.Send, gate.Evaluate($"Time 01:{batch:00}", batch, commit: false).Action);
        Assert.False(gate.IsMuted("Time 01:59"));
    }

    [Fact]
    public void Tekst_bez_cyfr_przechodzi_zawsze()
    {
        var gate = new VolatileCounterGate(new ManualClock());

        for (var batch = 1; batch <= 20; batch++)
            Assert.Equal(CounterAction.Send, gate.Evaluate("Press any key", batch).Action);
    }
}
