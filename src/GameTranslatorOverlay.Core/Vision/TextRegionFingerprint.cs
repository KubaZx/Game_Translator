using System.Security.Cryptography;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Exact RGB evidence for a complete, nonuniform source region. Stores only a digest,
/// never its image. Alpha and row padding are irrelevant; every RGB pixel matters.
/// </summary>
public sealed class TextRegionFingerprint
{
    public const int MaximumPixels = 262144;
    private readonly byte[] _digest;
    private readonly int _width, _height;

    private TextRegionFingerprint(int width, int height, byte[] digest) =>
        (_width, _height, _digest) = (width, height, digest);

    public bool Matches(TextRegionFingerprint? other) => other is not null
        && _width == other._width && _height == other._height
        && _digest.AsSpan().SequenceEqual(other._digest);

    public static bool CanSample(int width, int height) => width > 0 && height > 0
        && (long)width * height is >= 4 and <= MaximumPixels;

    public static TextRegionFingerprint? FromBitmap(OcrBitmap frame, RectPx box)
    {
        if (frame is null || frame.PixelsBgra32 is null || frame.Width <= 0 || frame.Height <= 0
            || frame.Stride <= 0 || (long)frame.Width * 4 > frame.Stride
            || (long)frame.Stride * frame.Height > frame.PixelsBgra32.Length
            || !CanSample(box.Width, box.Height) || box.X < 0 || box.Y < 0
            || (long)box.X + box.Width > frame.Width || (long)box.Y + box.Height > frame.Height)
            return null;
        using var builder = new Builder(box.Width, box.Height);
        for (var y = box.Y; y < box.Bottom; y++)
        {
            var offset = (int)((long)y * frame.Stride + (long)box.X * 4);
            builder.AppendBgra32(frame.PixelsBgra32.AsSpan(offset, box.Width * 4));
        }
        return builder.Finish();
    }

    /// <summary>Feeds complete rows/chunks without allocating a full region copy.</summary>
    public sealed class Builder : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly TextPresenceProbe _contrast = new();
        private readonly int _width, _height;
        private long _pixels;
        private bool _invalid, _finished;

        public Builder(int width, int height)
        {
            (_width, _height) = (width, height);
            _invalid = !CanSample(width, height);
        }

        public void AppendBgra32(ReadOnlySpan<byte> pixels)
        {
            if (_finished || _invalid) return;
            if (pixels.Length % 4 != 0 || _pixels + pixels.Length / 4 > (long)_width * _height)
            {
                _invalid = true;
                return;
            }
            _pixels += pixels.Length / 4;
            _contrast.ObserveBgra32(pixels);
            Span<byte> rgb = stackalloc byte[768];
            while (!pixels.IsEmpty)
            {
                var count = Math.Min(pixels.Length / 4, rgb.Length / 3);
                for (var i = 0; i < count; i++)
                {
                    rgb[i * 3] = pixels[i * 4];
                    rgb[i * 3 + 1] = pixels[i * 4 + 1];
                    rgb[i * 3 + 2] = pixels[i * 4 + 2];
                }
                _hash.AppendData(rgb[..(count * 3)]);
                pixels = pixels[(count * 4)..];
            }
        }

        public TextRegionFingerprint? Finish()
        {
            if (_finished) return null;
            _finished = true;
            return _invalid || _pixels != (long)_width * _height || !_contrast.HasContrast
                ? null : new TextRegionFingerprint(_width, _height, _hash.GetHashAndReset());
        }

        public void Dispose()
        {
            _finished = true;
            _hash.Dispose();
        }
    }
}
