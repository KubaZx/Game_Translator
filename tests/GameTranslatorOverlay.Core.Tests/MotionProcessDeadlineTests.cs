using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class MotionProcessDeadlineTests
{
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
    private static MotionProcessDeadline CreateDeadline() => new(Ms(2500));

    [Fact]
    public void Repeated_two_moving_and_one_calm_sample_cannot_postpone_the_deadline()
    {
        var deadline = CreateDeadline();

        // No OCR begins: even a missed/failed preparation must leave the deadline due.
        for (var frame = 0; frame * 167 <= 10000; frame++)
        {
            var now = Ms(frame * 167);
            var moving = frame % 3 != 2;
            Assert.Equal(now >= Ms(2500), deadline.Observe(moving, now));
        }
    }

    [Fact]
    public void Deadline_becomes_due_exactly_at_2500_ms_even_on_a_calm_sample()
    {
        var deadline = CreateDeadline();
        Assert.False(deadline.Observe(true, TimeSpan.Zero));
        Assert.False(deadline.Observe(true, Ms(167)));
        Assert.False(deadline.Observe(false, Ms(334)));

        Assert.False(deadline.Observe(false, Ms(2500) - TimeSpan.FromTicks(1)));
        Assert.True(deadline.Observe(false, Ms(2500)));
    }

    [Fact]
    public void Continuous_motion_renews_2500_ms_from_processing_start_not_from_session_start()
    {
        var deadline = CreateDeadline();
        Assert.False(deadline.Observe(true, Ms(1000)));
        Assert.False(deadline.Observe(true, Ms(3499)));
        Assert.True(deadline.Observe(true, Ms(3500)));

        deadline.OnProcessingStarted(Ms(3500));

        Assert.False(deadline.Observe(true, Ms(3500)));
        Assert.False(deadline.Observe(true, Ms(5999)));
        Assert.True(deadline.Observe(true, Ms(6000)));
    }

    [Fact]
    public void Calm_menu_never_requests_motion_processing_even_after_normal_OCR()
    {
        var deadline = CreateDeadline();

        foreach (var milliseconds in new[] { 0, 2500, 4000, 20000 })
            Assert.False(deadline.Observe(false, Ms(milliseconds)));

        deadline.OnProcessingStarted(Ms(20000));
        Assert.False(deadline.Observe(false, Ms(40000)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Earlier_normal_OCR_finishes_the_previous_wait(bool lastObservationWasMoving)
    {
        var deadline = CreateDeadline();
        Assert.False(deadline.Observe(true, TimeSpan.Zero));
        Assert.False(deadline.Observe(lastObservationWasMoving, Ms(334)));

        // OCR can start before the motion timeout, through a normal processing path.
        deadline.OnProcessingStarted(Ms(600));

        Assert.False(deadline.Observe(lastObservationWasMoving, Ms(2500)));
        Assert.False(deadline.Observe(lastObservationWasMoving, Ms(3100) - TimeSpan.FromTicks(1)));
        Assert.Equal(lastObservationWasMoving, deadline.Observe(lastObservationWasMoving, Ms(3100)));
    }

    [Fact]
    public void Reset_clears_both_pending_deadline_and_the_last_motion_observation()
    {
        var deadline = CreateDeadline();
        Assert.False(deadline.Observe(true, TimeSpan.Zero));
        Assert.True(deadline.Observe(true, Ms(2500)));

        deadline.Reset();
        // Without clearing the motion state, this could incorrectly start a new timeout.
        deadline.OnProcessingStarted(Ms(10000));
        Assert.False(deadline.Observe(false, Ms(12500)));
        Assert.False(deadline.Observe(false, Ms(20000)));

        Assert.False(deadline.Observe(true, Ms(30000)));
        Assert.False(deadline.Observe(true, Ms(32499)));
        Assert.True(deadline.Observe(true, Ms(32500)));
    }

    [Fact]
    public void Observe_does_not_consume_due_deadline_when_frame_preparation_never_starts_OCR()
    {
        var deadline = CreateDeadline();
        Assert.False(deadline.Observe(true, Ms(1000)));

        Assert.True(deadline.Observe(true, Ms(3500)));
        Assert.True(deadline.Observe(true, Ms(3500)));
        Assert.True(deadline.Observe(false, Ms(3501)));
        Assert.True(deadline.Observe(true, Ms(9000)));
    }

    [Fact]
    public void Zero_pause_requires_motion_but_is_due_immediately_when_motion_is_observed()
    {
        var deadline = new MotionProcessDeadline(TimeSpan.Zero);
        Assert.False(deadline.Observe(false, Ms(20000)));
        Assert.True(deadline.Observe(true, Ms(20000)));

        deadline.OnProcessingStarted(Ms(20000));
        Assert.True(deadline.Observe(true, Ms(20000)));
        Assert.True(deadline.Observe(false, Ms(20000)));

        deadline.OnProcessingStarted(Ms(20000));
        Assert.False(deadline.Observe(false, Ms(40000)));
    }

    [Fact]
    public void Negative_maximum_pause_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MotionProcessDeadline(TimeSpan.FromTicks(-1)));

    [Fact]
    public void Decision_with_stabilizer_limits_OCR_gaps_despite_short_calm_samples()
    {
        var starts = SimulateDecisions(
            frame => frame % 3 != 2,
            _ => true,
            durationMs: 10000);

        // At a 167 ms sampling cadence, 2505 ms is the first sample at/after the deadline.
        Assert.Equal(new[] { 2505, 5010, 7515 }, starts);
        var boundaries = new[] { 0 }.Concat(starts).Append(10000).ToArray();
        Assert.All(boundaries.Zip(boundaries.Skip(1), (left, right) => right - left),
            gap => Assert.InRange(gap, 0, 2500 + 167));
    }

    [Fact]
    public void Decision_without_strong_motion_preserves_normal_stabilizer_OCR_times()
    {
        static bool Changed(int frame) => frame == 0 || frame is >= 4 and <= 8 || frame == 15;

        var stabilizer = new ChangeStabilizer(Ms(250), Ms(600));
        var baseline = new List<int>();
        for (var frame = 0; frame * 167 <= 4000; frame++)
            if (stabilizer.Update(Changed(frame), Ms(frame * 167)))
                baseline.Add(frame * 167);

        Assert.Equal(new[] { 334, 1336, 1670, 2839 }, baseline);
        Assert.Equal(baseline, SimulateDecisions(_ => false, Changed, durationMs: 4000));
    }

    private static List<int> SimulateDecisions(
        Func<int, bool> isMovingAt, Func<int, bool> frameChangedAt, int durationMs)
    {
        var deadline = CreateDeadline();
        var stabilizer = new ChangeStabilizer(Ms(250), Ms(600));
        var starts = new List<int>();
        for (var frame = 0; frame * 167 <= durationMs; frame++)
        {
            var now = Ms(frame * 167);
            var moving = isMovingAt(frame);
            var forceProcess = deadline.Observe(moving, now);

            // Same decision order as the live session: motion can return before the
            // stabilizer, but a due deadline passes through even on a calm sample.
            if (moving && !forceProcess)
            {
                stabilizer.ForceDirty(now);
                continue;
            }

            var shouldProcess = stabilizer.Update(frameChangedAt(frame), now) || forceProcess;
            if (forceProcess) stabilizer.Reset();
            if (!shouldProcess) continue;

            // This pure test assumes successful frame preparation and zero OCR cost.
            // The separate Observe test covers a preparation that never reaches here.
            deadline.OnProcessingStarted(now);
            starts.Add(frame * 167);
        }
        return starts;
    }
}
