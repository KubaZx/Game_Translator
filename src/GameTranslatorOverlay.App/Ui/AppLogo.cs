using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameTranslatorOverlay.App.Ui;

public static class AppLogo
{
    private const double Canvas = 64;

    public static Color AccentStart { get; } = Color.FromRgb(0x7C, 0x5C, 0xFF);

    public static Color AccentEnd { get; } = Color.FromRgb(0x22, 0xD3, 0xEE);

    public static DrawingImage Image { get; } = CreateImage();

    public static DrawingGroup CreateDrawing()
    {
        var bubble = BubbleGeometry();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Canvas, Canvas))));
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(AccentStart, AccentEnd, new Point(0.05, 0.1), new Point(0.95, 0.95)), null, bubble));
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(
            Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), new Point(0.5, 0), new Point(0.5, 0.62)), null, bubble));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, LettersGeometry()));
        group.Freeze();
        return group;
    }

    public static BitmapSource Render(int pixels)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / Canvas, pixels / Canvas));
            dc.DrawDrawing(CreateDrawing());
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapFrame CreateWindowIcon()
    {
        var decoder = new IconBitmapDecoder(new MemoryStream(CreateIco(16, 20, 24, 32, 40, 48, 64, 256)),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    public static IntPtr CreateIconHandle(int pixels)
    {
        var straight = StraightPixels(Render(pixels), out var stride);
        using var bitmap = new System.Drawing.Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, pixels, pixels),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < pixels; y++) Marshal.Copy(straight, y * stride, data.Scan0 + y * data.Stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap.GetHicon();
    }

    public static byte[] CreateIco(params int[] sizes)
    {
        var images = sizes.Select(static size => (Size: size, Data: size >= 256 ? Png(Render(size)) : Dib(Render(size)))).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
        writer.Flush();
        return stream.ToArray();
    }

    private static DrawingImage CreateImage()
    {
        var image = new DrawingImage(CreateDrawing());
        image.Freeze();
        return image;
    }

    private static Geometry BubbleGeometry()
    {
        var body = new RectangleGeometry(new Rect(4, 6, 56, 42), 14, 14);
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            g.BeginFigure(new Point(14, 42), true, true);
            g.QuadraticBezierTo(new Point(14, 53), new Point(8, 59), true, false);
            g.QuadraticBezierTo(new Point(21, 57), new Point(31, 46), true, false);
        }
        var bubble = new CombinedGeometry(GeometryCombineMode.Union, body, tail);
        bubble.Freeze();
        return bubble;
    }

    private static Geometry LettersGeometry()
    {
        var text = new FormattedText("PL", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            30, Brushes.White, 1.0);
        var letters = text.BuildGeometry(new Point(0, 0));
        var bounds = letters.Bounds;
        letters.Transform = new TranslateTransform(32 - (bounds.Left + bounds.Width / 2), 27 - (bounds.Top + bounds.Height / 2));
        var flattened = letters.GetFlattenedPathGeometry();
        flattened.Freeze();
        return flattened;
    }

    private static byte[] StraightPixels(BitmapSource source, out int stride)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Dib(BitmapSource source)
    {
        var size = source.PixelWidth;
        var pixels = StraightPixels(source, out var stride);
        var maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(stride * size + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var y = size - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }
}
