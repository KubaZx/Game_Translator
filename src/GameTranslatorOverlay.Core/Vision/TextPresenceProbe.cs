using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Conservative evidence that a complete old text box has lost its visible RGB
/// contrast. A nonuniform or incomplete sample is inconclusive, not proof of text.
/// Create one probe per box and feed complete BGRA pixels, including every row.
/// </summary>
public sealed class TextPresenceProbe
{
    private const int MaximumUniformRange = 16;
    private const int MinimumKnownContrast = 48;
    private int _minimumBlue = 255, _minimumGreen = 255, _minimumRed = 255;
    private int _maximumBlue, _maximumGreen, _maximumRed;
    private int _sampledPixels;
    private bool _invalidInput;

    public bool HasContrast { get; private set; }
    public bool IsUniform => !_invalidInput && _sampledPixels >= 4 && !HasContrast;

    /// <summary>
    /// Accumulates one contiguous row or other complete BGRA chunk. Alpha is ignored.
    /// Every pixel is examined until any RGB channel spans more than 16 levels;
    /// sparse or thin letters must not disappear between sampling points.
    /// A partial pixel makes the accumulated uniformity evidence unusable.
    /// </summary>
    public void ObserveBgra32(ReadOnlySpan<byte> contiguousPixels)
    {
        if (contiguousPixels.Length % 4 != 0)
        {
            _invalidInput = true;
            return;
        }
        if (_invalidInput || HasContrast) return;

        for (var offset = 0; offset < contiguousPixels.Length; offset += 4)
        {
            var blue = contiguousPixels[offset];
            var green = contiguousPixels[offset + 1];
            var red = contiguousPixels[offset + 2];
            _minimumBlue = Math.Min(_minimumBlue, blue);
            _minimumGreen = Math.Min(_minimumGreen, green);
            _minimumRed = Math.Min(_minimumRed, red);
            _maximumBlue = Math.Max(_maximumBlue, blue);
            _maximumGreen = Math.Max(_maximumGreen, green);
            _maximumRed = Math.Max(_maximumRed, red);
            if (_sampledPixels < 4) _sampledPixels++;

            if (_maximumBlue - _minimumBlue > MaximumUniformRange
                || _maximumGreen - _minimumGreen > MaximumUniformRange
                || _maximumRed - _minimumRed > MaximumUniformRange)
            {
                HasContrast = true;
                return;
            }
        }
    }

    /// <summary>
    /// The previous text/background must be known packed RGB values and differ
    /// by at least 48 in one channel. Unknown or weak previous contrast cannot
    /// justify removing a block just because its current box looks uniform.
    /// </summary>
    public static bool CanCheckKnownText(int textRgb, int bgRgb)
    {
        if ((uint)textRgb > 0xFFFFFFu || (uint)bgRgb > 0xFFFFFFu) return false;
        return Math.Abs((textRgb & 255) - (bgRgb & 255)) >= MinimumKnownContrast
            || Math.Abs(((textRgb >> 8) & 255) - ((bgRgb >> 8) & 255)) >= MinimumKnownContrast
            || Math.Abs(((textRgb >> 16) & 255) - ((bgRgb >> 16) & 255)) >= MinimumKnownContrast;
    }

    /// <summary>
    /// True only for a complete box of at least four uniform pixels whose old text
    /// had known contrast. Never clips a box: an unseen edge could still contain
    /// a one-pixel stroke. Invalid dimensions, storage, stride or colors return false.
    /// Row padding and alpha are excluded; all RGB pixels in the box are examined.
    /// </summary>
    public static bool IsClearlyEmpty(OcrBitmap frame, RectPx sampleBox, int textRgb, int bgRgb)
    {
        if (!CanCheckKnownText(textRgb, bgRgb)
            || frame is null || frame.PixelsBgra32 is null
            || frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
            return false;

        // Use wide arithmetic before bounds checks so malformed dimensions or an
        // overflowing rectangle cannot turn a clipped region into valid evidence.
        if ((long)frame.Width * 4 > frame.Stride
            || (long)frame.Stride * frame.Height > frame.PixelsBgra32.Length
            || sampleBox.X < 0 || sampleBox.Y < 0 || sampleBox.Width <= 0 || sampleBox.Height <= 0
            || (long)sampleBox.X + sampleBox.Width > frame.Width
            || (long)sampleBox.Y + sampleBox.Height > frame.Height
            || (long)sampleBox.Width * sampleBox.Height < 4)
            return false;

        var probe = new TextPresenceProbe();
        var rowBytes = sampleBox.Width * 4;
        var bottom = sampleBox.Y + sampleBox.Height;
        for (var y = sampleBox.Y; y < bottom; y++)
        {
            var offset = (int)((long)y * frame.Stride + (long)sampleBox.X * 4);
            probe.ObserveBgra32(frame.PixelsBgra32.AsSpan(offset, rowBytes));
            if (probe.HasContrast) return false;
        }
        return probe.IsUniform;
    }
}