using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;
using CoreTextBlock = GameTranslatorOverlay.Core.Text.TextBlock;

namespace GameTranslatorOverlay.OverlayPreview;

internal sealed record PreviewBlock(
    int Number, string Key, string Text, RectPx Box, int LineHeight, string? Translation, string Origin, BlockColors Colors,
    GlyphCover? Cover = null);

internal sealed record RejectedBlock(string Text, RectPx Box);

internal sealed record FrameAnalysis(
    string FramePath, OcrBitmap Frame, IReadOnlyList<PreviewBlock> Blocks, IReadOnlyList<RejectedBlock> Rejected,
    int RawLines, double OcrFactor, long OcrMs, bool FromJson);

internal sealed record InputBlock(string Text, int X, int Y, int Width, int Height, int? LineHeight, string? Translation);

internal static class FrameAnalyzer
{
    public static async Task<FrameAnalysis> AnalyzeAsync(string framePath, PreviewTranslator translator, string? blocksPath, string coverMode = "crisp")
    {
        var frame = LoadFrame(framePath);
        if (blocksPath is not null) return await FromJsonAsync(framePath, frame, translator, blocksPath, coverMode).ConfigureAwait(false);

        var orchestrator = translator.Orchestrator;
        var preference = OcrScaling.ResolvePreference(orchestrator.ActiveProfile?.Ocr?.Upscale, translator.Settings.OcrUpscale);
        var maxDimension = translator.Ocr.MaxImageDimension;
        var preferredUpscale = frame.Width >= 1000 || frame.Height >= 700 ? 0.0 : preference.Preferred;
        var downscale = OcrScaling.ComputeDownscale(frame.Width, frame.Height, maxDimension);
        var factor = downscale < 1.0
            ? downscale
            : OcrScaling.ComputeUpscale(frame.Width, frame.Height, maxDimension, preferredUpscale, preference.AllowAuto);

        var ocrFrame = frame;
        var scaleBack = 1.0;
        if (Math.Abs(factor - 1.0) > 0.001)
        {
            using var bitmap = ToBitmap(frame);
            using var scaled = ScreenCapture.Rescale(bitmap, factor);
            ocrFrame = ScreenCapture.ToOcrBitmap(scaled);
            scaleBack = 1.0 / factor;
        }

        var watch = Stopwatch.StartNew();
        var result = await translator.Ocr.RecognizeAsync(ocrFrame, translator.Settings.SourceLanguage).ConfigureAwait(false);
        var ocrMs = watch.ElapsedMilliseconds;
        var lines = result.Lines;
        if (Math.Abs(scaleBack - 1.0) > 0.001)
        {
            lines = lines
                .Select(line => new OcrLine(
                    line.Text,
                    line.Box.Scale(scaleBack),
                    line.Words.Select(w => new OcrWord(w.Text, w.Box.Scale(scaleBack))).ToList()))
                .ToList();
        }

        var grouped = TextBlockGrouper.Group(lines);
        var accepted = grouped.Where(block => orchestrator.ShouldTranslateLive(block.Text)).ToList();
        var rejected = grouped.Except(accepted).Select(static b => new RejectedBlock(b.Text, b.Box)).ToList();
        var keyed = LiveBlockKeyer.AssignKeys(accepted, orchestrator.CorpusIdentity);

        var colors = keyed.Select(k =>
        {
            var box = k.Block.Box;
            var sampleBox = new RectPx(
                (int)(box.X / scaleBack),
                (int)(box.Y / scaleBack),
                Math.Max(1, (int)(box.Width / scaleBack)),
                Math.Max(1, (int)(box.Height / scaleBack)));
            return BlockColorSampler.SampleColors(ocrFrame.PixelsBgra32, ocrFrame.Width, ocrFrame.Height, ocrFrame.Stride, sampleBox);
        }).ToList();

        var covers = keyed.Select(k => coverMode == "off" ? null : GlyphCoverBuilder.BuildForBlock(ocrFrame, k.Block, default, scaleBack, coverMode == "soft")).ToList();
        var outcomes = await translator.TranslateAsync(keyed.Select(static k => k.Block.Text).ToList()).ConfigureAwait(false);
        var blocks = new List<PreviewBlock>(keyed.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < keyed.Count; i++)
        {
            var key = keyed[i].Key;
            while (!keys.Add(key)) key += "'";
            blocks.Add(new PreviewBlock(
                i + 1, key, keyed[i].Block.Text, keyed[i].Block.Box, TextBlockMetrics.MedianLineHeight(keyed[i].Block),
                outcomes[i].TranslatedText, PreviewTranslator.OriginLabel(outcomes[i]), colors[i], covers[i]));
        }

        return new FrameAnalysis(framePath, frame, blocks, rejected, result.Lines.Count, factor, ocrMs, FromJson: false);
    }

    private static async Task<FrameAnalysis> FromJsonAsync(string framePath, OcrBitmap frame, PreviewTranslator translator, string blocksPath, string coverMode)
    {
        var input = ReadInput(blocksPath);
        var ordered = input
            .Select(static b => (Input: b, Block: new CoreTextBlock(b.Text, new RectPx(b.X, b.Y, b.Width, b.Height), [])))
            .ToList();
        var keyed = LiveBlockKeyer.AssignKeys(ordered.Select(static o => o.Block).ToList(), translator.Orchestrator.CorpusIdentity);
        var sources = keyed.Select(k => ordered.First(o => ReferenceEquals(o.Block, k.Block)).Input).ToList();

        var pending = sources.Select((s, i) => (s, i)).Where(static p => p.s.Translation is null).ToList();
        var outcomes = await translator.TranslateAsync(pending.Select(static p => p.s.Text).ToList()).ConfigureAwait(false);
        var translated = new Dictionary<int, (string? Text, string Origin)>();
        for (var j = 0; j < pending.Count; j++)
            translated[pending[j].i] = (outcomes[j].TranslatedText, PreviewTranslator.OriginLabel(outcomes[j]));

        var blocks = new List<PreviewBlock>(keyed.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < keyed.Count; i++)
        {
            var source = sources[i];
            var box = keyed[i].Block.Box;
            var lineCount = Math.Max(1, source.Text.Count(static c => c == '\n') + 1);
            var lineHeight = source.LineHeight is > 0 ? source.LineHeight.Value : Math.Max(1, box.Height / lineCount);
            var (text, origin) = source.Translation is { } given ? (given, "JSON") : translated[i];
            var key = keyed[i].Key;
            while (!keys.Add(key)) key += "'";
            var colors = BlockColorSampler.SampleColors(frame.PixelsBgra32, frame.Width, frame.Height, frame.Stride, box);
            var lineBoxes = Enumerable.Range(0, lineCount)
                .Select(l => new RectPx(box.X, box.Y + l * box.Height / lineCount, box.Width, box.Height / lineCount))
                .ToList();
            var cover = coverMode == "off" ? null : GlyphCoverBuilder.Build(frame, box, lineBoxes, source.Text.Split('\n'), soft: coverMode == "soft");
            blocks.Add(new PreviewBlock(i + 1, key, source.Text, box, lineHeight, text, origin, colors, cover));
        }

        return new FrameAnalysis(framePath, frame, blocks, [], 0, 1.0, 0, FromJson: true);
    }

    private static List<InputBlock> ReadInput(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("blocks", out var blocks) ? blocks
            : throw new ArgumentException("Plik bloków: oczekiwano tablicy albo obiektu z polem „blocks”.");
        var result = new List<InputBlock>();
        foreach (var item in array.EnumerateArray())
        {
            var text = item.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Plik bloków: każdy blok potrzebuje pola „text”.");
            result.Add(new InputBlock(
                text,
                Required(item, "x"), Required(item, "y"), Required(item, "width"), Required(item, "height"),
                item.TryGetProperty("lineHeight", out var lh) && lh.ValueKind == JsonValueKind.Number ? lh.GetInt32() : null,
                item.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.String ? tr.GetString() : null));
        }
        return result;
    }

    private static int Required(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new ArgumentException($"Plik bloków: brak liczby „{name}”.");

    private static OcrBitmap LoadFrame(string path)
    {
        using var loaded = new Bitmap(path);
        using var copy = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(copy))
        {
            graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
        }
        return ScreenCapture.ToOcrBitmap(copy);
    }

    private static Bitmap ToBitmap(OcrBitmap frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < frame.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame.PixelsBgra32, y * frame.Stride, data.Scan0 + y * data.Stride, frame.Width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
