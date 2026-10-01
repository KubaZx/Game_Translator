using System.IO.Hashing;
using System.Runtime.Intrinsics;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Exact RGB evidence for a complete, nonuniform source region. Stores only a digest,
/// never its image. Alpha and row padding are irrelevant; every RGB pixel matters.
/// </summary>
/// <remarks>
/// Skrót to 128-bitowy XxHash128 (niekryptograficzny), a nie SHA-256: odcisk porównuje
/// tylko dwie klatki tej samej gry w jednej sesji, nikt nie dobiera pikseli pod kolizję,
/// a 128 bitów daje szansę przypadkowej kolizji rzędu 2^-128 — tyle co nic. SHA-256
/// kosztował ok. 1 ms na region 512×512 przy każdym sprawdzeniu statycznego menu.
/// Skrót nigdy nie jest zapisywany na dysk, więc zmiana algorytmu nie unieważnia danych.
/// </remarks>
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
        // Bufor na stosie: 256 pikseli na porcję skrótu.
        private const int ChunkBytes = 1024;
        private readonly XxHash128 _hash = new();
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

            // Skrót liczymy z pikseli BGRA z wyzerowanym kanałem alfa zamiast z upakowanego RGB:
            // ta sama informacja (każdy bajt B, G, R wchodzi do skrótu w tej samej kolejności,
            // alfa zawsze jako 0), a maskowanie całymi wektorami jest wielokrotnie tańsze niż
            // przepisywanie pojedynczych bajtów. Granice porcji nie zmieniają skrótu strumienia.
            Span<byte> masked = stackalloc byte[ChunkBytes];
            while (!pixels.IsEmpty)
            {
                var count = Math.Min(pixels.Length, ChunkBytes);
                MaskAlpha(pixels[..count], masked);
                _hash.Append(masked[..count]);
                pixels = pixels[count..];
            }
        }

        private static readonly Vector128<byte> AlphaMask = Vector128.Create(
            (byte)255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0);

        private static void MaskAlpha(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var index = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                // Maska bajtowa (B, G, R zostają, A = 0) nie zależy od kolejności bajtów w słowie.
                for (; index <= source.Length - Vector128<byte>.Count; index += Vector128<byte>.Count)
                {
                    (Vector128.Create(source.Slice(index, Vector128<byte>.Count)) & AlphaMask)
                        .CopyTo(destination.Slice(index, Vector128<byte>.Count));
                }
            }
            for (; index < source.Length; index += 4)
            {
                destination[index] = source[index];
                destination[index + 1] = source[index + 1];
                destination[index + 2] = source[index + 2];
                destination[index + 3] = 0;
            }
        }

        public TextRegionFingerprint? Finish()
        {
            if (_finished) return null;
            _finished = true;
            return _invalid || _pixels != (long)_width * _height || !_contrast.HasContrast
                ? null : new TextRegionFingerprint(_width, _height, _hash.GetHashAndReset());
        }

        public void Dispose() => _finished = true;
    }
}
