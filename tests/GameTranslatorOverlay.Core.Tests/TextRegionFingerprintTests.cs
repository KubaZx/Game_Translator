using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class TextRegionFingerprintTests
{
    private static OcrBitmap Image(int width = 8, int height = 4, int padding = 0)
    {
        var stride = width * 4 + padding;
        var pixels = new byte[stride * height];
        // One thin edge stroke suffices; no subsampling may skip it.
        pixels[(height - 1) * stride + (width - 1) * 4] = 220;
        return new OcrBitmap(pixels, width, height, stride);
    }
    private static RectPx All(OcrBitmap frame) => new(0, 0, frame.Width, frame.Height);
    private static TextRegionFingerprint Fingerprint(OcrBitmap frame) =>
        Assert.IsType<TextRegionFingerprint>(TextRegionFingerprint.FromBitmap(frame, All(frame)));

    [Fact]
    public void Identical_rgb_and_complete_edge_stroke_match()
    {
        var image = Image();
        Assert.True(Fingerprint(image).Matches(Fingerprint(Image())));
    }

    [Fact]
    public void Alpha_and_stride_padding_do_not_change_evidence()
    {
        var first = Image();
        var second = Image(padding: 16);
        for (var y = 0; y < second.Height; y++)
        {
            for (var x = 0; x < second.Width; x++) second.PixelsBgra32[y * second.Stride + x * 4 + 3] = 255;
            second.PixelsBgra32.AsSpan(y * second.Stride + second.Width * 4, 16).Fill(123);
        }
        Assert.True(Fingerprint(first).Matches(Fingerprint(second)));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(7, 0, 1)]
    [InlineData(0, 3, 2)]
    [InlineData(7, 3, 0)]
    public void One_rgb_level_at_any_edge_invalidates_snapshot(int x, int y, int channel)
    {
        var image = Image();
        var reference = Fingerprint(image);
        image.PixelsBgra32[y * image.Stride + x * 4 + channel]++;
        Assert.False(reference.Matches(Fingerprint(image)));
    }

    [Fact]
    public void Only_the_complete_selected_region_is_compared()
    {
        var image = Image();
        var box = new RectPx(4, 2, 4, 2);
        var reference = Assert.IsType<TextRegionFingerprint>(TextRegionFingerprint.FromBitmap(image, box));
        image.PixelsBgra32[0] = 255;
        Assert.True(reference.Matches(TextRegionFingerprint.FromBitmap(image, box)));
        image.PixelsBgra32[3 * image.Stride + 7 * 4] = 0;
        Assert.False(reference.Matches(TextRegionFingerprint.FromBitmap(image, box)));
    }

    [Theory]
    [InlineData(-1, 0, 8, 4)]
    [InlineData(0, -1, 8, 4)]
    [InlineData(0, 0, 0, 4)]
    [InlineData(0, 0, 8, -1)]
    [InlineData(0, 0, 9, 4)]
    [InlineData(0, 0, 8, 5)]
    [InlineData(0, 0, 3, 1)]
    [InlineData(1, 0, int.MaxValue, 4)]
    public void Invalid_or_clipped_boxes_are_inconclusive(int x, int y, int width, int height) =>
        Assert.Null(TextRegionFingerprint.FromBitmap(Image(), new RectPx(x, y, width, height)));

    [Fact]
    public void Malformed_frame_storage_never_becomes_proof()
    {
        var image = Image();
        Assert.Null(TextRegionFingerprint.FromBitmap(image with { Stride = 1 }, All(image)));
        Assert.Null(TextRegionFingerprint.FromBitmap(image with { PixelsBgra32 = new byte[127] }, All(image)));
        Assert.Null(TextRegionFingerprint.FromBitmap(image with { Width = int.MaxValue }, All(image)));
        Assert.Null(TextRegionFingerprint.FromBitmap(image with { Height = int.MaxValue }, All(image)));
        Assert.Null(TextRegionFingerprint.FromBitmap(image with { Stride = -32 }, All(image)));
        Assert.Null(TextRegionFingerprint.FromBitmap(null!, All(image)));
    }

    [Fact]
    public void Uniform_and_nearly_uniform_regions_cannot_protect_text()
    {
        var image = Image();
        Array.Clear(image.PixelsBgra32);
        Assert.Null(TextRegionFingerprint.FromBitmap(image, All(image)));
        image.PixelsBgra32[^4] = 16;
        Assert.Null(TextRegionFingerprint.FromBitmap(image, All(image)));
    }

    [Fact]
    public void Chunk_boundaries_do_not_change_digest()
    {
        var image = Image(600, 2);
        using var builder = new TextRegionFingerprint.Builder(image.Width, image.Height);
        for (var offset = 0; offset < image.PixelsBgra32.Length; offset += 4)
            builder.AppendBgra32(image.PixelsBgra32.AsSpan(offset, 4));
        Assert.True(Fingerprint(image).Matches(builder.Finish()));
        Assert.Null(builder.Finish());
    }

    [Fact]
    public void Same_bytes_with_different_geometry_do_not_match()
    {
        var image = Image();
        Assert.False(Fingerprint(image).Matches(Fingerprint(image with { Width = 4, Height = 8, Stride = 16 })));
        Assert.False(Fingerprint(image).Matches(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(124)]
    [InlineData(129)]
    [InlineData(132)]
    public void Incomplete_partial_pixel_or_extra_input_is_inconclusive(int length)
    {
        using var builder = new TextRegionFingerprint.Builder(8, 4);
        var data = new byte[length];
        if (length > 0) data[0] = 255;
        builder.AppendBgra32(data);
        Assert.Null(builder.Finish());
    }

    [Fact]
    public void Malformed_input_cannot_be_repaired_by_valid_rows()
    {
        using var builder = new TextRegionFingerprint.Builder(8, 4);
        builder.AppendBgra32(new byte[1]);
        builder.AppendBgra32(Image().PixelsBgra32);
        Assert.Null(builder.Finish());
    }

    [Fact]
    public void Work_is_bounded_and_disposal_prevents_reuse()
    {
        Assert.True(TextRegionFingerprint.CanSample(512, 512));
        Assert.False(TextRegionFingerprint.CanSample(513, 512));
        Assert.False(TextRegionFingerprint.CanSample(int.MaxValue, int.MaxValue));
        using var oversized = new TextRegionFingerprint.Builder(513, 512);
        oversized.AppendBgra32(Image().PixelsBgra32);
        Assert.Null(oversized.Finish());
        var disposed = new TextRegionFingerprint.Builder(8, 4);
        disposed.Dispose();
        disposed.AppendBgra32(Image().PixelsBgra32);
        Assert.Null(disposed.Finish());
    }
}
