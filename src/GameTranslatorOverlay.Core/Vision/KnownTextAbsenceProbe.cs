using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

public sealed class KnownTextAbsenceProbe
{
    public const int MinimumLuminanceContrast = 48;
    public const int RemainingContrastPercent = 25;
    private const int AllowedTextPixelsPerMille = 1;
    private readonly bool _textIsBrighter;
    private readonly int _threshold;
    private readonly long _expectedPixels;
    private readonly long _allowedTextPixels;
    private long _pixels;
    private long _textPixels;
    private bool _invalidInput;

    public KnownTextAbsenceProbe(int textRgb, int backgroundRgb, long expectedPixels)
    {
        CanDecide = CanCheck(textRgb, backgroundRgb) && expectedPixels >= 4;
        if (!CanDecide) return;
        var text = Luminance(textRgb);
        var background = Luminance(backgroundRgb);
        _textIsBrighter = text > background;
        _threshold = background + (text - background) * RemainingContrastPercent / 100;
        _expectedPixels = expectedPixels;
        _allowedTextPixels = expectedPixels * AllowedTextPixelsPerMille / 1000;
    }

    public bool CanDecide { get; }

    public long TextPixels => _textPixels;

    public bool HasTextPixels => _textPixels > _allowedTextPixels;

    public bool IsTextAbsent => CanDecide && !_invalidInput && _pixels == _expectedPixels && !HasTextPixels;

    public void ObserveBgra32(ReadOnlySpan<byte> contiguousPixels)
    {
        if (!CanDecide || _invalidInput || HasTextPixels) return;
        if (contiguousPixels.Length % 4 != 0 || _pixels + contiguousPixels.Length / 4 > _expectedPixels)
        {
            _invalidInput = true;
            return;
        }
        _pixels += contiguousPixels.Length / 4;
        for (var offset = 0; offset < contiguousPixels.Length; offset += 4)
        {
            var luminance = 114 * contiguousPixels[offset] + 587 * contiguousPixels[offset + 1]
                + 299 * contiguousPixels[offset + 2];
            if (_textIsBrighter ? luminance < _threshold : luminance > _threshold) continue;
            _textPixels++;
            if (HasTextPixels) return;
        }
    }

    public static bool CanCheck(int textRgb, int backgroundRgb) =>
        (uint)textRgb <= 0xFFFFFFu && (uint)backgroundRgb <= 0xFFFFFFu
        && Math.Abs(Luminance(textRgb) - Luminance(backgroundRgb)) >= MinimumLuminanceContrast * 1000;

    public static bool IsKnownTextAbsent(OcrBitmap frame, RectPx box, int textRgb, int backgroundRgb)
    {
        if (!CanCheck(textRgb, backgroundRgb)
            || frame is null || frame.PixelsBgra32 is null
            || frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0
            || (long)frame.Width * 4 > frame.Stride
            || (long)frame.Stride * frame.Height > frame.PixelsBgra32.Length
            || box.X < 0 || box.Y < 0 || box.Width <= 0 || box.Height <= 0
            || (long)box.X + box.Width > frame.Width
            || (long)box.Y + box.Height > frame.Height)
            return false;

        var probe = new KnownTextAbsenceProbe(textRgb, backgroundRgb, (long)box.Width * box.Height);
        if (!probe.CanDecide) return false;
        var rowBytes = box.Width * 4;
        for (var y = box.Y; y < box.Bottom; y++)
        {
            var offset = (int)((long)y * frame.Stride + (long)box.X * 4);
            probe.ObserveBgra32(frame.PixelsBgra32.AsSpan(offset, rowBytes));
            if (probe.HasTextPixels) return false;
        }
        return probe.IsTextAbsent;
    }

    private static int Luminance(int rgb) =>
        299 * ((rgb >> 16) & 255) + 587 * ((rgb >> 8) & 255) + 114 * (rgb & 255);
}
