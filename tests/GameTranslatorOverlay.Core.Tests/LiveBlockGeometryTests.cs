using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveBlockGeometryTests
{
    [Theory]
    [InlineData(80, 20)]
    [InlineData(300, 40)]
    [InlineData(1000, 100)]
    public void Three_pixel_motion_is_followed_at_each_reading_regardless_of_text_size(int width, int height)
    {
        var displayed = new RectPx(100, 80, width, height);
        for (var step = 1; step <= 12; step++)
        {
            var observed = new RectPx(100 + step * 3, 80 + step * 3, width, height);
            displayed = LiveBlockGeometry.Stabilize(displayed, observed);
            Assert.Equal(observed, displayed);
        }
    }

    [Fact]
    public void Alternating_one_pixel_noise_keeps_the_original_position()
    {
        var original = new RectPx(100, 80, 300, 40);
        var displayed = original;
        foreach (var offset in new[] { 1, -1, 1, -1, 0, 1, -1 })
        {
            displayed = LiveBlockGeometry.Stabilize(displayed, original.Offset(offset, -offset));
            Assert.Equal(original, displayed);
        }
    }

    [Fact]
    public void One_pixel_motion_accumulates_against_displayed_position_and_catches_up_at_three()
    {
        var original = new RectPx(100, 80, 300, 40);
        var displayed = original;
        for (var step = 1; step <= 9; step++)
        {
            var observed = original.Offset(step, 0);
            displayed = LiveBlockGeometry.Stabilize(displayed, observed);
            Assert.InRange(observed.X - displayed.X, 0, 2);
            Assert.Equal(original.X + step / 3 * 3, displayed.X);
        }
    }

    [Fact]
    public void Horizontal_motion_does_not_admit_vertical_noise_or_small_size_noise()
    {
        var previous = new RectPx(100, 80, 300, 40);
        var observed = new RectPx(109, 81, 311, 47);
        Assert.Equal(new RectPx(109, 80, 300, 40), LiveBlockGeometry.Stabilize(previous, observed));
    }

    [Fact]
    public void Large_jump_is_immediate_while_size_stays_stable()
    {
        var previous = new RectPx(100, 80, 300, 40);
        Assert.Equal(new RectPx(800, -200, 300, 40),
            LiveBlockGeometry.Stabilize(previous, new RectPx(800, -200, 312, 48)));
    }

    [Theory]
    [InlineData(100, 20, 112, 32, true)]
    [InlineData(300, 100, 345, 135, true)]
    [InlineData(300, 100, 346, 136, false)]
    public void Size_tolerances_keep_existing_symmetric_thresholds(int width, int height, int otherWidth, int otherHeight, bool retain)
    {
        var a = new RectPx(100, 80, width, height);
        var b = new RectPx(100, 80, otherWidth, otherHeight);
        Assert.Equal(retain ? a : b, LiveBlockGeometry.Stabilize(a, b));
        Assert.Equal(retain ? b : a, LiveBlockGeometry.Stabilize(b, a));
    }

    [Fact]
    public void Width_and_height_stabilize_independently()
    {
        var previous = new RectPx(100, 80, 300, 40);
        Assert.Equal(new RectPx(100, 80, 360, 40),
            LiveBlockGeometry.Stabilize(previous, new RectPx(101, 79, 360, 47)));
        Assert.Equal(new RectPx(100, 80, 300, 80),
            LiveBlockGeometry.Stabilize(previous, new RectPx(101, 79, 312, 80)));
    }

    [Fact]
    public void Empty_input_never_creates_or_resurrects_a_box()
    {
        var observed = new RectPx(100, 80, 300, 40);
        Assert.Equal(observed, LiveBlockGeometry.Stabilize(default, observed));
        Assert.Equal(default, LiveBlockGeometry.Stabilize(observed, default));
    }
}