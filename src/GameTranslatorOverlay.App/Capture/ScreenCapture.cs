using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;
using Size = System.Drawing.Size;

namespace GameTranslatorOverlay.App.Capture;

/// <summary>
/// Przechwytywanie obrazu przez oficjalne mechanizmy GDI (BitBlt/PrintWindow).
/// Działa dla okien i borderless fullscreen; exclusive fullscreen jest poza zakresem MVP.
/// Wszystkie współrzędne w fizycznych pikselach ekranu.
/// </summary>
public static class ScreenCapture
{
    public static Bitmap CaptureScreenRegion(RectPx region)
    {
        if (region.IsEmpty) throw new ArgumentException("Region przechwytywania jest pusty.", nameof(region));

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    public static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return null;

        var bounds = GetWindowBounds(hwnd);
        if (bounds.IsEmpty) return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect)) return null;
        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        if (windowWidth <= 0 || windowHeight <= 0) return null;

        var printed = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
        bool success;
        using (var graphics = Graphics.FromImage(printed))
        {
            var hdc = graphics.GetHdc();
            try
            {
                success = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        if (!success || LooksBlank(printed))
        {
            // Niektóre gry (DirectX/Vulkan) nie wspierają PrintWindow — bierzemy obraz prosto z ekranu.
            printed.Dispose();
            return CaptureScreenRegion(bounds);
        }

        // PrintWindow renderuje pełny prostokąt okna (z cieniem) — przycinamy do widocznej ramki.
        var crop = new Rectangle(
            Math.Max(0, bounds.X - windowRect.Left),
            Math.Max(0, bounds.Y - windowRect.Top),
            Math.Min(bounds.Width, windowWidth),
            Math.Min(bounds.Height, windowHeight));

        if (crop.Width <= 0 || crop.Height <= 0 || (crop.X == 0 && crop.Y == 0 && crop.Width == windowWidth && crop.Height == windowHeight))
        {
            return printed;
        }

        try
        {
            return printed.Clone(crop, PixelFormat.Format32bppArgb);
        }
        finally
        {
            printed.Dispose();
        }
    }

    /// <summary>Widoczna ramka okna (bez cienia DWM) w fizycznych pikselach ekranu.</summary>
    public static RectPx GetWindowBounds(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT extended, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            return new RectPx(extended.Left, extended.Top, extended.Right - extended.Left, extended.Bottom - extended.Top);
        }

        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return new RectPx(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        return default;
    }

    public static OcrBitmap ToOcrBitmap(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = bitmap.Width * 4;
            var pixels = new byte[stride * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + row * data.Stride, pixels, row * stride, stride);
            }
            return new OcrBitmap(pixels, bitmap.Width, bitmap.Height, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static Bitmap Rescale(Bitmap source, double factor)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * factor));
        var height = Math.Max(1, (int)Math.Round(source.Height * factor));
        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return scaled;
    }

    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    /// <summary>
    /// Zredukowana siatka jasności klatki do taniego wykrywania zmian w trybie live.
    /// Kopiuje z klatki wyłącznie próbkowane wiersze (ok. 80 zamiast ~1080), a bufor
    /// jest reużywany między klatkami — pętla live nie mieli pamięci.
    /// </summary>
    public static Core.Vision.LuminanceGrid ComputeLuminanceGrid(Bitmap bitmap, ref byte[]? reusableBuffer)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var required = stride * bitmap.Height;
            if (reusableBuffer is null || reusableBuffer.Length < required)
            {
                reusableBuffer = new byte[required];
            }

            foreach (var y in Core.Vision.LuminanceGrid.GetSampledRows(bitmap.Height))
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, reusableBuffer, y * stride, stride);
            }

            return Core.Vision.LuminanceGrid.FromBgra32(reusableBuffer, bitmap.Width, bitmap.Height, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Heurystyka „czarnego” zrzutu z PrintWindow — próbkuje siatkę pikseli.</summary>
    public static bool LooksBlank(Bitmap bitmap)
    {
        const int gridSize = 24;
        var stepX = Math.Max(1, bitmap.Width / gridSize);
        var stepY = Math.Max(1, bitmap.Height / gridSize);

        var first = bitmap.GetPixel(0, 0);
        var different = 0;
        var total = 0;

        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                total++;
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - first.R) + Math.Abs(pixel.G - first.G) + Math.Abs(pixel.B - first.B) > 12)
                {
                    different++;
                }
            }
        }

        return total == 0 || different < total * 0.02;
    }
}
