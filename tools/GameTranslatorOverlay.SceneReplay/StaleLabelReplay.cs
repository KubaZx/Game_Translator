using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Vision;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;

internal static class StaleLabelReplay
{
    private const string Inspect = "Inspect";
    private const string JunkReading = "lRrgIé@ue";
    private const string NewText = "The drawer is open";
    private const string NewWord = "is open";
    private const string LabelCropFile = "inspect_crop.png";
    private const int FrameWidth = 1500;
    private const int FrameHeight = 900;
    private const int ObservationMs = 12000;
    private const int SettleMs = 1500;
    private const int SettledReadingMs = 200;
    private const int AnimationIntervalMs = 200;
    private const int MarkerSize = 8;
    private const int OutlineDilation = 7;
    private const int GhostCycle = 4;
    private const double JunkShiftFraction = 0.15;
    private static readonly RectPx LabelPatch = new(525, 375, 450, 150);
    private static readonly RectPx SyntheticGlyphTarget = new(674, 430, 259, 70);
    private static readonly RectPx AnimationBox = new(1360, 60, 96, 96);
    private static readonly RectPx NewTextTarget = new(80, 760, 0, 0);

    public static int Run(string output, string scenario, string? assetsDirectory, string? texturePath,
        (int X, int Y)? textureOrigin, int providerDelayMs, int brightSpotPx = 0)
    {
        if (scenario is not ("stale-junk" or "stale-junk-ghost" or "stale-texture" or "stale-newtext" or "stale-busy"))
            throw new ArgumentException("Unknown stale label scenario.", nameof(scenario));
        var assets = SceneAssets.Load(assetsDirectory, texturePath, textureOrigin);
        using var report = new Report(output, scenario, providerDelayMs, assets, brightSpotPx);
        return RunAsync(report, assets, providerDelayMs).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Report report, SceneAssets assets, int providerDelayMs)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gto-stalelabel-" + Guid.NewGuid().ToString("N")));
        var settings = new AppSettings
        {
            Provider = MockTranslationProvider.ProviderName, PrivateMode = true,
            ActiveProfileId = null, OcrUpscale = 1,
        };
        using var logging = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var scripted = report.Junk ? new MarkerOcr(report, report.Ghost) : null;
        IOcrProvider ocr = scripted is not null ? scripted : new ObservedOcr(new WindowsOcrProvider(), report);
        var usage = new UsageTracker();
        var orchestrator = new TranslationOrchestrator(settings, new SqliteTranslationCache(paths.DatabasePath),
            new GlossaryService(), GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths),
            new UserGlossaryStore(paths), new MockTranslationProvider { Delay = TimeSpan.FromMilliseconds(providerDelayMs) },
            new DeepLTranslationProvider(http, static () => null), ocr, usage, logging);
        TestWindow? window = null;
        LiveTranslationSession? session = null;
        var exitCode = 0;
        var reason = "completed";
        try
        {
            orchestrator.Initialize();
            if (!ocr.IsLanguageAvailable(settings.SourceLanguage))
                throw new OcrLanguageNotAvailableException(settings.SourceLanguage);
            window = await TestWindow.CreateAsync(assets, report);
            var truth = await MeasureGroundTruthAsync(window, report.ShowsNewText);
            report.RecordGroundTruth(truth);
            scripted?.Calibrate(truth);
            if (report.Busy) await window.StartAnimationAsync();
            var options = new LiveSessionOptions { OcrUpscale = 1, EnableDiagnostics = true };
            session = new LiveTranslationSession(orchestrator, ocr, window.Handle, options,
                report.Update, logging.CreateLogger("StaleLabelReplay"));
            session.Start();
            await report.InitialDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(SettleMs);
            await window.SetStateAsync(false, report.ShowsNewText, () => report.BeginMeasurement(usage));
            await Task.Delay(ObservationMs);
        }
        catch (Exception ex)
        {
            exitCode = 3;
            reason = ex is TimeoutException ? "scenario_timeout" : "runtime_error";
            report.Event(new { type = "error", errorType = ex.GetType().Name });
        }
        finally
        {
            report.EndMeasurement();
            if (session is not null)
            {
                session.Stop();
                try
                {
                    await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                    session.Dispose();
                }
                catch (OperationCanceledException) { session.Dispose(); }
                catch (Exception) { exitCode = 4; reason = "session_shutdown_failed"; }
            }
            if (window is not null)
            {
                try { await window.CloseAsync(); }
                catch (Exception) { exitCode = 4; reason = "window_shutdown_failed"; }
            }
        }
        if (exitCode == 0 && !report.FixtureValid)
        {
            exitCode = 4;
            reason = "fixture_preconditions_failed";
        }
        return report.Finish(exitCode, reason, usage);
    }

    private sealed record GroundTruth(
        int CaptureWidth, int CaptureHeight, bool ScreenFallback, RectPx GlyphBox, int GlyphWhite,
        RectPx? Marker, int TextRgb, int BackgroundRgb, bool KnownContrast, int AfterWhite, bool AfterClearlyEmpty,
        int[] AfterChannelRange, double ChangedPixelFraction, double GridChangedFraction, double GridSignificantFraction,
        RectPx? GridSignificantRegion, int NewTextWhite, bool AfterKnownTextAbsent);

    private static async Task<GroundTruth> MeasureGroundTruthAsync(TestWindow window, bool busy)
    {
        await window.SetStateAsync(true, false);
        await Task.Delay(150);
        var (before, beforeFallback) = CaptureFrame(window.Handle);
        await window.SetStateAsync(false, busy);
        await Task.Delay(150);
        var (after, afterFallback) = CaptureFrame(window.Handle);
        await window.SetStateAsync(true, false);
        await Task.Delay(150);
        var frameRect = new RectPx(0, 0, before.Width, before.Height);
        var (glyphWhite, glyphBox) = CountWhite(before, LabelPatch.Intersect(frameRect));
        var marker = FindMarker(before);
        var colors = BlockColorSampler.SampleColors(before.PixelsBgra32, before.Width, before.Height, before.Stride, glyphBox);
        var (afterWhite, _) = CountWhite(after, glyphBox);
        var afterEmpty = TextPresenceProbe.IsClearlyEmpty(after, glyphBox, colors.TextRgb, colors.BackgroundRgb);
        var afterAbsent = KnownTextAbsenceProbe.IsKnownTextAbsent(after, glyphBox, colors.TextRgb, colors.BackgroundRgb);
        var range = ChannelRange(after, glyphBox);
        var changed = 0L;
        if (before.Width == after.Width && before.Height == after.Height)
        {
            for (var y = 0; y < before.Height; y++)
            {
                for (var x = 0; x < before.Width; x++)
                {
                    var offset = y * before.Stride + x * 4;
                    if (Math.Abs(before.PixelsBgra32[offset] - after.PixelsBgra32[offset]) > 10
                        || Math.Abs(before.PixelsBgra32[offset + 1] - after.PixelsBgra32[offset + 1]) > 10
                        || Math.Abs(before.PixelsBgra32[offset + 2] - after.PixelsBgra32[offset + 2]) > 10)
                        changed++;
                }
            }
        }
        var gridBefore = LuminanceGrid.FromBgra32(before.PixelsBgra32, before.Width, before.Height, before.Stride);
        var gridAfter = LuminanceGrid.FromBgra32(after.PixelsBgra32, after.Width, after.Height, after.Stride);
        var analysis = before.Width == after.Width && before.Height == after.Height
            ? new NoiseAwareChangeDetector().Analyze(gridBefore, gridAfter, before.Width, before.Height)
            : new NoiseAwareAnalysis(1, 1, 1, null);
        var newWhite = busy ? CountWhite(after, new RectPx(NewTextTarget.X - 10, NewTextTarget.Y - 10, 900, 110).Intersect(frameRect)).Count : 0;
        return new GroundTruth(before.Width, before.Height, beforeFallback || afterFallback, glyphBox, glyphWhite,
            marker, colors.TextRgb, colors.BackgroundRgb, TextPresenceProbe.CanCheckKnownText(colors.TextRgb, colors.BackgroundRgb),
            afterWhite, afterEmpty, range, (double)changed / ((long)before.Width * before.Height),
            analysis.ChangedFraction, analysis.SignificantFraction, analysis.SignificantRegion, newWhite, afterAbsent);
    }

    private static (OcrBitmap Frame, bool Fallback) CaptureFrame(IntPtr handle)
    {
        var (bitmap, fallback) = ScreenCapture.CaptureWindowEx(handle);
        using (bitmap)
        {
            if (bitmap is null) throw new InvalidOperationException("Own-window capture failed.");
            return (ScreenCapture.ToOcrBitmap(bitmap), fallback);
        }
    }

    private static bool IsWhite(byte[] pixels, int offset)
    {
        var b = pixels[offset];
        var g = pixels[offset + 1];
        var r = pixels[offset + 2];
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        return min >= 215 && max - min <= 40;
    }

    private static bool IsMarker(byte[] pixels, int offset) =>
        pixels[offset + 2] is >= 110 and <= 190 && pixels[offset + 1] <= 50 && pixels[offset] is >= 110 and <= 190;

    private static (int Count, RectPx Box) CountWhite(OcrBitmap bitmap, RectPx area)
    {
        var region = area.Intersect(new RectPx(0, 0, bitmap.Width, bitmap.Height));
        if (region.IsEmpty) return (0, default);
        var count = 0;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                if (!IsWhite(bitmap.PixelsBgra32, y * bitmap.Stride + x * 4)) continue;
                count++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return count == 0 ? (0, default) : (count, new RectPx(left, top, right - left + 1, bottom - top + 1));
    }

    private static RectPx? FindMarker(OcrBitmap bitmap)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (!IsMarker(bitmap.PixelsBgra32, y * bitmap.Stride + x * 4)) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < 0) return null;
        var box = new RectPx(left, top, right - left + 1, bottom - top + 1);
        return box.Width is >= MarkerSize / 2 and <= MarkerSize * 3 && box.Height is >= MarkerSize / 2 and <= MarkerSize * 3
            ? box : null;
    }

    private static int[] ChannelRange(OcrBitmap bitmap, RectPx area)
    {
        var region = area.Intersect(new RectPx(0, 0, bitmap.Width, bitmap.Height));
        if (region.IsEmpty) return [0, 0, 0];
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                var offset = y * bitmap.Stride + x * 4;
                minB = Math.Min(minB, bitmap.PixelsBgra32[offset]);
                maxB = Math.Max(maxB, bitmap.PixelsBgra32[offset]);
                minG = Math.Min(minG, bitmap.PixelsBgra32[offset + 1]);
                maxG = Math.Max(maxG, bitmap.PixelsBgra32[offset + 1]);
                minR = Math.Min(minR, bitmap.PixelsBgra32[offset + 2]);
                maxR = Math.Max(maxR, bitmap.PixelsBgra32[offset + 2]);
            }
        }
        return [maxR - minR, maxG - minG, maxB - minB];
    }

    private static long Area(RectPx box) => box.IsEmpty ? 0 : (long)box.Width * box.Height;

    private static bool IsOld(string text) => text.Contains("nspect", StringComparison.OrdinalIgnoreCase);
    private static bool IsNew(string text) => text.Contains(NewWord, StringComparison.OrdinalIgnoreCase);

    private sealed record SceneAssets(
        BitmapSource Texture, BitmapSource? Label, string? TextureFile, int TextureX, int TextureY,
        string? LabelFile, RectPx? LabelGlyphsInCrop)
    {
        public bool RealTexture => TextureFile is not null;
        public bool RealLabel => Label is not null;

        public static SceneAssets Load(string? assetsDirectory, string? texturePath, (int X, int Y)? origin)
        {
            BitmapSource? label = null;
            RectPx? glyphs = null;
            string? labelFile = null;
            if (assetsDirectory is not null)
            {
                var labelPath = Path.Combine(assetsDirectory, LabelCropFile);
                if (!File.Exists(labelPath)) throw new ArgumentException($"--assets wymaga pliku {LabelCropFile}.");
                var crop = Decode(labelPath);
                if (crop.PixelWidth != LabelPatch.Width || crop.PixelHeight != LabelPatch.Height)
                    throw new ArgumentException($"{LabelCropFile} musi miec {LabelPatch.Width}x{LabelPatch.Height} px.");
                (label, var cropGlyphs) = MaskLabel(crop);
                glyphs = cropGlyphs;
                labelFile = LabelCropFile;
            }
            if (texturePath is null)
            {
                if (origin is not null) throw new ArgumentException("--texture-origin wymaga --texture.");
                return new SceneAssets(SyntheticTexture(), label, null, 0, 0, labelFile, glyphs);
            }
            if (!File.Exists(texturePath)) throw new ArgumentException("Plik --texture nie istnieje.");
            var source = Decode(texturePath);
            var x = origin?.X ?? (source.PixelWidth - FrameWidth) / 2;
            var y = origin?.Y ?? (source.PixelHeight - FrameHeight) / 2;
            if (x < 0 || y < 0 || x + FrameWidth > source.PixelWidth || y + FrameHeight > source.PixelHeight)
                throw new ArgumentException($"--texture musi zawierac obszar {FrameWidth}x{FrameHeight} px od --texture-origin.");
            var cropped = new CroppedBitmap(source, new Int32Rect(x, y, FrameWidth, FrameHeight));
            cropped.Freeze();
            var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(texturePath)));
            return new SceneAssets(cropped, label, $"{folder}/{Path.GetFileName(texturePath)}", x, y, labelFile, glyphs);
        }
    }

    private static BitmapSource Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static (BitmapSource Label, RectPx Glyphs) MaskLabel(BitmapSource crop)
    {
        var width = crop.PixelWidth;
        var height = crop.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        crop.CopyPixels(pixels, stride, 0);
        var core = new bool[width * height];
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!IsWhite(pixels, y * stride + x * 4)) continue;
                core[y * width + x] = true;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < 0) throw new ArgumentException($"{LabelCropFile} nie zawiera bialych glifow etykiety.");
        for (var y = Math.Max(0, top - 30); y < Math.Min(height, bottom + 16); y++)
        {
            for (var x = 0; x < Math.Max(0, left - 10); x++)
            {
                var offset = y * stride + x * 4;
                var luminance = 0.299 * pixels[offset + 2] + 0.587 * pixels[offset + 1] + 0.114 * pixels[offset];
                if (luminance >= 100) core[y * width + x] = true;
            }
        }
        var mask = Dilate(core, width, height, OutlineDilation);
        for (var i = 0; i < mask.Length; i++) pixels[i * 4 + 3] = mask[i] ? (byte)255 : (byte)0;
        var label = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        label.Freeze();
        return (label, new RectPx(left, top, right - left + 1, bottom - top + 1));
    }

    private static bool[] Dilate(bool[] source, int width, int height, int radius)
    {
        var horizontal = new bool[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var found = false;
                for (var dx = Math.Max(0, x - radius); dx <= Math.Min(width - 1, x + radius) && !found; dx++)
                    found = source[y * width + dx];
                horizontal[y * width + x] = found;
            }
        }
        var result = new bool[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var found = false;
                for (var dy = Math.Max(0, y - radius); dy <= Math.Min(height - 1, y + radius) && !found; dy++)
                    found = horizontal[dy * width + x];
                result[y * width + x] = found;
            }
        }
        return result;
    }

    private static BitmapSource SyntheticTexture()
    {
        var random = new Random(20261005);
        double[,] Grid(int cell)
        {
            var grid = new double[FrameHeight / cell + 2, FrameWidth / cell + 2];
            for (var y = 0; y < grid.GetLength(0); y++)
                for (var x = 0; x < grid.GetLength(1); x++)
                    grid[y, x] = random.NextDouble();
            return grid;
        }
        static double Sample(double[,] grid, int cell, int x, int y)
        {
            var gx = (double)x / cell;
            var gy = (double)y / cell;
            var x0 = (int)gx;
            var y0 = (int)gy;
            var fx = gx - x0;
            var fy = gy - y0;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            var top = grid[y0, x0] + (grid[y0, x0 + 1] - grid[y0, x0]) * fx;
            var bottom = grid[y0 + 1, x0] + (grid[y0 + 1, x0 + 1] - grid[y0 + 1, x0]) * fx;
            return top + (bottom - top) * fy;
        }
        var fine = Grid(18);
        var coarse = Grid(90);
        var stride = FrameWidth * 4;
        var pixels = new byte[stride * FrameHeight];
        for (var y = 0; y < FrameHeight; y++)
        {
            for (var x = 0; x < FrameWidth; x++)
            {
                var noise = 0.6 * Sample(coarse, 90, x, y) + 0.4 * Sample(fine, 18, x, y);
                var stripe = 0.5 + 0.5 * Math.Sin(x * 0.031 + y * 0.017);
                var t = Math.Clamp(0.75 * noise + 0.25 * stripe, 0, 1);
                var grain = random.Next(-5, 6);
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)Math.Clamp(52 - 20 * t + grain, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(26 + 40 * t + grain, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(20 + 75 * t + grain, 0, 255);
                pixels[offset + 3] = 255;
            }
        }
        var texture = BitmapSource.Create(FrameWidth, FrameHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        texture.Freeze();
        return texture;
    }

    private static void AddOutlinedText(Panel parent, string text, double fontPx, double left, double top, double stroke)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontPx, Brushes.White, 1.0);
        var geometry = formatted.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        var placed = geometry.Clone();
        placed.Transform = new TranslateTransform(left - bounds.X, top - bounds.Y);
        placed.Freeze();
        parent.Children.Add(new WpfPath { Data = placed, Stroke = Brushes.Black, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round });
        parent.Children.Add(new WpfPath { Data = placed, Fill = Brushes.White });
    }

    private static Canvas SyntheticLabel()
    {
        var canvas = new Canvas { Width = FrameWidth, Height = FrameHeight, IsHitTestVisible = false };
        var icon = new WpfRectangle
        {
            Width = 70, Height = 92, RadiusX = 32, RadiusY = 32, StrokeThickness = 6, Stroke = Brushes.Black,
            Fill = new SolidColorBrush(Color.FromRgb(168, 160, 190)),
        };
        Canvas.SetLeft(icon, 566);
        Canvas.SetTop(icon, 418);
        var button = new WpfRectangle
        {
            Width = 24, Height = 34, RadiusX = 10, RadiusY = 10,
            Fill = new SolidColorBrush(Color.FromRgb(214, 206, 92)),
        };
        Canvas.SetLeft(button, 574);
        Canvas.SetTop(button, 426);
        canvas.Children.Add(icon);
        canvas.Children.Add(button);
        AddOutlinedText(canvas, Inspect, 76, SyntheticGlyphTarget.X, SyntheticGlyphTarget.Y, 8);
        return canvas;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP disabled in StaleLabelReplay.");
    }

    private sealed class ObservedOcr(IOcrProvider inner, Report report) : IOcrProvider
    {
        public string Name => inner.Name;
        public int MaxImageDimension => inner.MaxImageDimension;
        public IReadOnlyList<string> AvailableLanguages => inner.AvailableLanguages;
        public bool IsLanguageAvailable(string languageTag) => inner.IsLanguageAvailable(languageTag);
        public async Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            var started = report.Now;
            var result = await inner.RecognizeAsync(bitmap, languageTag, cancellationToken).ConfigureAwait(false);
            report.WindowsOcr(result, bitmap.Width, bitmap.Height, started);
            return result;
        }
    }

    private sealed class MarkerOcr(Report report, bool ghostCycle) : IOcrProvider
    {
        private GroundTruth? _truth;
        private int _absentReadings;
        public string Name => "Scripted label OCR from captured marker and white glyph pixels";
        public int MaxImageDimension => 4096;
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public bool IsLanguageAvailable(string languageTag) => languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        public void Calibrate(GroundTruth truth) => _truth = truth;

        public Task<OcrResult> RecognizeAsync(OcrBitmap bitmap, string languageTag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = report.Now;
            var truth = _truth ?? throw new InvalidOperationException("Scripted OCR used before calibration.");
            var frame = new RectPx(0, 0, bitmap.Width, bitmap.Height);
            if (FindMarker(bitmap) is not { } marker || truth.Marker is not { } reference)
            {
                report.ScriptedOcr("no-marker", 0, 0, null, bitmap.Width, bitmap.Height, started);
                return Task.FromResult(OcrResult.Empty(languageTag));
            }
            var ratio = (marker.Width + marker.Height) / (double)(reference.Width + reference.Height);
            var scale = ratio < 1.5 ? 1.0 : 2.0;
            var centerX = marker.X + marker.Width / 2.0;
            var centerY = marker.Y + marker.Height / 2.0;
            var referenceX = reference.X + reference.Width / 2.0;
            var referenceY = reference.Y + reference.Height / 2.0;
            var expected = new RectPx(
                (int)Math.Round(centerX + (truth.GlyphBox.X - referenceX) * scale),
                (int)Math.Round(centerY + (truth.GlyphBox.Y - referenceY) * scale),
                (int)Math.Round(truth.GlyphBox.Width * scale),
                (int)Math.Round(truth.GlyphBox.Height * scale));
            var (white, whiteBox) = CountWhite(bitmap, expected.Intersect(frame));
            var whiteRatio = white / (truth.GlyphWhite * scale * scale);
            if (whiteRatio >= 0.3)
            {
                report.ScriptedOcr("inspect", scale, whiteRatio, null, bitmap.Width, bitmap.Height, started);
                return Task.FromResult(new OcrResult([new OcrLine(Inspect, whiteBox, [new OcrWord(Inspect, whiteBox)])], languageTag));
            }
            _absentReadings++;
            if (ghostCycle && _absentReadings % GhostCycle != 0)
            {
                report.ScriptedOcr("empty", scale, whiteRatio, null, bitmap.Width, bitmap.Height, started);
                return Task.FromResult(OcrResult.Empty(languageTag));
            }
            var junkBox = expected.Offset((int)Math.Round(expected.Width * JunkShiftFraction), 0).Intersect(frame);
            var overlap = Area(junkBox.Intersect(expected)) / (double)Math.Max(1, Math.Min(Area(junkBox), Area(expected)));
            report.ScriptedOcr("junk", scale, whiteRatio, overlap, bitmap.Width, bitmap.Height, started);
            return Task.FromResult(new OcrResult([new OcrLine(JunkReading, junkBox, [new OcrWord(JunkReading, junkBox)])], languageTag));
        }
    }

    private sealed class TestWindow(Window window, UIElement label, UIElement? newText, WpfRectangle? animation,
        DispatcherTimer? timer, Thread thread)
    {
        public IntPtr Handle { get; } = new WindowInteropHelper(window).Handle;

        public static async Task<TestWindow> CreateAsync(SceneAssets assets, Report report)
        {
            var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? thread = null;
            thread = new Thread(() =>
            {
                try
                {
                    var canvas = new Canvas { Width = FrameWidth, Height = FrameHeight, Background = Brushes.Black, ClipToBounds = true };
                    RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.NearestNeighbor);
                    canvas.Children.Add(new Image { Source = assets.Texture, Width = FrameWidth, Height = FrameHeight, Stretch = Stretch.Fill });
                    if (report.BrightSpot is { } spot)
                    {
                        var brightSpot = new WpfRectangle { Width = spot.Width, Height = spot.Height, Fill = Brushes.White };
                        Canvas.SetLeft(brightSpot, spot.X);
                        Canvas.SetTop(brightSpot, spot.Y);
                        canvas.Children.Add(brightSpot);
                    }
                    UIElement label;
                    if (assets.Label is { } real)
                    {
                        var image = new Image { Source = real, Width = LabelPatch.Width, Height = LabelPatch.Height, Stretch = Stretch.Fill };
                        Canvas.SetLeft(image, LabelPatch.X);
                        Canvas.SetTop(image, LabelPatch.Y);
                        label = image;
                    }
                    else
                    {
                        label = SyntheticLabel();
                    }
                    canvas.Children.Add(label);
                    UIElement? newText = null;
                    WpfRectangle? animation = null;
                    DispatcherTimer? timer = null;
                    if (report.ShowsNewText)
                    {
                        var newCanvas = new Canvas { Width = FrameWidth, Height = FrameHeight, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                        AddOutlinedText(newCanvas, NewText, 56, NewTextTarget.X, NewTextTarget.Y, 7);
                        canvas.Children.Add(newCanvas);
                        newText = newCanvas;
                        var dark = new SolidColorBrush(Color.FromRgb(30, 30, 40));
                        var bright = new SolidColorBrush(Color.FromRgb(230, 200, 60));
                        animation = new WpfRectangle { Width = AnimationBox.Width, Height = AnimationBox.Height, Fill = dark };
                        Canvas.SetLeft(animation, AnimationBox.X);
                        Canvas.SetTop(animation, AnimationBox.Y);
                        canvas.Children.Add(animation);
                        var tick = 0;
                        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AnimationIntervalMs) };
                        var animated = animation;
                        timer.Tick += (_, _) =>
                        {
                            tick++;
                            animated.Fill = tick % 2 == 0 ? dark : bright;
                            report.Tick();
                        };
                    }
                    if (report.Junk)
                    {
                        var marker = new WpfRectangle { Width = MarkerSize, Height = MarkerSize, Fill = new SolidColorBrush(Color.FromRgb(144, 0, 144)) };
                        Canvas.SetLeft(marker, SyntheticGlyphTarget.X + SyntheticGlyphTarget.Width / 2 - MarkerSize / 2);
                        Canvas.SetTop(marker, SyntheticGlyphTarget.Y + SyntheticGlyphTarget.Height / 2 - MarkerSize / 2);
                        Panel.SetZIndex(marker, 10);
                        canvas.Children.Add(marker);
                    }
                    var window = new Window
                    {
                        Title = "GTO StaleLabelReplay - local test", Width = FrameWidth, Height = FrameHeight,
                        Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, UseLayoutRounding = true,
                        Content = new Viewbox { Stretch = Stretch.Fill, Child = canvas },
                    };
                    var stoppedTimer = timer;
                    window.Closed += (_, _) =>
                    {
                        stoppedTimer?.Stop();
                        window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    };
                    window.Show();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    window.Width = FrameWidth / dpi.DpiScaleX;
                    window.Height = FrameHeight / dpi.DpiScaleY;
                    report.Event(new { type = "window_created", dpiScaleX = dpi.DpiScaleX, dpiScaleY = dpi.DpiScaleY,
                        windowWidthDip = window.Width, windowHeightDip = window.Height });
                    ready.TrySetResult(new TestWindow(window, label, newText, animation, timer, thread!));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task SetStateAsync(bool labelVisible, bool newTextVisible, Action? afterRendering = null) => RenderAsync(() =>
        {
            label.Visibility = labelVisible ? Visibility.Visible : Visibility.Collapsed;
            if (newText is not null) newText.Visibility = newTextVisible ? Visibility.Visible : Visibility.Collapsed;
        }, afterRendering);

        public Task StartAnimationAsync() => RenderAsync(() =>
        {
            if (timer is null || animation is null) throw new InvalidOperationException("Animation is only available in stale-busy.");
            timer.Start();
        });

        private async Task RenderAsync(Action? change, Action? afterRendering = null)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() =>
            {
                change?.Invoke();
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    afterRendering?.Invoke();
                    rendered.TrySetResult();
                };
                CompositionTarget.Rendering += handler;
            });
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task CloseAsync()
        {
            await window.Dispatcher.InvokeAsync(window.Close).Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!thread.Join(TimeSpan.FromSeconds(3))) throw new TimeoutException();
        }
    }

    private sealed class Report : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private GroundTruth? _truth;
        private double? _changeAt, _endedAt, _initialDisplayedAt, _oldRemovedAt, _newReadyAt;
        private double? _firstOcrAfterChangeAt, _firstOcrCallbackAfterChangeAt, _firstFullOcrAfterChangeAt, _lastFullOcrAt;
        private double? _firstRetainedAt, _newFirstOcrAt, _oldVisibleSince, _lastOldRemovedAt;
        private double _maxFullOcrGapMs, _oldVisibleTotalMs;
        private long _requestsAtChange, _charactersAtChange;
        private bool _ended, _screenFallback, _stoppedDuringMeasurement;
        private bool _visibleOld, _visibleNew, _oldRemoved;
        private int _oldReturnTransitions, _oldVisibleCallbacksAfterRemoval, _visualUpdates, _clears, _hides, _maxOtherBlocks;
        private int _completedOcr, _fullOcr, _partialOcr, _sceneCuts, _whiffs, _retained, _reused, _reusedFrames;
        private int _ocrCalls, _ocrFull, _ocrInspect, _ocrInspectSettled, _ocrNew, _ocrEmpty, _ocrOtherMeaningful;
        private int _junkReadings, _emptyScripted, _noMarker, _inspectScriptedSettled;
        private double _minJunkOverlap = double.PositiveInfinity;
        private int _ticks, _ticksAfterChange;
        private RectPx? _initialOldBox;
        private readonly string _scenario;

        public bool Junk { get; }
        public bool Ghost { get; }
        public bool Busy { get; }
        public bool ShowsNewText { get; }
        public RectPx? BrightSpot { get; }
        public TaskCompletionSource InitialDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double Now => _clock.Elapsed.TotalMilliseconds;

        public Report(string output, string scenario, int providerDelayMs, SceneAssets assets, int brightSpotPx)
        {
            _scenario = scenario;
            BrightSpot = brightSpotPx > 0
                ? new RectPx(SyntheticGlyphTarget.X + 24, SyntheticGlyphTarget.Y + (SyntheticGlyphTarget.Height - brightSpotPx) / 2,
                    brightSpotPx, brightSpotPx)
                : null;
            Junk = scenario is "stale-junk" or "stale-junk-ghost";
            Ghost = scenario == "stale-junk-ghost";
            Busy = scenario == "stale-busy";
            ShowsNewText = scenario is "stale-busy" or "stale-newtext";
            var junkNormalized = TextNormalizer.Normalize(JunkReading);
            var stabilizer = new LiveReadingStabilizer();
            var firstDecision = stabilizer.Observe("fixture", Inspect, JunkReading);
            var secondDecision = stabilizer.Observe("fixture", Inspect, JunkReading);
            var junkMeaningful = JunkFilter.IsMeaningful(JunkReading);
            var junkQuality = ReadingQuality.Score(junkNormalized);
            var inspectQuality = ReadingQuality.Score(Inspect);
            var junkSimilarity = TextSimilarity.Ratio(junkNormalized, Inspect);
            var ghostCondition = junkSimilarity >= 0.5 || junkQuality < inspectQuality - 0.1;
            if (Junk && (!junkMeaningful || junkQuality >= 0.9 || firstDecision != LiveReadingDecision.Keep
                || secondDecision != LiveReadingDecision.Keep || (Ghost && !ghostCondition)))
                throw new InvalidOperationException("Junk OCR fixture no longer exercises the Keep path of LiveReadingStabilizer.");
            _writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Event(new
            {
                type = "header", scenario, ocrMode = Junk ? "scripted" : "windows", provider = "Mock", providerDelayMs,
                privateMode = true, cache = "fresh-memory", network = "blocked", capture = "own-window-only", actualOverlayWindow = false,
                fps = 6, ocrUpscale = 1, staticRescanIntervalMs = 4000, blockMissGrace = 2, maxWhiffRetries = 2,
                observationMs = ObservationMs, settleAfterInitialDisplayMs = SettleMs, settledReadingMs = SettledReadingMs,
                framePx = new[] { FrameWidth, FrameHeight }, labelPatchPx = LabelPatch,
                labelPatchFraction = (double)Area(LabelPatch) / ((long)FrameWidth * FrameHeight),
                realLabel = assets.RealLabel, labelFile = assets.LabelFile, labelGlyphsInCropPx = assets.LabelGlyphsInCrop,
                realTexture = assets.RealTexture, textureFile = assets.TextureFile,
                textureOriginPx = assets.RealTexture ? new[] { assets.TextureX, assets.TextureY } : null,
                syntheticLabel = assets.RealLabel ? null : "Segoe UI Bold 76 px white, black 8 px stroke, drawn mouse icon",
                syntheticTexture = assets.RealTexture ? null : "Deterministic value noise, seed 20261005, channels below 110",
                assetsCopied = false, pixelsSaved = false, ocrTextSaved = false,
                busyAnimationPx = ShowsNewText ? AnimationBox : (RectPx?)null, animationRunning = Busy, busyAnimationIntervalMs = Busy ? AnimationIntervalMs : (int?)null,
                newTextShown = ShowsNewText, busyNewTextOriginPx = ShowsNewText ? new[] { NewTextTarget.X, NewTextTarget.Y } : null, busyNewTextFontPx = ShowsNewText ? 56 : (int?)null,
                brightSpotPx = BrightSpot,
                brightSpotMeaning = BrightSpot is null ? null
                    : "White square under the label inside the old glyph box; stays after the label disappears, so no pixel evidence of absence exists",
                junkFixture = Junk ? new
                {
                    markerPx = MarkerSize, markerColor = "#900090 inside the old glyph box (luminance below the remaining-contrast threshold of the white label)",
                    meaningful = junkMeaningful, quality = junkQuality, inspectQuality,
                    qualityBelowClean = junkQuality < 0.9, qualityWorseByMoreThanTolerance = junkQuality < inspectQuality - 0.1,
                    similarityToInspect = junkSimilarity, stabilizerFirstDecision = firstDecision.ToString(),
                    stabilizerSecondDecision = secondDecision.ToString(), ghostResurrectionCondition = ghostCondition,
                    boxRule = "Expected old glyph box shifted right by 15% of its width; overlap of smaller box is logged per reading",
                    requiredOverlapOfSmaller = 0.5,
                    ghostCycle = Ghost ? "empty, empty, empty, junk; repeated for every OCR covering the marker without glyphs" : null,
                } : null,
                timingMeaning = "From WPF Rendering after the label disappears to the first session callback without the old block; not physical overlay presentation",
                resultMeaning = ShowsNewText
                    ? "Old label removed and never returns; the new text is shown"
                    : "Old label removed and never returns",
            });
        }

        public void Event(object data) { lock (_gate) Write(data); }
        private void Write(object data) => _writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs = _clock.Elapsed.TotalMilliseconds, data }));

        public void RecordGroundTruth(GroundTruth truth)
        {
            lock (_gate)
            {
                _truth = truth;
                Write(new
                {
                    type = "ground_truth", truth.CaptureWidth, truth.CaptureHeight, truth.ScreenFallback,
                    glyphBoxPx = truth.GlyphBox, truth.GlyphWhite, markerPx = truth.Marker,
                    textRgb = truth.TextRgb.ToString("X6", CultureInfo.InvariantCulture),
                    backgroundRgb = truth.BackgroundRgb.ToString("X6", CultureInfo.InvariantCulture),
                    truth.KnownContrast, afterWhiteInGlyphBox = truth.AfterWhite, afterClearlyEmpty = truth.AfterClearlyEmpty,
                    afterKnownTextAbsent = truth.AfterKnownTextAbsent,
                    afterChannelRangeRgb = truth.AfterChannelRange, truth.ChangedPixelFraction,
                    truth.GridChangedFraction, truth.GridSignificantFraction, gridSignificantRegionPx = truth.GridSignificantRegion,
                    newTextWhite = truth.NewTextWhite,
                    meaning = "Pre-session captures of the same window: label visible, label hidden, label restored",
                });
            }
        }

        public void BeginMeasurement(UsageTracker usage)
        {
            lock (_gate)
            {
                _changeAt = _clock.Elapsed.TotalMilliseconds;
                _oldVisibleSince = _visibleOld ? _changeAt : null;
                _requestsAtChange = usage.ApiRequests;
                _charactersAtChange = usage.ApiCharacters;
                Write(new { type = "label_hidden_rendering", newTextShown = ShowsNewText, mockRequestsAtChange = _requestsAtChange, oldVisible = _visibleOld });
            }
        }

        public void EndMeasurement()
        {
            lock (_gate)
            {
                _ended = true;
                _endedAt = _clock.Elapsed.TotalMilliseconds;
            }
        }

        public void Tick()
        {
            lock (_gate)
            {
                _ticks++;
                if (_changeAt is not null && !_ended) _ticksAfterChange++;
            }
        }

        private bool Measuring => _changeAt is not null && !_ended;

        public void WindowsOcr(OcrResult result, int width, int height, double started)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                var hasInspect = result.Lines.Any(static line => IsOld(TextNormalizer.Normalize(line.Text)));
                var hasNew = result.Lines.Any(static line => IsNew(TextNormalizer.Normalize(line.Text)));
                var meaningful = result.Lines.Count(static line => JunkFilter.IsMeaningful(line.Text));
                var other = result.Lines.Count(static line => JunkFilter.IsMeaningful(line.Text)
                    && !IsOld(TextNormalizer.Normalize(line.Text)) && !IsNew(TextNormalizer.Normalize(line.Text)));
                var full = width == FrameWidth && height == FrameHeight;
                var startedAfterChange = _changeAt is { } change && started > change;
                var settled = _changeAt is { } changed && started >= changed + SettledReadingMs;
                if (Measuring && startedAfterChange)
                {
                    _ocrCalls++;
                    if (full) _ocrFull++;
                    if (hasInspect) _ocrInspect++;
                    if (hasInspect && settled) _ocrInspectSettled++;
                    if (hasNew)
                    {
                        _ocrNew++;
                        _newFirstOcrAt ??= now;
                    }
                    if (result.Lines.Count == 0) _ocrEmpty++;
                    if (other > 0) _ocrOtherMeaningful++;
                    _firstOcrAfterChangeAt ??= now;
                }
                Write(new
                {
                    type = "ocr_completed", measured = Measuring, startedAfterChange, settled, width, height, full,
                    rawLines = result.Lines.Count, meaningfulLines = meaningful, hasInspect, hasNew, otherMeaningfulLines = other,
                    startedMs = started, sinceChangeMs = now - _changeAt,
                });
            }
        }

        public void ScriptedOcr(string kind, double scale, double whiteRatio, double? overlap, int width, int height, double started)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                var startedAfterChange = _changeAt is { } change && started > change;
                var settled = _changeAt is { } changed && started >= changed + SettledReadingMs;
                var full = width == FrameWidth && height == FrameHeight;
                if (Measuring && startedAfterChange)
                {
                    _ocrCalls++;
                    if (full) _ocrFull++;
                    _firstOcrAfterChangeAt ??= now;
                    switch (kind)
                    {
                        case "junk":
                            _junkReadings++;
                            _minJunkOverlap = Math.Min(_minJunkOverlap, overlap ?? 0);
                            break;
                        case "empty": _emptyScripted++; break;
                        case "no-marker": _noMarker++; break;
                        case "inspect":
                            _ocrInspect++;
                            if (settled) _inspectScriptedSettled++;
                            break;
                    }
                }
                Write(new
                {
                    type = "ocr_completed", measured = Measuring, startedAfterChange, settled, kind, scale, whiteRatio,
                    junkOverlapOfSmaller = overlap, width, height, full, startedMs = started, sinceChangeMs = now - _changeAt,
                });
            }
        }

        public void Update(LiveUpdate update)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                _screenFallback |= update.Diagnostics?.UsedScreenFallback ?? false;
                if (update.Stopped)
                {
                    _stoppedDuringMeasurement |= Measuring;
                    Write(new { type = "session_stopped", measured = Measuring });
                    return;
                }
                if (_ended) return;
                var visual = update.Blocks is not null || update.ClearOverlay || update.HideOverlay;
                var wasVisibleOld = _visibleOld;
                var other = 0;
                if (update.ClearOverlay || update.HideOverlay)
                {
                    _visibleOld = false;
                    _visibleNew = false;
                }
                if (update.Blocks is { } blocks)
                {
                    _visibleOld = blocks.Any(static block => IsOld(block.TranslatedText));
                    _visibleNew = blocks.Any(static block => IsNew(block.TranslatedText));
                    other = blocks.Count(static block => !IsOld(block.TranslatedText) && !IsNew(block.TranslatedText));
                    if (_changeAt is null && _visibleOld && _initialOldBox is null)
                    {
                        var oldBlock = blocks.First(static block => IsOld(block.TranslatedText));
                        _initialOldBox = oldBlock.ScreenBox.Offset(-update.WindowBounds.X, -update.WindowBounds.Y);
                    }
                }
                if (_changeAt is null && _visibleOld)
                {
                    _initialDisplayedAt ??= now;
                    InitialDisplayed.TrySetResult();
                }
                var measured = Measuring;
                if (measured)
                {
                    if (visual)
                    {
                        _visualUpdates++;
                        _maxOtherBlocks = Math.Max(_maxOtherBlocks, other);
                        if (wasVisibleOld && !_visibleOld && _oldVisibleSince is { } since)
                        {
                            _oldVisibleTotalMs += now - since;
                            _oldVisibleSince = null;
                            _lastOldRemovedAt = now;
                        }
                        else if (!wasVisibleOld && _visibleOld)
                        {
                            _oldVisibleSince = now;
                        }
                        if (!_visibleOld && !_oldRemoved)
                        {
                            _oldRemoved = true;
                            _oldRemovedAt = now;
                        }
                        else if (_oldRemoved && _visibleOld)
                        {
                            _oldVisibleCallbacksAfterRemoval++;
                            if (!wasVisibleOld) _oldReturnTransitions++;
                        }
                        if (_visibleNew) _newReadyAt ??= now;
                    }
                    if (update.ClearOverlay) _clears++;
                    if (update.HideOverlay) _hides++;
                    if (update.Diagnostics is { } frame)
                    {
                        _completedOcr++;
                        _firstOcrCallbackAfterChangeAt ??= now;
                        if (frame.PartialOcr) _partialOcr++;
                        else
                        {
                            _fullOcr++;
                            _firstFullOcrAfterChangeAt ??= now;
                            var previous = _lastFullOcrAt ?? _changeAt!.Value;
                            _maxFullOcrGapMs = Math.Max(_maxFullOcrGapMs, now - previous);
                            _lastFullOcrAt = now;
                        }
                        if (frame.SceneCut) _sceneCuts++;
                        if (frame.WhiffSuspected) _whiffs++;
                        _retained += frame.RetainedBlocks;
                        if (frame.RetainedBlocks > 0) _firstRetainedAt ??= now;
                        _reused += frame.ReusedBlocks;
                        if (frame.ReusedBlocks > 0) _reusedFrames++;
                    }
                }
                Write(new
                {
                    type = "update", measured, sinceChangeMs = now - _changeAt, visualUpdate = visual, blockCount = update.Blocks?.Count,
                    visibleOld = _visibleOld, visibleNew = _visibleNew, otherBlocks = other,
                    update.ClearOverlay, update.HideOverlay, completedOcr = update.Diagnostics is not null,
                    partialOcr = update.Diagnostics?.PartialOcr, ocrWidth = update.Diagnostics?.OcrWidth, ocrHeight = update.Diagnostics?.OcrHeight,
                    rawLines = update.Diagnostics?.RawLines, recognizedBlocks = update.Diagnostics?.RecognizedBlocks,
                    sceneCut = update.Diagnostics?.SceneCut, whiffSuspected = update.Diagnostics?.WhiffSuspected,
                    retainedBlocks = update.Diagnostics?.RetainedBlocks, reusedBlocks = update.Diagnostics?.ReusedBlocks,
                    translateMs = update.Diagnostics?.TranslateMs, ocrMs = update.Diagnostics?.OcrMs,
                });
            }
        }

        private bool TruthValid => _truth is { } t && t.CaptureWidth == FrameWidth && t.CaptureHeight == FrameHeight
            && !t.ScreenFallback && t.GlyphWhite >= 1000 && t.AfterWhite * 20 <= t.GlyphWhite && !t.AfterClearlyEmpty
            && (!Junk || t.Marker is not null) && (!ShowsNewText || t.NewTextWhite >= 1000)
            && (BrightSpot is null || !t.AfterKnownTextAbsent);

        public bool FixtureValid
        {
            get
            {
                lock (_gate)
                {
                    var common = TruthValid && _changeAt is not null && _initialDisplayedAt is not null && !_screenFallback
                        && !_stoppedDuringMeasurement && _sceneCuts == 0 && _completedOcr >= 1;
                    if (Junk)
                        return common && _junkReadings >= (Ghost ? 1 : 2) && _inspectScriptedSettled == 0 && _minJunkOverlap >= 0.5;
                    if (ShowsNewText)
                        return common && _ocrNew >= 1 && _ocrInspectSettled == 0
                            && (!Busy || _ticksAfterChange >= ObservationMs / AnimationIntervalMs / 2);
                    return common && _ocrInspectSettled == 0;
                }
            }
        }

        public int Finish(int exitCode, string reason, UsageTracker usage)
        {
            lock (_gate)
            {
                var valid = FixtureValid;
                var expected = valid && _oldRemoved && _oldReturnTransitions == 0 && !_visibleOld
                    && (!ShowsNewText || (_newReadyAt is not null && _visibleNew));
                var tail = _endedAt is { } end && _changeAt is not null ? end - (_lastFullOcrAt ?? _changeAt.Value) : (double?)null;
                var visibleTotal = _oldVisibleTotalMs + (_oldVisibleSince is { } visibleSince && _endedAt is { } stop ? stop - visibleSince : 0);
                var maxGap = tail is { } t ? Math.Max(_maxFullOcrGapMs, t) : _maxFullOcrGapMs;
                double? iou = null;
                if (_initialOldBox is { } box && _truth is { } truth)
                {
                    var intersection = Area(box.Intersect(truth.GlyphBox));
                    var union = Area(box) + Area(truth.GlyphBox) - intersection;
                    iou = union > 0 ? (double)intersection / union : null;
                }
                Write(new
                {
                    type = "summary", exitCode, reason, fixtureValid = valid, expectedBehavior = expected, scenario = _scenario,
                    screenFallback = _screenFallback, stoppedDuringMeasurement = _stoppedDuringMeasurement,
                    staleLifetimeMs = _oldRemovedAt - _changeAt, oldRemoved = _oldRemoved,
                    oldNotRemovedAfterObservationMs = _oldRemoved ? (double?)null : _endedAt - _changeAt,
                    oldReturnTransitions = _oldReturnTransitions, oldVisibleCallbacksAfterRemoval = _oldVisibleCallbacksAfterRemoval,
                    oldVisibleAtEnd = _visibleOld, oldVisibleMsAfterChange = _changeAt is null ? (double?)null : visibleTotal,
                    lastOldRemovedMs = _lastOldRemovedAt - _changeAt,
                    newReadyMs = ShowsNewText ? _newReadyAt - _changeAt : null,
                    newFirstOcrMs = ShowsNewText ? _newFirstOcrAt - _changeAt : null, newVisibleAtEnd = ShowsNewText ? _visibleNew : (bool?)null,
                    initialDisplayedMs = _initialDisplayedAt, initialOldBoxWindowPx = _initialOldBox, initialOldBoxIouWithGlyphs = iou,
                    observationMs = _endedAt - _changeAt, visualUpdates = _visualUpdates, maxOtherBlocks = _maxOtherBlocks,
                    clearCallbacks = _clears, hideCallbacks = _hides,
                    completedOcrCallbacksAfterChange = _completedOcr, fullOcrCallbacksAfterChange = _fullOcr,
                    partialOcrCallbacksAfterChange = _partialOcr, firstFullOcrCallbackAfterChangeMs = _firstFullOcrAfterChangeAt - _changeAt,
                    maxFullOcrGapMsIncludingEdges = maxGap, firstOcrCompletedAfterChangeMs = _firstOcrAfterChangeAt - _changeAt,
                    firstOcrCallbackAfterChangeMs = _firstOcrCallbackAfterChangeAt - _changeAt,
                    firstRetainedCallbackAfterChangeMs = _firstRetainedAt - _changeAt,
                    sceneCutFrames = _sceneCuts, whiffSuspectedFrames = _whiffs, retainedBlocks = _retained,
                    reusedBlocks = _reused, framesWithReusedBlocks = _reusedFrames,
                    ocrCallsAfterChange = _ocrCalls, fullOcrCallsAfterChange = _ocrFull, inspectReadingsAfterChange = _ocrInspect,
                    inspectReadingsSettled = Junk ? _inspectScriptedSettled : _ocrInspectSettled,
                    newTextReadings = _ocrNew, emptyOcrReadings = Junk ? _emptyScripted : _ocrEmpty,
                    otherMeaningfulReadings = _ocrOtherMeaningful, junkReadings = _junkReadings, noMarkerReadings = _noMarker,
                    minJunkOverlapOfSmaller = double.IsPositiveInfinity(_minJunkOverlap) ? (double?)null : _minJunkOverlap,
                    animationTicks = _ticks, animationTicksAfterChange = _ticksAfterChange,
                    mockProviderRequests = usage.ApiRequests, mockRequestsAfterChange = usage.ApiRequests - _requestsAtChange,
                    mockProviderCharacters = usage.ApiCharacters, mockCharactersAfterChange = usage.ApiCharacters - _charactersAtChange,
                    usage.CacheHits, usage.GlossaryHits,
                });
                Console.WriteLine($"StaleLabelReplay: {reason}; fixture={valid}; expected={expected}; stary napis {(_oldRemoved ? $"usuniety po {_oldRemovedAt - _changeAt:F0} ms" : "nie usuniety")}, powroty {_oldReturnTransitions}.");
            }
            return exitCode;
        }

        public void Dispose() => _writer.Dispose();
    }
}
