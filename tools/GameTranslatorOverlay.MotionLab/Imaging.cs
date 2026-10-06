using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.Core.Ocr;

internal sealed class StaPool : IDisposable
{
    private readonly BlockingCollection<Action> _queue;
    private readonly Thread[] _threads;

    public StaPool(int threads, int capacity, string name, ThreadPriority priority = ThreadPriority.Normal)
    {
        _queue = new BlockingCollection<Action>(capacity);
        _threads = Enumerable.Range(0, threads).Select(i =>
        {
            var thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            })
            {
                IsBackground = true,
                Name = $"{name}-{i}",
                Priority = priority,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread;
        }).ToArray();
    }

    public int Pending => _queue.Count;

    public Task<T> Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    public bool TryPost(Action work) => !_queue.IsAddingCompleted && _queue.TryAdd(() =>
    {
        try { work(); }
        catch (Exception ex) { Console.Error.WriteLine($"MotionLab: błąd zadania w tle: {ex.GetType().Name}: {ex.Message}"); }
    });

    public void Post(Action work) => _queue.Add(() =>
    {
        try { work(); }
        catch (Exception ex) { Console.Error.WriteLine($"MotionLab: błąd zadania w tle: {ex.GetType().Name}: {ex.Message}"); }
    });

    public void Complete(TimeSpan timeout)
    {
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        var deadline = DateTime.UtcNow + timeout;
        foreach (var thread in _threads)
        {
            var left = deadline - DateTime.UtcNow;
            thread.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        Complete(TimeSpan.FromSeconds(30));
        _queue.Dispose();
    }
}

internal static class Imaging
{
    public static BitmapSource DecodeJpeg(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var decoder = new JpegBitmapDecoder(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static BitmapSource DecodeAny(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static byte[] ToBgra(BitmapSource source, out int stride)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static OcrBitmap ToOcrBitmap(BitmapSource source)
    {
        var pixels = ToBgra(source, out var stride);
        return new OcrBitmap(pixels, source.PixelWidth, source.PixelHeight, stride);
    }

    public static BitmapSource FromPbgra(byte[] pbgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pbgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource FromBgra(byte[] bgra, int width, int height, int stride)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void SaveJpeg(BitmapSource source, string path, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static byte[] DownscaleBgra(byte[] source, int width, int height, int stride, int factor, out int outWidth, out int outHeight)
    {
        outWidth = width / factor;
        outHeight = height / factor;
        var result = new byte[outWidth * outHeight * 4];
        var area = factor * factor;
        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                int b = 0, g = 0, r = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = (y * factor + dy) * stride + x * factor * 4;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var p = row + dx * 4;
                        b += source[p];
                        g += source[p + 1];
                        r += source[p + 2];
                    }
                }
                var o = (y * outWidth + x) * 4;
                result[o] = (byte)(b / area);
                result[o + 1] = (byte)(g / area);
                result[o + 2] = (byte)(r / area);
                result[o + 3] = 255;
            }
        }
        return result;
    }
}
