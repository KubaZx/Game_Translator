using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.CorpusEval;

internal sealed record OcrBlockRecord(string Text, int X, int Y, int Width, int Height, bool OverlapsText);

internal sealed record OcrSampleRecord
{
    public int Id { get; init; }
    public string Dataset { get; init; } = "";
    public string Stratum { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Truth { get; init; } = "";
    public string TruthKey { get; init; } = "";
    public string Font { get; init; } = "";
    public int Size { get; init; }
    public bool Bold { get; init; }
    public int Background { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public int TextLines { get; init; }
    public int BackgroundRetries { get; init; }
    public long OcrMs { get; init; }
    public List<OcrBlockRecord> Blocks { get; init; } = [];
}

internal static class RenderCommand
{
    private static readonly RectPx[] HudAreas =
    [
        new(1600, 0, 700, 200),
        new(0, 1750, 800, 410),
        new(1800, 980, 600, 200),
        new(150, 980, 3300, 300),
        new(3350, 1420, 400, 200),
    ];

    private static readonly string[] Fonts =
        ["Segoe UI", "Arial", "Verdana", "Trebuchet MS", "Calibri", "Tahoma", "Bahnschrift", "Corbel", "Candara"];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(EvalArgs args)
    {
        var corpus = CorpusJsonl.ReadFile(args.Required("corpus"));
        var backgroundPaths = args.Required("backgrounds").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var output = args.Required("out");
        var count = args.Int("count", 440);
        var seed = args.Int("seed", 1);
        var examples = args.Optional("examples");
        if (examples is not null) Directory.CreateDirectory(examples);

        var samples = Sample(corpus, count, new Random(seed));
        var backgrounds = backgroundPaths.Select(static p => LoadArgb(p)).ToList();
        var ocr = new WindowsOcrProvider();
        var random = new Random(seed * 7919 + 13);
        var records = new List<OcrSampleRecord>();
        var fullFrame = (args.Optional("mode") ?? "full") == "full";
        var backgroundText = new List<List<RectPx>>();
        foreach (var background in backgrounds)
        {
            var result = await ocr.RecognizeAsync(ScreenCapture.ToOcrBitmap(background), "en");
            backgroundText.Add(result.Lines.Select(static l => l.Box).Concat(HudAreas).ToList());
        }

        for (var i = 0; i < samples.Count; i++)
        {
            var (stratum, entry) = samples[i];
            var exampleDirectory = examples is not null && i < 24 ? examples : null;
            var record = fullFrame
                ? await RenderFull(i, stratum, entry, backgrounds, backgroundText, ocr, random, exampleDirectory)
                : await RenderOne(i, stratum, entry, backgrounds, backgroundText, ocr, random, exampleDirectory);
            records.Add(record);
            if ((i + 1) % 50 == 0) Console.WriteLine($"  {i + 1}/{samples.Count}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await using (var writer = new StreamWriter(output, false, new System.Text.UTF8Encoding(false)))
        {
            foreach (var record in records) await writer.WriteLineAsync(JsonSerializer.Serialize(record, Json));
        }
        Console.WriteLine($"Zapisano {records.Count} próbek OCR: {output}");
        foreach (var b in backgrounds) b.Dispose();
        return 0;
    }

    private static List<(string Stratum, CorpusEntry Entry)> Sample(IReadOnlyList<CorpusEntry> corpus, int count, Random random)
    {
        var distinct = corpus
            .GroupBy(static e => CorpusText.MatchKey(e.En))
            .Select(static g => g.First())
            .Where(static e => CorpusText.LetterOrDigitCount(e.En) >= 2 && e.En.Length <= 220 && !e.En.Contains('\t'))
            .ToList();

        var strata = new (string Name, Func<CorpusEntry, bool> Filter, double Share)[]
        {
            ("ui-krotkie", static e => e.Kind == CorpusEntryKind.Ui && CorpusText.WordCount(CorpusText.MatchKey(e.En)) <= 2, 0.18),
            ("ui-dluzsze", static e => e.Kind == CorpusEntryKind.Ui && CorpusText.WordCount(CorpusText.MatchKey(e.En)) > 2, 0.28),
            ("dialog", static e => e.Kind == CorpusEntryKind.Dialog, 0.27),
            ("napis", static e => e.Kind == CorpusEntryKind.Subtitle, 0.27),
        };

        var result = new List<(string, CorpusEntry)>();
        foreach (var (name, filter, share) in strata)
        {
            var pool = distinct.Where(filter).OrderBy(_ => random.Next()).ToList();
            var take = (int)Math.Round(count * share);
            result.AddRange(pool.Take(take).Select(e => (name, e)));
        }
        return result.OrderBy(_ => random.Next()).ToList();
    }

    private static Bitmap LoadArgb(string path)
    {
        using var loaded = new Bitmap(path);
        var copy = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
        return copy;
    }

    private static (GraphicsPath Path, RectangleF Bounds, string Font, int Size, bool Bold) Layout(string stratum, string text, Random random, int maxTotalWidth, int maxTotalHeight)
    {
        var fontName = Fonts[random.Next(Fonts.Length)];
        var size = random.Next(32, 73);
        var bold = random.NextDouble() < 0.3;
        var maxWidth = stratum == "ui-krotkie" ? 3400 : random.Next(1100, 2700);
        using var family = new FontFamily(fontName);
        while (true)
        {
            var path = new GraphicsPath();
            using var format = new StringFormat(StringFormatFlags.NoClip);
            path.AddString(text, family, (int)(bold ? FontStyle.Bold : FontStyle.Regular), size, new RectangleF(0, 0, maxWidth, 20000), format);
            var bounds = path.GetBounds();
            if (bounds.Width <= maxTotalWidth && bounds.Height <= maxTotalHeight) return (path, bounds, fontName, size, bold);
            path.Dispose();
            size = Math.Max(24, size - 6);
            maxWidth = Math.Min(3400, maxWidth + 300);
        }
    }

    private static async Task<OcrSampleRecord> RenderFull(int id, string stratum, CorpusEntry entry, List<Bitmap> backgrounds,
        List<List<RectPx>> backgroundText, WindowsOcrProvider ocr, Random random, string? exampleDirectory)
    {
        var text = entry.En;
        var backgroundIndex = random.Next(backgrounds.Count);
        var background = backgrounds[backgroundIndex];
        var (path, bounds, fontName, size, bold) = Layout(stratum, text, random, background.Width - 200, background.Height - 200);
        using (path)
        {
            var width = (int)Math.Ceiling(bounds.Width);
            var height = (int)Math.Ceiling(bounds.Height);
            var x = 0;
            var y = 0;
            var retries = 0;
            for (; retries < 200; retries++)
            {
                x = random.Next(60, Math.Max(61, background.Width - width - 60));
                y = random.Next(60, Math.Max(61, background.Height - height - 60));
                var guard = new RectPx(x - 220, y - 260, width + 440, height + 520);
                if (!backgroundText[backgroundIndex].Any(box => Overlaps(box, guard))) break;
            }

            using var image = (Bitmap)background.Clone();
            using (var g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(x - bounds.X, y - bounds.Y);
                using var pen = new Pen(Color.Black, Math.Max(2f, size * 0.12f)) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                g.FillPath(Brushes.White, path);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await ocr.RecognizeAsync(ScreenCapture.ToOcrBitmap(image), "en");
            watch.Stop();
            if (exampleDirectory is not null)
            {
                var view = new Rectangle(Math.Max(0, x - 200), Math.Max(0, y - 120), Math.Min(width + 400, image.Width - Math.Max(0, x - 200)), Math.Min(height + 240, image.Height - Math.Max(0, y - 120)));
                using var cut = image.Clone(view, PixelFormat.Format32bppArgb);
                cut.Save(Path.Combine(exampleDirectory, $"probka-{id:D3}.png"), ImageFormat.Png);
            }

            var textRect = new RectPx(x - 6, y - 6, width + 12, height + 12);
            var blocks = TextBlockGrouper.Group(result.Lines)
                .Where(static b => JunkFilter.IsMeaningful(b.Text))
                .Select(b => new OcrBlockRecord(b.Text, b.Box.X, b.Box.Y, b.Box.Width, b.Box.Height, Overlaps(b.Box, textRect)))
                .Where(b => b.OverlapsText)
                .ToList();

            return new OcrSampleRecord
            {
                Id = id,
                Stratum = stratum,
                Kind = entry.Kind.ToString().ToLowerInvariant(),
                Truth = text,
                TruthKey = CorpusText.MatchKey(text),
                Font = fontName,
                Size = size,
                Bold = bold,
                Background = backgroundIndex,
                CropWidth = image.Width,
                CropHeight = image.Height,
                TextLines = result.Lines.Count(l => Overlaps(l.Box, textRect)),
                BackgroundRetries = retries,
                OcrMs = watch.ElapsedMilliseconds,
                Blocks = blocks,
            };
        }
    }

    private static async Task<OcrSampleRecord> RenderOne(int id, string stratum, CorpusEntry entry, List<Bitmap> backgrounds,
        List<List<RectPx>> backgroundText, WindowsOcrProvider ocr, Random random, string? exampleDirectory)
    {
        var fontName = Fonts[random.Next(Fonts.Length)];
        var size = random.Next(32, 73);
        var bold = random.NextDouble() < 0.3;
        var maxWidth = stratum == "ui-krotkie" ? 3400 : random.Next(1100, 2700);
        var margin = random.Next(24, 121);
        var text = entry.En;

        using var family = new FontFamily(fontName);
        GraphicsPath path;
        RectangleF bounds;
        while (true)
        {
            path = new GraphicsPath();
            using var format = new StringFormat(StringFormatFlags.NoClip);
            path.AddString(text, family, (int)(bold ? FontStyle.Bold : FontStyle.Regular), size, new RectangleF(0, 0, maxWidth, 20000), format);
            bounds = path.GetBounds();
            if (bounds.Width + 2 * margin <= 3800 && bounds.Height + 2 * margin <= 2100) break;
            path.Dispose();
            size = Math.Max(24, size - 6);
            maxWidth = Math.Min(3400, maxWidth + 300);
        }

        using (path)
        {
            var marginX = Math.Max(margin, (800 - (int)Math.Ceiling(bounds.Width)) / 2);
            var marginY = Math.Max(margin, (400 - (int)Math.Ceiling(bounds.Height)) / 2);
            var cropWidth = (int)Math.Ceiling(bounds.Width) + 2 * marginX;
            var cropHeight = (int)Math.Ceiling(bounds.Height) + 2 * marginY;
            var backgroundIndex = random.Next(backgrounds.Count);
            var background = backgrounds[backgroundIndex];
            Rectangle crop = default;
            var retries = 0;
            for (; retries < 400; retries++)
            {
                var x = random.Next(0, Math.Max(1, background.Width - cropWidth));
                var y = random.Next(0, Math.Max(1, background.Height - cropHeight));
                crop = new Rectangle(x, y, Math.Min(cropWidth, background.Width - x), Math.Min(cropHeight, background.Height - y));
                var cropBox = new RectPx(crop.X - 40, crop.Y - 40, crop.Width + 80, crop.Height + 80);
                if (backgroundText[backgroundIndex].Any(box => Overlaps(box, cropBox))) continue;
                using var plain = background.Clone(crop, PixelFormat.Format32bppArgb);
                var empty = await ocr.RecognizeAsync(ScreenCapture.ToOcrBitmap(plain), "en");
                if (empty.Lines.Count == 0) break;
            }

            using var image = background.Clone(crop, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(marginX - bounds.X, marginY - bounds.Y);
                using var pen = new Pen(Color.Black, Math.Max(2f, size * 0.12f)) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                g.FillPath(Brushes.White, path);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await ocr.RecognizeAsync(ScreenCapture.ToOcrBitmap(image), "en");
            watch.Stop();
            if (exampleDirectory is not null) image.Save(Path.Combine(exampleDirectory, $"probka-{id:D3}.png"), ImageFormat.Png);

            var textRect = new RectPx(marginX - 4, marginY - 4, (int)Math.Ceiling(bounds.Width) + 8, (int)Math.Ceiling(bounds.Height) + 8);
            var blocks = TextBlockGrouper.Group(result.Lines)
                .Where(static b => JunkFilter.IsMeaningful(b.Text))
                .Select(b => new OcrBlockRecord(b.Text, b.Box.X, b.Box.Y, b.Box.Width, b.Box.Height, Overlaps(b.Box, textRect)))
                .ToList();

            return new OcrSampleRecord
            {
                Id = id,
                Stratum = stratum,
                Kind = entry.Kind.ToString().ToLowerInvariant(),
                Truth = text,
                TruthKey = CorpusText.MatchKey(text),
                Font = fontName,
                Size = size,
                Bold = bold,
                Background = backgroundIndex,
                CropWidth = crop.Width,
                CropHeight = crop.Height,
                TextLines = result.Lines.Count,
                BackgroundRetries = retries,
                OcrMs = watch.ElapsedMilliseconds,
                Blocks = blocks,
            };
        }
    }

    private static bool Overlaps(RectPx a, RectPx b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
