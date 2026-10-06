using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

public sealed record KnownTextReference(int TextRgb, int BackgroundRgb, long TextPixels)
{
    public const int CorePercent = 50;
    public const long MinimumTextPixels = 16;

    public bool CanCheckAbsence => KnownTextAbsenceProbe.CanCheck(TextRgb, BackgroundRgb);

    public bool CanConfirmPresence => CanCheckAbsence && TextPixels >= MinimumTextPixels;

    public bool IsPresent(long currentTextPixels) =>
        CanConfirmPresence && currentTextPixels * 2 >= TextPixels && currentTextPixels <= TextPixels * 2;

    public static KnownTextReference FromSample(OcrBitmap frame, RectPx sampleBox, int textRgb, int backgroundRgb, double pixelScale)
    {
        var count = CountCorePixels(frame, sampleBox, textRgb, backgroundRgb);
        var scaled = count < 0 ? 0 : (long)Math.Round(count * pixelScale * pixelScale);
        return new KnownTextReference(textRgb, backgroundRgb, scaled);
    }

    public static long CountCorePixels(OcrBitmap frame, RectPx box, int textRgb, int backgroundRgb)
    {
        if (!KnownTextAbsenceProbe.CanCheck(textRgb, backgroundRgb)
            || frame is null || frame.PixelsBgra32 is null
            || frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0
            || (long)frame.Width * 4 > frame.Stride
            || (long)frame.Stride * frame.Height > frame.PixelsBgra32.Length
            || box.X < 0 || box.Y < 0 || box.Width <= 0 || box.Height <= 0
            || (long)box.X + box.Width > frame.Width
            || (long)box.Y + box.Height > frame.Height)
            return -1;
        var counter = new Counter(textRgb, backgroundRgb);
        var rowBytes = box.Width * 4;
        for (var y = box.Y; y < box.Bottom; y++)
        {
            var offset = (int)((long)y * frame.Stride + (long)box.X * 4);
            counter.ObserveBgra32(frame.PixelsBgra32.AsSpan(offset, rowBytes));
        }
        return counter.Invalid ? -1 : counter.TextPixels;
    }

    public sealed class Counter
    {
        private readonly bool _textIsBrighter;
        private readonly int _threshold;

        public Counter(int textRgb, int backgroundRgb)
        {
            CanCount = KnownTextAbsenceProbe.CanCheck(textRgb, backgroundRgb);
            if (!CanCount) return;
            var text = Luminance(textRgb);
            var background = Luminance(backgroundRgb);
            _textIsBrighter = text > background;
            _threshold = background + (text - background) * CorePercent / 100;
        }

        public bool CanCount { get; }
        public bool Invalid { get; private set; }
        public long TextPixels { get; private set; }

        public void ObserveBgra32(ReadOnlySpan<byte> contiguousPixels)
        {
            if (!CanCount || Invalid) return;
            if (contiguousPixels.Length % 4 != 0)
            {
                Invalid = true;
                return;
            }
            for (var offset = 0; offset < contiguousPixels.Length; offset += 4)
            {
                var luminance = 114 * contiguousPixels[offset] + 587 * contiguousPixels[offset + 1]
                    + 299 * contiguousPixels[offset + 2];
                if (_textIsBrighter ? luminance >= _threshold : luminance <= _threshold) TextPixels++;
            }
        }
    }

    private static int Luminance(int rgb) =>
        299 * ((rgb >> 16) & 255) + 587 * ((rgb >> 8) & 255) + 114 * (rgb & 255);
}
