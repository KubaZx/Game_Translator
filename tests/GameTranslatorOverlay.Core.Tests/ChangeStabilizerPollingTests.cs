using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class ChangeStabilizerPollingTests
{
    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
    private static ChangeStabilizer Create() => new(Ms(250), Ms(600));

    [Fact]
    public void A_quiet_frame_at_167_ms_schedules_the_exact_250_ms_stability_boundary()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(0)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(0), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(167)));

        Assert.Equal(Ms(83), stabilizer.GetPollingDelay(Ms(167), Ms(167), Ms(167)));
        // Asking for a delay must not consume the pending read or lower its threshold.
        Assert.Equal(Ms(83), stabilizer.GetPollingDelay(Ms(167), Ms(167), Ms(167)));
        Assert.True(stabilizer.IsDirty);
        Assert.False(stabilizer.Update(frameChanged: false, Ms(250) - TimeSpan.FromTicks(1)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(250)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(250), Ms(167), Ms(167)));
    }

    [Fact]
    public void Deadline_polling_brings_both_reads_forward_without_shortening_confirmation()
    {
        var normal = RunTwoReadScenario(useDeadline: false);
        var scheduled = RunTwoReadScenario(useDeadline: true);

        Assert.Equal(new[] { Ms(334), Ms(668) }, normal.OcrStarts);
        Assert.Equal(new[] { Ms(250), Ms(530) }, scheduled.OcrStarts);
        // OCR takes 30 ms. The second read remains 250 ms after the first one ends.
        Assert.Equal(Ms(250), scheduled.OcrStarts[1] - (scheduled.OcrStarts[0] + Ms(30)));
        Assert.Equal(5, normal.Captures);
        Assert.Equal(5, scheduled.Captures);
    }

    [Fact]
    public void Continuous_motion_renewing_ForceDirty_keeps_the_normal_poll_interval()
    {
        var stabilizer = Create();
        for (var elapsedMs = 0; elapsedMs <= 10020; elapsedMs += 167)
        {
            var now = Ms(elapsedMs);
            // The live loop returns before Update while strong motion defers OCR.
            stabilizer.ForceDirty(now);
            Assert.Equal(Ms(167), stabilizer.GetPollingDelay(now, Ms(167), Ms(167)));
        }
    }

    [Fact]
    public void A_change_in_the_deadline_capture_postpones_OCR_until_the_new_stability_boundary()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(0)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(167)));
        Assert.Equal(Ms(83), stabilizer.GetPollingDelay(Ms(167), Ms(167), Ms(167)));

        // Reaching the timer is not permission to process an image that changed again.
        Assert.False(stabilizer.Update(frameChanged: true, Ms(250)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(250), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(417)));
        Assert.Equal(Ms(83), stabilizer.GetPollingDelay(Ms(417), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(499)));
        Assert.True(stabilizer.Update(frameChanged: false, Ms(500)));
    }

    [Fact]
    public void A_clean_or_reset_stabilizer_preserves_normal_polling()
    {
        var stabilizer = Create();
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(167), Ms(167), Ms(167)));
        stabilizer.ForceDirty(Ms(0));
        Assert.Equal(Ms(83), stabilizer.GetPollingDelay(Ms(167), Ms(167), Ms(167)));

        stabilizer.Reset();

        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(167), Ms(167), Ms(167)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(20000), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: false, Ms(20000)));
    }

    [Theory]
    [InlineData(250)]
    [InlineData(251)]
    [InlineData(1000)]
    public void An_expired_deadline_keeps_capture_failure_retries_from_spinning(int elapsedMs)
    {
        var stabilizer = Create();
        stabilizer.ForceDirty(Ms(0));
        var now = Ms(elapsedMs);

        // A failed capture can leave dirty state intact after its deadline.
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(now, Ms(167), Ms(167)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(now, Ms(167), Ms(167)));
        Assert.True(stabilizer.IsDirty);
        // A subsequent successful, quiet capture still consumes the due read.
        Assert.True(stabilizer.Update(frameChanged: false, now + Ms(167)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void A_cycle_that_already_used_its_interval_does_not_add_another_delay(int normalMs)
    {
        var stabilizer = Create();
        Assert.Equal(TimeSpan.Zero, stabilizer.GetPollingDelay(Ms(167), Ms(normalMs), Ms(167)));
        stabilizer.ForceDirty(Ms(0));
        Assert.Equal(TimeSpan.Zero, stabilizer.GetPollingDelay(Ms(167), Ms(normalMs), Ms(167)));
    }

    [Fact]
    public void High_frame_rate_polling_is_never_delayed_until_a_later_stability_deadline()
    {
        var stabilizer = Create();
        stabilizer.ForceDirty(Ms(0));

        Assert.Equal(Ms(33), stabilizer.GetPollingDelay(Ms(167), Ms(33), Ms(33)));
        Assert.Equal(Ms(33), stabilizer.GetPollingDelay(Ms(217), Ms(33), Ms(33)));
        Assert.Equal(Ms(10), stabilizer.GetPollingDelay(Ms(240), Ms(33), Ms(33)));
    }

    [Fact]
    public void Querying_poll_delays_does_not_restart_the_600_ms_forced_processing_limit()
    {
        var stabilizer = Create();
        Assert.False(stabilizer.Update(frameChanged: true, Ms(0)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(0), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(200)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(200), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(400)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(400), Ms(167), Ms(167)));
        Assert.False(stabilizer.Update(frameChanged: true, Ms(599)));
        Assert.Equal(Ms(167), stabilizer.GetPollingDelay(Ms(599), Ms(167), Ms(167)));

        Assert.True(stabilizer.Update(frameChanged: true, Ms(600)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(300)]
    public void Continuous_motion_at_two_FPS_preserves_the_full_cycle_interval(int captureMs)
    {
        var stabilizer = Create();
        for (var cycleStartMs = 0; cycleStartMs < 5000; cycleStartMs += 500)
        {
            var now = Ms(cycleStartMs + captureMs);
            stabilizer.ForceDirty(now);
            var delay = stabilizer.GetPollingDelay(now, Ms(500 - captureMs), Ms(500));

            Assert.Equal(Ms(500 - captureMs), delay);
            Assert.Equal(Ms(cycleStartMs + 500), now + delay);
        }
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(500, 200)]
    [InlineData(250, 250)]
    [InlineData(250, 150)]
    public void Low_FPS_and_the_four_FPS_boundary_use_the_full_interval_not_remaining_delay(
        int pollingMs, int remainingMs)
    {
        var stabilizer = Create();
        stabilizer.ForceDirty(Ms(0));

        // Stability is only 83 ms away. Time already spent in a cycle must not
        // turn a deliberately low capture rate into the faster deadline mode.
        Assert.Equal(Ms(remainingMs),
            stabilizer.GetPollingDelay(Ms(167), Ms(remainingMs), Ms(pollingMs)));
    }
    private static (TimeSpan[] OcrStarts, int Captures) RunTwoReadScenario(bool useDeadline)
    {
        var stabilizer = Create();
        var starts = new List<TimeSpan>();
        var now = TimeSpan.Zero;
        var captures = 0;
        while (now <= Ms(1000) && starts.Count < 2 && captures < 20)
        {
            var cycleStart = now;
            captures++;
            if (stabilizer.Update(frameChanged: captures == 1, now))
            {
                starts.Add(now);
                now += Ms(30);
                if (starts.Count == 2) break;
                stabilizer.ForceDirty(now);
            }

            // Same ordering as the session: fresh capture/Update, processing, then sleep.
            var normalDelay = cycleStart + Ms(167) - now;
            now += useDeadline ? stabilizer.GetPollingDelay(now, normalDelay, Ms(167)) : normalDelay;
        }
        return (starts.ToArray(), captures);
    }
}