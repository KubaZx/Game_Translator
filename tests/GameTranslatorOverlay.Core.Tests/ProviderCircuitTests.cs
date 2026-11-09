using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class ProviderCircuitTests
{
    [Theory]
    [InlineData(TranslationFailureKind.MissingApiKey)]
    [InlineData(TranslationFailureKind.InvalidApiKey)]
    [InlineData(TranslationFailureKind.QuotaExceeded)]
    [InlineData(TranslationFailureKind.ModelNotFound)]
    [InlineData(TranslationFailureKind.InvalidConfiguration)]
    public void Blad_trwaly_wstrzymuje_dostawce_az_do_wznowienia(TranslationFailureKind kind)
    {
        var clock = new ManualClock();
        var circuit = new ProviderCircuit(clock);

        circuit.RecordFailure(kind, "powód");
        clock.Advance(TimeSpan.FromDays(1));

        var blocked = circuit.TryAcquire();
        Assert.NotNull(blocked);
        Assert.Equal(ProviderPauseKind.Permanent, blocked.Kind);
        Assert.Equal(kind, blocked.Failure);
        Assert.Equal("powód", blocked.Message);

        circuit.Resume();
        Assert.Null(circuit.TryAcquire());
        Assert.False(circuit.Status.IsPaused);
    }

    [Theory]
    [InlineData(TranslationFailureKind.RateLimited)]
    [InlineData(TranslationFailureKind.NetworkError)]
    [InlineData(TranslationFailureKind.Timeout)]
    [InlineData(TranslationFailureKind.ServiceUnavailable)]
    public void Blad_chwilowy_daje_przerwe_5_s(TranslationFailureKind kind)
    {
        var clock = new ManualClock();
        var circuit = new ProviderCircuit(clock);

        circuit.RecordFailure(kind, "x");

        Assert.Equal(ProviderPauseKind.Temporary, circuit.Status.Kind);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(5), circuit.Status.ResumeAt);
        Assert.NotNull(circuit.TryAcquire());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(circuit.TryAcquire());
    }

    [Theory]
    [InlineData(TranslationFailureKind.TextTooLong)]
    [InlineData(TranslationFailureKind.InvalidRequest)]
    [InlineData(TranslationFailureKind.ContentRefused)]
    [InlineData(TranslationFailureKind.Unknown)]
    public void Blad_dotyczacy_tekstu_nie_wstrzymuje_dostawcy(TranslationFailureKind kind)
    {
        var circuit = new ProviderCircuit(new ManualClock());

        circuit.RecordFailure(kind, "x");

        Assert.Null(circuit.TryAcquire());
        Assert.False(circuit.Status.IsPaused);
    }

    [Fact]
    public void Przerwy_rosna_5_15_60_i_zostaja_na_60()
    {
        var clock = new ManualClock();
        var circuit = new ProviderCircuit(clock);
        var expected = new[] { 5, 15, 60, 60, 60 };

        foreach (var seconds in expected)
        {
            circuit.RecordFailure(TranslationFailureKind.NetworkError, "x");
            Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(seconds), circuit.Status.ResumeAt);
            clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.NotNull(circuit.TryAcquire());
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(circuit.TryAcquire());
        }
    }

    [Fact]
    public void Udana_odpowiedz_zeruje_harmonogram()
    {
        var clock = new ManualClock();
        var circuit = new ProviderCircuit(clock);
        circuit.RecordFailure(TranslationFailureKind.Timeout, "x");
        clock.Advance(TimeSpan.FromSeconds(5));
        circuit.RecordFailure(TranslationFailureKind.Timeout, "x");
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(15), circuit.Status.ResumeAt);

        circuit.RecordResponse();
        Assert.False(circuit.Status.IsPaused);

        circuit.RecordFailure(TranslationFailureKind.Timeout, "x");
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(5), circuit.Status.ResumeAt);
    }

    [Fact]
    public void Po_przerwie_przechodzi_jedna_proba_a_reszta_czeka()
    {
        var clock = new ManualClock();
        var circuit = new ProviderCircuit(clock);
        circuit.RecordFailure(TranslationFailureKind.ServiceUnavailable, "x");
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Null(circuit.TryAcquire());
        Assert.NotNull(circuit.TryAcquire());

        circuit.Abandon();
        Assert.Null(circuit.TryAcquire());
    }

    [Fact]
    public void Proba_bez_wyniku_wygasa_po_dzierzawie()
    {
        var clock = new ManualClock();
        var circuit = new ProviderCircuit(clock);
        circuit.RecordFailure(TranslationFailureKind.ServiceUnavailable, "x");
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(circuit.TryAcquire());

        clock.Advance(ProviderCircuit.ProbeLease - TimeSpan.FromSeconds(1));
        Assert.NotNull(circuit.TryAcquire());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(circuit.TryAcquire());
    }

    [Fact]
    public void Blad_trwaly_ma_pierwszenstwo_nad_chwilowym()
    {
        var circuit = new ProviderCircuit(new ManualClock());
        circuit.RecordFailure(TranslationFailureKind.NetworkError, "x");
        circuit.RecordFailure(TranslationFailureKind.InvalidApiKey, "klucz");

        Assert.Equal(ProviderPauseKind.Permanent, circuit.Status.Kind);
        circuit.RecordResponse();
        Assert.Equal(ProviderPauseKind.Permanent, circuit.Status.Kind);
    }
}

public class FailedTextMemoryTests
{
    private static readonly FailedTextInfo Info = new(TranslationFailureKind.ContentRefused, "odmowa", EmptyResult: false);

    [Fact]
    public void Dopiero_drugi_blad_z_rzedu_blokuje_tekst()
    {
        var memory = new FailedTextMemory(new ManualClock());

        memory.RecordFailure("Alpha", Info);
        Assert.False(memory.TryGetBlock("Alpha", out _));

        memory.RecordFailure("Alpha", Info);
        Assert.True(memory.TryGetBlock("Alpha", out var info));
        Assert.Equal(Info, info);
        Assert.False(memory.TryGetBlock("Beta", out _));
    }

    [Fact]
    public void Blokada_wygasa_po_5_minutach()
    {
        var clock = new ManualClock();
        var memory = new FailedTextMemory(clock);
        memory.RecordFailure("Alpha", Info);
        memory.RecordFailure("Alpha", Info);

        clock.Advance(FailedTextMemory.Quarantine - TimeSpan.FromSeconds(1));
        Assert.True(memory.TryGetBlock("Alpha", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(memory.TryGetBlock("Alpha", out _));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void Pierwszy_blad_po_dlugiej_przerwie_nie_liczy_sie_z_poprzednim()
    {
        var clock = new ManualClock();
        var memory = new FailedTextMemory(clock);
        memory.RecordFailure("Alpha", Info);
        clock.Advance(FailedTextMemory.Quarantine + TimeSpan.FromSeconds(1));

        memory.RecordFailure("Alpha", Info);

        Assert.False(memory.TryGetBlock("Alpha", out _));
    }

    [Fact]
    public void Sukces_zdejmuje_blokade()
    {
        var memory = new FailedTextMemory(new ManualClock());
        memory.RecordFailure("Alpha", Info);
        memory.RecordFailure("Alpha", Info);

        memory.RecordSuccess("Alpha");

        Assert.False(memory.TryGetBlock("Alpha", out _));
    }

    [Fact]
    public void Liczba_wpisow_jest_ograniczona()
    {
        var clock = new ManualClock();
        var memory = new FailedTextMemory(clock, capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            memory.RecordFailure("text" + i, Info);
            memory.RecordFailure("text" + i, Info);
        }

        Assert.Equal(3, memory.Count);
        Assert.True(memory.TryGetBlock("text9", out _));
        Assert.False(memory.TryGetBlock("text0", out _));
    }
}
