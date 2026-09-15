using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class OcrSceneCheckTimingTests
{
    private static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(1000d / 6);
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    [Theory]
    [InlineData(150, false)]
    [InlineData(166.666, false)]
    [InlineData(175, true)]
    [InlineData(250, true)]
    [InlineData(500, true)]
    public void Completion_preserves_the_original_age_threshold(double elapsedMs, bool requiresCheck)
    {
        Assert.Equal(requiresCheck,
            OcrSceneCheckTiming.RequiresCompletionCheck(Ms(elapsedMs), Normal, performedPeriodicCheck: false));
    }

    [Fact]
    public void A_frame_one_tick_before_the_boundary_can_finish_but_the_boundary_requires_a_fresh_capture()
    {
        Assert.False(OcrSceneCheckTiming.RequiresCompletionCheck(Normal - TimeSpan.FromTicks(1), Normal, false));
        Assert.True(OcrSceneCheckTiming.RequiresCompletionCheck(Normal, Normal, false));
        Assert.True(OcrSceneCheckTiming.RequiresCompletionCheck(Normal + TimeSpan.FromTicks(1), Normal, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(166.666)]
    public void An_observed_periodic_check_always_requires_a_fresh_completion_check(double elapsedMs)
    {
        // Even an unexpectedly low elapsed reading must not authorize reusing a
        // bitmap from a periodic capture that may have begun before OCR completed.
        Assert.True(OcrSceneCheckTiming.RequiresCompletionCheck(Ms(elapsedMs), Normal, performedPeriodicCheck: true));
    }

    [Theory]
    [InlineData(150, 0, 150)]
    [InlineData(175, 1, 245)]
    [InlineData(225, 1, 295)]
    public void OCR_finishing_before_the_first_periodic_deadline_avoids_redundant_captures(
        double ocrMs, int totalCaptures, double expectedCompletionMs)
    {
        var run = Simulate(Ms(ocrMs), Normal, Ms(70));

        Assert.Empty(run.PeriodicStarts);
        Assert.Equal(totalCaptures, run.PeriodicStarts.Length + (run.CompletionCheck ? 1 : 0));
        Assert.Equal(Ms(expectedCompletionMs), run.CompletedAt);
    }

    [Fact]
    public void OCR_completing_during_a_periodic_capture_still_gets_a_new_capture_afterwards()
    {
        // OCR completes at 260 ms, inside the first capture (roughly 250–320 ms).
        // That capture could have obtained its pixels at 250 ms, before a scene
        // change at 270 ms. It cannot stand in for the final freshness check.
        var run = Simulate(Ms(260), Normal, Ms(70));

        Assert.Single(run.PeriodicStarts);
        Assert.True(run.PeriodicStarts[0] < Ms(260));
        Assert.True(run.PeriodicStarts[0] + Ms(70) > Ms(260));
        Assert.True(run.CompletionCheck);
        Assert.Equal(run.PeriodicStarts[0] + Ms(140), run.CompletedAt);
    }

    [Fact]
    public void A_slow_OCR_returns_to_normal_periodic_spacing_and_keeps_the_final_check()
    {
        var run = Simulate(Ms(850), Normal, Ms(70));

        Assert.Equal(3, run.PeriodicStarts.Length);
        Assert.InRange((run.PeriodicStarts[0] - Ms(250)).Ticks, -1, 1);
        Assert.Equal(Normal + Ms(70), run.PeriodicStarts[1] - run.PeriodicStarts[0]);
        Assert.Equal(Normal + Ms(70), run.PeriodicStarts[2] - run.PeriodicStarts[1]);
        Assert.True(run.CompletionCheck);
        Assert.Equal(Ms(920), run.CompletedAt);
    }

    [Theory]
    [InlineData(0.5, 3000)]
    [InlineData(2, 750)]
    [InlineData(6, 250)]
    [InlineData(30, 50)]
    public void The_first_deadline_scales_with_supported_FPS_including_deliberately_slow_sampling(
        double fps, double expectedDelayMs)
    {
        var interval = TimeSpan.FromSeconds(1 / fps);
        var delay = OcrSceneCheckTiming.FirstCheckDelay(interval);

        Assert.True(delay > interval);
        // TimeSpan stores integer ticks; FPS-derived intervals can round by one.
        Assert.InRange((delay - Ms(expectedDelayMs)).Ticks, -1, 1);
    }

    [Fact]
    public void Half_FPS_preserves_its_two_second_completion_threshold()
    {
        var interval = TimeSpan.FromSeconds(2);
        Assert.False(OcrSceneCheckTiming.RequiresCompletionCheck(Ms(500), interval, false));
        Assert.False(OcrSceneCheckTiming.RequiresCompletionCheck(interval - TimeSpan.FromTicks(1), interval, false));
        Assert.True(OcrSceneCheckTiming.RequiresCompletionCheck(interval, interval, false));

        var run = Simulate(Ms(2500), interval, Ms(70));
        Assert.Empty(run.PeriodicStarts);
        Assert.True(run.CompletionCheck);
        Assert.Equal(Ms(2570), run.CompletedAt);
    }

    // Deterministic ordering model: Task completion wakes the wait immediately;
    // a synchronous capture must finish before the loop observes that completion.
    // The model never substitutes a previous capture for the required final one.
    private static (TimeSpan[] PeriodicStarts, bool CompletionCheck, TimeSpan CompletedAt) Simulate(
        TimeSpan ocrDuration, TimeSpan normalInterval, TimeSpan captureDuration)
    {
        var starts = new List<TimeSpan>();
        var now = TimeSpan.Zero;
        var delay = OcrSceneCheckTiming.FirstCheckDelay(normalInterval);
        while (now < ocrDuration)
        {
            if (now + delay >= ocrDuration)
            { now = ocrDuration; break; }
            now += delay;
            starts.Add(now);
            now += captureDuration;
            delay = normalInterval;
        }
        var completionCheck = OcrSceneCheckTiming.RequiresCompletionCheck(now, normalInterval, starts.Count > 0);
        if (completionCheck) now += captureDuration;
        return (starts.ToArray(), completionCheck, now);
    }
}