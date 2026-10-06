using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveSceneValidityTests
{
    private static LiveSceneValidity CreateValidity() => new(sceneCutThreshold: 0.55, motionThreshold: 0.12);

    private static NoiseAwareAnalysis Analysis(double changed, double strong, double? significant = null) =>
        new(changed, strong, significant ?? changed,
            changed > 0 ? new RectPx(0, 0, 3840, 2160) : null);

    [Fact]
    public void Initial_full_frame_is_not_a_scene_cut_or_a_motion_sample()
    {
        var validity = CreateValidity();
        var epoch = validity.Generation;

        Assert.False(validity.Observe(Analysis(1, 0), hasPreviousFrame: false));
        Assert.Equal(epoch, validity.Generation);
        Assert.True(validity.IsCurrent(epoch));
        Assert.Equal(0, validity.MotionSamples);

        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.Equal(1, validity.MotionSamples);
    }

    [Fact]
    public void Soft_global_scene_cut_invalidates_pending_result_without_strong_motion()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;

        Assert.True(validity.Observe(Analysis(0.8, 0), hasPreviousFrame: true));

        Assert.Equal(pendingEpoch + 1, validity.Generation);
        Assert.False(validity.IsCurrent(pendingEpoch));
        Assert.True(validity.IsCurrent(validity.Generation));
        Assert.Equal(0, validity.MotionSamples);
    }

    [Fact]
    public void Only_the_second_consecutive_motion_sample_invalidates_the_pending_result()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;

        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.True(validity.IsCurrent(pendingEpoch));
        Assert.Equal(1, validity.MotionSamples);

        Assert.True(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.Equal(pendingEpoch + 1, validity.Generation);
        Assert.False(validity.IsCurrent(pendingEpoch));
        Assert.Equal(2, validity.MotionSamples);
    }


    [Fact]
    public void New_OCR_started_during_confirmed_motion_is_invalidated_by_continued_camera_motion()
    {
        var validity = CreateValidity();
        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.True(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));

        // The motion deadline may now start a new OCR. Its result belongs to this
        // generation, even though this continuous movement episode already reached two samples.
        var newOcrEpoch = validity.Generation;
        Assert.True(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.False(validity.IsCurrent(newOcrEpoch));

        var anotherOcrEpoch = validity.Generation;
        Assert.True(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.False(validity.IsCurrent(anotherOcrEpoch));
    }

    [Fact]
    public void Ordinary_animation_and_local_hover_do_not_invalidate_the_scene()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;

        for (var sample = 0; sample < 30; sample++)
        {
            // Distributed weak animation can have no significant cells after noise filtering.
            Assert.False(validity.Observe(Analysis(0.3, 0, significant: 0), hasPreviousFrame: true));
            Assert.False(validity.Observe(Analysis(0.04, 0.02), hasPreviousFrame: true));
        }

        Assert.Equal(pendingEpoch, validity.Generation);
        Assert.True(validity.IsCurrent(pendingEpoch));
        Assert.Equal(0, validity.MotionSamples);
    }

    [Fact]
    public void A_calm_sample_breaks_motion_confirmation_before_the_second_sample()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;

        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.Equal(1, validity.MotionSamples);
        Assert.False(validity.Observe(Analysis(0.03, 0), hasPreviousFrame: true));
        Assert.Equal(0, validity.MotionSamples);

        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.True(validity.IsCurrent(pendingEpoch));
        Assert.Equal(1, validity.MotionSamples);
        Assert.True(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.False(validity.IsCurrent(pendingEpoch));
    }

    [Fact]
    public void Returning_to_old_colors_does_not_make_an_old_cached_epoch_current_again()
    {
        var validity = CreateValidity();
        var firstSceneRequest = validity.Generation;

        // Scene A -> B, then B -> A. A delayed result from the first A is still stale.
        Assert.True(validity.Observe(Analysis(0.8, 0), hasPreviousFrame: true));
        var secondSceneRequest = validity.Generation;
        Assert.True(validity.Observe(Analysis(0.8, 0), hasPreviousFrame: true));
        Assert.False(validity.Observe(Analysis(0, 0), hasPreviousFrame: true));

        Assert.False(validity.IsCurrent(firstSceneRequest));
        Assert.False(validity.IsCurrent(secondSceneRequest));
        Assert.True(validity.IsCurrent(validity.Generation));
    }

    [Fact]
    public void Reset_for_a_minimized_or_lost_window_invalidates_inflight_result_and_motion()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;
        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));

        validity.Reset();

        Assert.Equal(pendingEpoch + 1, validity.Generation);
        Assert.False(validity.IsCurrent(pendingEpoch));
        Assert.Equal(0, validity.MotionSamples);
        var restoredEpoch = validity.Generation;
        Assert.False(validity.Observe(Analysis(1, 0), hasPreviousFrame: false));
        Assert.True(validity.IsCurrent(restoredEpoch));
        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true));
        Assert.Equal(1, validity.MotionSamples);
    }

    [Fact]
    public void Scene_cut_threshold_includes_exactly_0_55_but_not_the_value_below_it()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;

        Assert.False(validity.Observe(Analysis(Math.BitDecrement(0.55), 0), hasPreviousFrame: true));
        Assert.True(validity.IsCurrent(pendingEpoch));
        Assert.True(validity.Observe(Analysis(0.55, 0), hasPreviousFrame: true));
        Assert.False(validity.IsCurrent(pendingEpoch));
    }

    [Fact]
    public void Motion_threshold_includes_exactly_0_12_but_not_the_value_below_it()
    {
        var validity = CreateValidity();
        var pendingEpoch = validity.Generation;

        for (var sample = 0; sample < 3; sample++)
            Assert.False(validity.Observe(Analysis(0.3, Math.BitDecrement(0.12)), hasPreviousFrame: true));
        Assert.Equal(0, validity.MotionSamples);

        Assert.False(validity.Observe(Analysis(0.3, 0.12), hasPreviousFrame: true));
        Assert.True(validity.IsCurrent(pendingEpoch));
        Assert.Equal(1, validity.MotionSamples);
        Assert.True(validity.Observe(Analysis(0.3, 0.12), hasPreviousFrame: true));
        Assert.False(validity.IsCurrent(pendingEpoch));
        Assert.Equal(2, validity.MotionSamples);
    }

    [Fact]
    public void Ze_sledzeniem_trwajacy_ruch_nie_uniewaznia_a_pierwsza_duza_zmiana_tak()
    {
        var validity = CreateValidity();
        var epoch = validity.Generation;

        Assert.True(validity.Observe(Analysis(0.9, 0.5), hasPreviousFrame: true, tolerateMotion: true));
        Assert.Equal(epoch + 1, validity.Generation);
        var during = validity.Generation;

        Assert.False(validity.Observe(Analysis(0.9, 0.5), hasPreviousFrame: true, tolerateMotion: true));
        Assert.False(validity.Observe(Analysis(0.9, 0.5), hasPreviousFrame: true, tolerateMotion: true));
        Assert.True(validity.IsCurrent(during));
        Assert.Equal(3, validity.MotionSamples);

        Assert.False(validity.Observe(Analysis(0.3, 0.2), hasPreviousFrame: true, tolerateMotion: true));
        Assert.True(validity.IsCurrent(during));
    }
}
