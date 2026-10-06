using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.OverlayPreview;

internal sealed class Pixels
{
    public Pixels(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public int Stride => Width * 4;

    public static Pixels FromOpaque(OcrBitmap bitmap)
    {
        var result = new Pixels(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
        {
            Buffer.BlockCopy(bitmap.PixelsBgra32, y * bitmap.Stride, result.Data, y * result.Stride, result.Stride);
        }
        for (var i = 3; i < result.Data.Length; i += 4) result.Data[i] = 255;
        return result;
    }

    public static Pixels FromSource(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var result = new Pixels(converted.PixelWidth, converted.PixelHeight);
        converted.CopyPixels(result.Data, result.Stride, 0);
        return result;
    }

    public BitmapSource ToSource()
    {
        var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, Data, Stride);
        source.Freeze();
        return source;
    }

    public void SavePng(string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToSource()));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public Pixels CompositeOver(Pixels background)
    {
        if (background.Width != Width || background.Height != Height) throw new ArgumentException("Różne wymiary warstw.");
        var result = new Pixels(Width, Height);
        for (var i = 0; i < Data.Length; i += 4)
        {
            var inverse = 255 - Data[i + 3];
            for (var c = 0; c < 3; c++)
            {
                result.Data[i + c] = (byte)Math.Min(255, Data[i + c] + (background.Data[i + c] * inverse + 127) / 255);
            }
            result.Data[i + 3] = (byte)Math.Min(255, Data[i + 3] + (background.Data[i + 3] * inverse + 127) / 255);
        }
        return result;
    }

    public Pixels Crop(RectPx area)
    {
        var clipped = area.Intersect(new RectPx(0, 0, Width, Height));
        var result = new Pixels(Math.Max(1, clipped.Width), Math.Max(1, clipped.Height));
        if (clipped.IsEmpty) return result;
        for (var y = 0; y < clipped.Height; y++)
        {
            Buffer.BlockCopy(Data, (clipped.Y + y) * Stride + clipped.X * 4, result.Data, y * result.Stride, clipped.Width * 4);
        }
        return result;
    }

    public Pixels ScaleNearest(int factor)
    {
        if (factor <= 1) return this;
        var result = new Pixels(Width * factor, Height * factor);
        for (var y = 0; y < result.Height; y++)
        {
            var sourceRow = y / factor * Stride;
            var targetRow = y * result.Stride;
            for (var x = 0; x < result.Width; x++)
            {
                Buffer.BlockCopy(Data, sourceRow + x / factor * 4, result.Data, targetRow + x * 4, 4);
            }
        }
        return result;
    }

    public Pixels HalfSize()
    {
        var result = new Pixels(Math.Max(1, Width / 2), Math.Max(1, Height / 2));
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var sum = Data[(2 * y) * Stride + (2 * x) * 4 + c] + Data[(2 * y) * Stride + (2 * x + 1) * 4 + c]
                        + Data[(2 * y + 1) * Stride + (2 * x) * 4 + c] + Data[(2 * y + 1) * Stride + (2 * x + 1) * 4 + c];
                    result.Data[y * result.Stride + x * 4 + c] = (byte)((sum + 2) / 4);
                }
            }
        }
        return result;
    }
}
