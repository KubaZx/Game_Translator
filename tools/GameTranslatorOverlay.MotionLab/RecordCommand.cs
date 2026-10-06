using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Interop;

internal static class RecordCommand
{
    public static int Run(string titleFragment, string outputDirectory, double seconds, double fps, long quality)
    {
        var window = WindowEnumerator.GetOpenWindows()
            .Where(w => w.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static w => w.Area)
            .FirstOrDefault();
        if (window is null)
        {
            Console.Error.WriteLine("Nie znaleziono okna.");
            return 3;
        }
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            Console.Error.WriteLine("Katalog wyjściowy musi być pusty.");
            return 2;
        }
        Directory.CreateDirectory(outputDirectory);
        var encoder = ImageCodecInfo.GetImageEncoders().First(static e => e.FormatID == ImageFormat.Jpeg.Guid);
        var parameters = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, quality) } };
        var queue = new BlockingCollection<(int Index, System.Drawing.Bitmap Bitmap)>(boundedCapacity: 24);
        var workers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            foreach (var (index, bitmap) in queue.GetConsumingEnumerable())
            {
                using (bitmap) bitmap.Save(Path.Combine(outputDirectory, $"f{index:D5}.jpg"), encoder, parameters);
            }
        })).ToArray();

        using var meta = new StreamWriter(Path.Combine(outputDirectory, "frames.jsonl"));
        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var index = 0;
        var dropped = 0;
        var fallbacks = 0;
        var next = TimeSpan.Zero;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            next += interval;
            var started = clock.Elapsed;
            var (bitmap, fallback) = ScreenCapture.CaptureWindowEx(window.Handle);
            var captured = clock.Elapsed;
            if (bitmap is null)
            {
                dropped++;
                continue;
            }
            if (fallback) fallbacks++;
            var bounds = ScreenCapture.GetWindowBounds(window.Handle);
            meta.WriteLine(JsonSerializer.Serialize(new
            {
                i = index,
                tMs = Math.Round(started.TotalMilliseconds, 1),
                captureMs = Math.Round((captured - started).TotalMilliseconds, 1),
                w = bitmap.Width,
                h = bitmap.Height,
                x = bounds.X,
                y = bounds.Y,
                fallback,
            }));
            if (!queue.TryAdd((index, bitmap)))
            {
                bitmap.Dispose();
                dropped++;
                continue;
            }
            index++;
            if (next < clock.Elapsed) next = clock.Elapsed;
        }
        queue.CompleteAdding();
        Task.WaitAll(workers);
        Console.WriteLine($"MotionLab record: {index} klatek, {dropped} pominiętych, {fallbacks} ze zrzutu ekranu, {clock.Elapsed.TotalSeconds:F1} s → {outputDirectory}");
        return 0;
    }
}
