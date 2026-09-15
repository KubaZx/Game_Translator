using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class TextPresenceProbeTests
{
    [Fact]
    public void An_empty_probe_has_no_contrast_but_cannot_prove_that_text_is_absent()
    {
        var probe = new TextPresenceProbe();

        Assert.False(probe.HasContrast);
        Assert.False(probe.IsUniform);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Uniformity_requires_at_least_four_pixels(int pixelCount, bool expectedUniform)
    {
        var frame = MakeBitmap(pixelCount, 1, 0x808080);
        var probe = new TextPresenceProbe();

        probe.ObserveBgra32(frame.PixelsBgra32);

        Assert.False(probe.HasContrast);
        Assert.Equal(expectedUniform, probe.IsUniform);
    }

    [Fact]
    public void Sixteen_levels_of_noise_per_channel_are_uniform_and_alpha_is_ignored()
    {
        var frame = MakeBitmap(4, 1, 0x5080C8);
        SetPixel(frame, 0, 0, 0x5080C8, 0);
        SetPixel(frame, 1, 0, 0x6090D8, 255);
        SetPixel(frame, 2, 0, 0x5888D0, 17);
        SetPixel(frame, 3, 0, 0x5F81C9, 128);
        var probe = new TextPresenceProbe();

        probe.ObserveBgra32(frame.PixelsBgra32);

        Assert.False(probe.HasContrast);
        Assert.True(probe.IsUniform);
    }

    [Theory]
    [InlineData(0x614080)]
    [InlineData(0x505180)]
    [InlineData(0x504091)]
    public void Seventeen_levels_in_any_single_rgb_channel_count_as_contrast(int contrastingRgb)
    {
        var frame = MakeBitmap(4, 1, 0x504080);
        SetPixel(frame, 3, 0, contrastingRgb);
        var probe = new TextPresenceProbe();

        probe.ObserveBgra32(frame.PixelsBgra32);

        Assert.True(probe.HasContrast);
        Assert.False(probe.IsUniform);
    }

    [Fact]
    public void Separately_uniform_rows_contribute_to_one_global_rgb_range()
    {
        var probe = new TextPresenceProbe();
        probe.ObserveBgra32(MakeBitmap(4, 1, 0x404040).PixelsBgra32);
        Assert.True(probe.IsUniform);

        probe.ObserveBgra32(MakeBitmap(4, 1, 0x515151).PixelsBgra32);

        Assert.True(probe.HasContrast);
        Assert.False(probe.IsUniform);
    }

    [Fact]
    public void An_incomplete_bgra_pixel_permanently_prevents_uniformity_without_throwing()
    {
        var probe = new TextPresenceProbe();
        var validPixels = MakeBitmap(4, 1, 0x808080).PixelsBgra32;
        probe.ObserveBgra32(validPixels);
        Assert.True(probe.IsUniform);

        probe.ObserveBgra32(new byte[] { 0x80 });
        Assert.False(probe.IsUniform);

        probe.ObserveBgra32(validPixels);
        Assert.False(probe.IsUniform);
        Assert.False(probe.HasContrast);
    }

    [Theory]
    [InlineData(0x2F0000, false)]
    [InlineData(0x300000, true)]
    public void Known_text_needs_at_least_forty_eight_levels_in_one_channel(int textRgb, bool expected)
    {
        Assert.Equal(expected, TextPresenceProbe.CanCheckKnownText(textRgb, 0));
        Assert.Equal(expected, TextPresenceProbe.CanCheckKnownText(0, textRgb));
    }

    [Fact]
    public void Negative_or_out_of_range_packed_colors_are_unknown()
    {
        foreach (var unknown in new[] { -1, int.MinValue, 0x1000000, int.MaxValue })
        {
            Assert.False(TextPresenceProbe.CanCheckKnownText(unknown, 0));
            Assert.False(TextPresenceProbe.CanCheckKnownText(0, unknown));
        }
    }

    [Fact]
    public void A_uniform_crop_can_be_empty_even_when_its_background_color_has_changed()
    {
        var frame = MakeBitmap(8, 4, 0x707070);

        Assert.True(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(1, 1, 6, 2), 0xFFFFFF, 0x202020));
    }

    [Theory]
    [InlineData(0x404040, 0xFFFFFF, 512, 64)]
    [InlineData(0xE0E0E0, 0x101010, 511, 63)]
    [InlineData(0x404040, 0x404080, 251, 31)]
    public void A_single_white_dark_or_colored_glyph_pixel_anywhere_in_a_large_box_prevents_empty_detection(
        int backgroundRgb, int textRgb, int textX, int textY)
    {
        var frame = MakeBitmap(513, 65, backgroundRgb);
        SetPixel(frame, textX, textY, textRgb);

        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 513, 65), textRgb, backgroundRgb));
    }

    [Fact]
    public void Hover_background_changes_do_not_erase_text_that_is_still_present()
    {
        var frame = MakeBitmap(16, 4, 0x707070);
        for (var x = 3; x < 12; x += 2)
        {
            SetPixel(frame, x, 1, 0xFFFFFF);
            SetPixel(frame, x, 2, 0xFFFFFF);
        }

        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 16, 4), 0xFFFFFF, 0x303030));
    }

    [Fact]
    public void Cropped_outside_empty_negative_and_overflowing_boxes_are_rejected_without_clipping()
    {
        var frame = MakeBitmap(4, 4, 0x808080);
        var invalidBoxes = new[]
        {
            new RectPx(-1, 0, 2, 2),
            new RectPx(3, 0, 2, 2),
            new RectPx(0, -1, 2, 2),
            new RectPx(0, 3, 2, 2),
            new RectPx(4, 0, 2, 2),
            new RectPx(0, 4, 2, 2),
            new RectPx(int.MaxValue, 0, 2, 2),
            new RectPx(2, 0, int.MaxValue, 2),
            new RectPx(0, int.MaxValue, 2, 2),
            new RectPx(0, 2, 2, int.MaxValue),
            new RectPx(0, 0, 0, 2),
            new RectPx(0, 0, 2, 0),
            new RectPx(0, 0, -1, 2),
            new RectPx(0, 0, 2, -1)
        };

        foreach (var box in invalidBoxes)
            Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, box, 0xFFFFFF, 0), $"Invalid box was accepted: {box}");
    }

    [Fact]
    public void Malformed_bitmaps_and_incomplete_last_stride_are_rejected_without_throwing()
    {
        var invalidFrames = new[]
        {
            new OcrBitmap(Array.Empty<byte>(), 0, 0, 0),
            new OcrBitmap(Array.Empty<byte>(), 2, 2, 8),
            new OcrBitmap(null!, 2, 2, 8),
            new OcrBitmap(new byte[16], 2, 2, 0),
            new OcrBitmap(new byte[16], 2, 2, -8),
            new OcrBitmap(new byte[16], 2, 2, 7),
            // All visible pixels fit, but the final row's padding is incomplete.
            new OcrBitmap(new byte[23], 2, 2, 12),
            new OcrBitmap(new byte[16], -2, 2, 8),
            new OcrBitmap(new byte[16], 2, -2, 8),
            new OcrBitmap(Array.Empty<byte>(), int.MaxValue, 2, int.MaxValue),
            new OcrBitmap(new byte[16], 2, int.MaxValue, 8)
        };

        foreach (var frame in invalidFrames)
            Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 2, 2), 0xFFFFFF, 0));
    }

    [Fact]
    public void The_crop_itself_must_supply_at_least_four_pixels_across_its_rows()
    {
        var frame = MakeBitmap(4, 4, 0x808080);

        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 3, 1), 0xFFFFFF, 0));
        Assert.True(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 4, 1), 0xFFFFFF, 0));
        Assert.True(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 2, 2), 0xFFFFFF, 0));
    }

    [Fact]
    public void Stride_padding_and_alpha_changes_do_not_create_text_contrast()
    {
        var frame = MakeBitmap(2, 2, 0x4080C0, paddingBytes: 5);
        SetPixel(frame, 0, 0, 0x4080C0, 0);
        SetPixel(frame, 1, 0, 0x4080C0, 255);
        SetPixel(frame, 0, 1, 0x4080C0, 1);
        SetPixel(frame, 1, 1, 0x4080C0, 254);
        for (var y = 0; y < frame.Height; y++)
            for (var i = frame.Width * 4; i < frame.Stride; i++)
                frame.PixelsBgra32[y * frame.Stride + i] = i % 2 == 0 ? (byte)0 : (byte)255;

        Assert.True(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(0, 0, 2, 2), 0xFFFFFF, 0));
    }

    [Fact]
    public void Contrast_outside_the_requested_box_does_not_prevent_empty_detection_inside_it()
    {
        var frame = MakeBitmap(8, 4, 0x808080);
        SetPixel(frame, 0, 0, 0);
        SetPixel(frame, 7, 3, 0xFFFFFF);

        Assert.True(TextPresenceProbe.IsClearlyEmpty(frame, new RectPx(2, 1, 4, 2), 0xFFFFFF, 0));
    }

    [Fact]
    public void Empty_observations_do_not_discard_valid_pixels_from_other_rows()
    {
        var probe = new TextPresenceProbe();
        probe.ObserveBgra32(Array.Empty<byte>());
        Assert.False(probe.IsUniform);
        probe.ObserveBgra32(MakeBitmap(2, 1, 0x808080).PixelsBgra32);
        Assert.False(probe.IsUniform);
        probe.ObserveBgra32(Array.Empty<byte>());
        probe.ObserveBgra32(MakeBitmap(2, 1, 0x808080).PixelsBgra32);
        probe.ObserveBgra32(Array.Empty<byte>());

        Assert.True(probe.IsUniform);
        Assert.False(probe.HasContrast);
    }

    [Fact]
    public void Uniform_pixels_are_inconclusive_when_original_text_colors_are_unknown_or_low_contrast()
    {
        var frame = MakeBitmap(4, 4, 0x808080);
        var box = new RectPx(0, 0, 4, 4);

        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, box, -1, 0));
        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, box, 0xFFFFFF, -1));
        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, box, 0x1000000, 0));
        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, box, 0x808080, 0xAFAFAF));
        Assert.True(TextPresenceProbe.IsClearlyEmpty(frame, box, 0x808080, 0xB08080));
    }

    private static OcrBitmap MakeBitmap(int width, int height, int rgb, int paddingBytes = 0)
    {
        var stride = checked(width * 4 + paddingBytes);
        var frame = new OcrBitmap(new byte[checked(stride * height)], width, height, stride);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                SetPixel(frame, x, y, rgb);
        return frame;
    }

    private static void SetPixel(OcrBitmap frame, int x, int y, int rgb, byte alpha = 255)
    {
        var offset = checked(y * frame.Stride + x * 4);
        frame.PixelsBgra32[offset] = (byte)(rgb & 0xFF);
        frame.PixelsBgra32[offset + 1] = (byte)((rgb >> 8) & 0xFF);
        frame.PixelsBgra32[offset + 2] = (byte)((rgb >> 16) & 0xFF);
        frame.PixelsBgra32[offset + 3] = alpha;
    }
}