using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Ocr;

// Laboratorium OCR (narzędzie dev): zrzuca klatkę wskazanego okna do PNG i przepuszcza
// ją przez Windows OCR w kilku wariantach preprocessingu, żeby na DANYCH wybrać ten,
// który najlepiej czyta stylizowane czcionki gier.
// Użycie: OcrLab "fragment tytułu" [katalog wyjściowy]

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var title = args.Length > 0 ? args[0] : "Escape Academy";
        var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "gto-ocrlab");
        Directory.CreateDirectory(outputDir);

        var target = WindowEnumerator.GetOpenWindows()
            .FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Console.WriteLine($"Nie znalazłem okna „{title}”.");
            return 2;
        }

        // Geometria: DPI okna gry vs DPI naszego procesu i realne wymiary — PrintWindow
        // potrafi oddać zawartość w innej skali niż to, co widać na ekranie.
        var bounds = ScreenCapture.GetWindowBounds(target.Handle);
        GetWindowRect(target.Handle, out var rect);
        GetClientRect(target.Handle, out var client);
        Console.WriteLine($"DPI okna gry: {GetDpiForWindow(target.Handle)} | DPI naszego okna: {GetDpiForSystem()} | " +
                          $"bounds DWM: {bounds.Width}×{bounds.Height} @({bounds.X},{bounds.Y}) | " +
                          $"WindowRect: {rect.Right - rect.Left}×{rect.Bottom - rect.Top} | ClientRect: {client.Right}×{client.Bottom}");

        var (captured, fallback) = ScreenCapture.CaptureWindowEx(target.Handle);
        if (captured is null)
        {
            Console.WriteLine("Przechwycenie nie powiodło się.");
            return 3;
        }
        using var frame = captured;
        Console.WriteLine($"== {target.DisplayName}: PrintWindow {frame.Width}×{frame.Height}, fallback ekranowy: {fallback} ==");
        var originalPath = Path.Combine(outputDir, "frame-original.png");
        frame.Save(originalPath, ImageFormat.Png);
        Console.WriteLine($"Klatka PrintWindow: {originalPath}");

        using (var screen = ScreenCapture.CaptureScreenRegion(bounds))
        {
            var screenPath = Path.Combine(outputDir, "frame-screen.png");
            screen.Save(screenPath, ImageFormat.Png);
            Console.WriteLine($"Zrzut ekranu (CopyFromScreen) tych samych bounds: {screen.Width}×{screen.Height} → {screenPath}");
        }

        if (args.Length > 2 && args[2] == "--geometry-only") return 0;

        var ocr = new WindowsOcrProvider();
        var variants = new (string Name, Func<Bitmap, Bitmap> Transform)[]
        {
            ("oryginał", b => (Bitmap)b.Clone()),
            ("upscale 1.5×", b => ScreenCapture.Rescale(b, 1.5)),
            ("upscale 2×", b => ScreenCapture.Rescale(b, 2.0)),
            ("szarość + rozciągnięty kontrast", b => ContrastStretch(Grayscale(b))),
            ("inwersja", Invert),
            ("gamma 0.6 (rozjaśnienie)", b => Gamma(b, 0.6)),
            ("gamma 1.6 (przyciemnienie)", b => Gamma(b, 1.6)),
            ("wyostrzenie", Sharpen),
            ("upscale 2× + kontrast", b => ContrastStretch(Grayscale(ScreenCapture.Rescale(b, 2.0)))),
            ("upscale 2× + wyostrzenie", b => Sharpen(ScreenCapture.Rescale(b, 2.0))),
        };

        foreach (var (name, transform) in variants)
        {
            using var processed = transform(frame);
            if (processed.Width > ocr.MaxImageDimension || processed.Height > ocr.MaxImageDimension)
            {
                Console.WriteLine();
                Console.WriteLine($"--- {name}: pominięty ({processed.Width}×{processed.Height} > limit OCR {ocr.MaxImageDimension}) ---");
                continue;
            }
            var slug = new string(name.Where(char.IsLetterOrDigit).ToArray());
            processed.Save(Path.Combine(outputDir, $"variant-{slug}.png"), ImageFormat.Png);

            var input = ScreenCapture.ToOcrBitmap(processed);
            var watch = Stopwatch.StartNew();
            var result = await ocr.RecognizeAsync(input, "en");
            var ms = watch.ElapsedMilliseconds;

            Console.WriteLine();
            Console.WriteLine($"--- {name}: {result.Lines.Count} linii, {ms} ms ({processed.Width}×{processed.Height}) ---");
            foreach (var line in result.Lines)
            {
                Console.WriteLine($"   [{line.Box.X},{line.Box.Y} {line.Box.Width}×{line.Box.Height}] {line.Text}");
            }
        }

        return 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    private static Bitmap Grayscale(Bitmap source) => Map(source, (r, g, b) =>
    {
        var l = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
        return (l, l, l);
    });

    private static Bitmap Invert(Bitmap source) => Map(source, (r, g, b) => ((byte)(255 - r), (byte)(255 - g), (byte)(255 - b)));

    private static Bitmap Gamma(Bitmap source, double gamma)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++) table[i] = (byte)Math.Clamp(Math.Round(255 * Math.Pow(i / 255.0, gamma)), 0, 255);
        return Map(source, (r, g, b) => (table[r], table[g], table[b]));
    }

    /// <summary>Rozciąga jasność między 2. a 98. percentylem do pełnego zakresu 0–255.</summary>
    private static Bitmap ContrastStretch(Bitmap source)
    {
        var histogram = new int[256];
        ForEachPixel(source, (r, _, _) => histogram[r]++);
        var total = source.Width * source.Height;
        int low = 0, high = 255, acc = 0;
        for (var i = 0; i < 256; i++) { acc += histogram[i]; if (acc >= total * 0.02) { low = i; break; } }
        acc = 0;
        for (var i = 255; i >= 0; i--) { acc += histogram[i]; if (acc >= total * 0.02) { high = i; break; } }
        var range = Math.Max(1, high - low);
        return Map(source, (r, g, b) =>
        {
            byte Stretch(byte v) => (byte)Math.Clamp((v - low) * 255 / range, 0, 255);
            return (Stretch(r), Stretch(g), Stretch(b));
        });
    }

    private static Bitmap Sharpen(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var src = ReadPixels(source);
        var dst = new byte[src.Length];
        int[] kernel = [0, -1, 0, -1, 5, -1, 0, -1, 0];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    var k = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var sx = Math.Clamp(x + dx, 0, width - 1);
                            var sy = Math.Clamp(y + dy, 0, height - 1);
                            sum += src[(sy * width + sx) * 4 + c] * kernel[k++];
                        }
                    }
                    dst[(y * width + x) * 4 + c] = (byte)Math.Clamp(sum, 0, 255);
                }
                dst[(y * width + x) * 4 + 3] = 255;
            }
        }
        return WritePixels(dst, width, height);
    }

    private static Bitmap Map(Bitmap source, Func<byte, byte, byte, (byte R, byte G, byte B)> map)
    {
        var pixels = ReadPixels(source);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var (r, g, b) = map(pixels[i + 2], pixels[i + 1], pixels[i]);
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return WritePixels(pixels, source.Width, source.Height);
    }

    private static void ForEachPixel(Bitmap source, Action<byte, byte, byte> visit)
    {
        var pixels = ReadPixels(source);
        for (var i = 0; i < pixels.Length; i += 4) visit(pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = bitmap.Width * 4;
            var pixels = new byte[stride * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * stride, stride);
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap WritePixels(byte[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = width * 4;
            for (var row = 0; row < height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels, row * stride, data.Scan0 + row * data.Stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
