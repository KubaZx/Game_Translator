using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.OverlayPreview;

internal sealed record FrameGallery(string Name, string Directory, FrameAnalysis Analysis, RenderedOverlay Overlay, IReadOnlyList<string> Files);

internal static class GalleryWriter
{
    private static readonly Typeface Label = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush OcrBoxBrush = Frozen(Color.FromRgb(0x00, 0xE5, 0xFF));
    private static readonly Brush PatchBrush = Frozen(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xFF, 0x4F, 0xD8));
    private static readonly Brush RejectedBrush = Frozen(Color.FromRgb(0xFF, 0x52, 0x52));
    private static readonly Brush MockBrush = Frozen(Color.FromRgb(0xFF, 0x9E, 0x40));
    private static readonly Brush HeaderBrush = Frozen(Color.FromRgb(0x1A, 0x1D, 0x21));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static FrameGallery Write(string name, string directory, FrameAnalysis analysis, RenderedOverlay overlay, AppSettings settings, double dpi, int zoom)
    {
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        var original = Pixels.FromOpaque(analysis.Frame);
        var composite = overlay.Layer.CompositeOver(original);

        Save(composite, Path.Combine(directory, "nakladka.png"), files);
        Save(overlay.Layer, Path.Combine(directory, "warstwa.png"), files);
        Save(SideBySide(name, original, composite, analysis, overlay, settings, dpi), Path.Combine(directory, "porownanie.png"), files);

        foreach (var block in overlay.Blocks)
        {
            var path = Path.Combine(directory, "bloki", $"blok-{block.Block.Number:00}.png");
            Save(BlockSheet(block, original, composite, zoom), path, files);
        }

        var json = Path.Combine(directory, "bloki.json");
        File.WriteAllText(json, JsonSerializer.Serialize(Describe(analysis, overlay, settings, dpi), Json), new UTF8Encoding(false));
        files.Add(json);
        return new FrameGallery(name, directory, analysis, overlay, files);
    }

    public static string WriteIndex(string output, IReadOnlyList<FrameGallery> frames, AppSettings settings, double dpi, PreviewOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Podgląd nakładki na klatkach z gry");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Wygenerowano: {DateTime.Now:yyyy-MM-dd HH:mm} • DPI {dpi:0} (skala {dpi / 96:0.##}) • umieszczenie {settings.OverlayPlacement} • czcionka {(settings.OverlayFontSize >= 9 ? settings.OverlayFontSize.ToString("0.#", CultureInfo.InvariantCulture) : "auto")} {settings.OverlayFontFamily} • krycie tła {settings.OverlayBackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture)} • profil {settings.ActiveProfileId ?? "brak"}");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Baza tłumaczeń: {(options.Cache is null ? "brak (wszystko Mock)" : "kopia " + Path.GetFileName(options.Cache))} • korpus: {(options.Corpus is null ? "brak" : Path.GetFileName(options.Corpus))}. „Mock (brak w bazie)” = tekstu nie ma lokalnie; w aplikacji poszedłby do dostawcy, tu dostaje prefiks [PL].");
        builder.AppendLine();
        builder.AppendLine("Obrysy na wycinkach: turkus = box OCR oryginału, żółty = łatka, różowy = zasięg polskiego tekstu. Czerwona przerywana ramka na porównaniu = tekst odrzucony przez bramkę live (nie tłumaczony).");
        foreach (var frame in frames)
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"## {frame.Name}");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Klatka: `{frame.Analysis.FramePath}` ({frame.Analysis.Frame.Width}×{frame.Analysis.Frame.Height})");
            if (!frame.Analysis.FromJson)
                builder.AppendLine(CultureInfo.InvariantCulture, $"OCR: {frame.Analysis.RawLines} linii, skala OCR {frame.Analysis.OcrFactor:0.###}, {frame.Analysis.OcrMs} ms; bloki: {frame.Analysis.Blocks.Count} przyjęte, {frame.Analysis.Rejected.Count} odrzucone.");
            else
                builder.AppendLine(CultureInfo.InvariantCulture, $"Bloki z pliku JSON: {frame.Analysis.Blocks.Count}.");
            builder.AppendLine();
            var relative = Path.GetRelativePath(output, frame.Directory).Replace('\\', '/');
            builder.AppendLine(CultureInfo.InvariantCulture, $"![porównanie]({relative}/porownanie.png)");
            builder.AppendLine();
            builder.AppendLine("| # | pochodzenie | styl | oryginał (EN) | tłumaczenie | box px | linia px | czcionka px | tekst/łatka szer. | tekst/łatka wys. |");
            builder.AppendLine("|---:|---|---|---|---|---|---:|---:|---:|---:|");
            foreach (var block in frame.Analysis.Blocks)
            {
                var rendered = frame.Overlay.Blocks.FirstOrDefault(r => r.Block.Number == block.Number);
                var widthRatio = rendered is null || rendered.PatchPx.Width == 0 ? "" : Ratio(rendered.TextPx.Width, rendered.PatchPx.Width);
                var heightRatio = rendered is null || rendered.PatchPx.Height == 0 ? "" : Ratio(rendered.TextPx.Height, rendered.PatchPx.Height);
                var link = rendered is null ? block.Number.ToString(CultureInfo.InvariantCulture) : $"[{block.Number}]({relative}/bloki/blok-{block.Number:00}.png)";
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"| {link} | {block.Origin} | {rendered?.Style ?? "—"} | {Cell(block.Text)} | {Cell(block.Translation ?? "—")} | {block.Box.Width}×{block.Box.Height} | {block.LineHeight} | {(rendered is null ? "" : rendered.FontSizePx.ToString("0.#", CultureInfo.InvariantCulture))} | {widthRatio} | {heightRatio} |");
            }
            if (frame.Analysis.Rejected.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Odrzucone przez bramkę live:");
                foreach (var rejected in frame.Analysis.Rejected)
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- `{Cell(rejected.Text)}` ({rejected.Box.X},{rejected.Box.Y} {rejected.Box.Width}×{rejected.Box.Height})");
            }
        }

        var path = Path.Combine(output, "galeria.md");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Ratio(int value, int reference) =>
        (value / (double)reference).ToString("0.00", CultureInfo.InvariantCulture) + "×";

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ⏎ ");

    private static object Describe(FrameAnalysis analysis, RenderedOverlay overlay, AppSettings settings, double dpi) => new
    {
        frame = analysis.FramePath,
        width = analysis.Frame.Width,
        height = analysis.Frame.Height,
        dpi,
        settings = new
        {
            overlayPlacement = settings.OverlayPlacement,
            liveDisplayMode = settings.LiveDisplayMode,
            overlayFontSize = settings.OverlayFontSize,
            overlayFontFamily = settings.OverlayFontFamily,
            overlayBackgroundOpacity = settings.OverlayBackgroundOpacity,
            activeProfileId = settings.ActiveProfileId,
        },
        ocr = new { rawLines = analysis.RawLines, factor = analysis.OcrFactor, ms = analysis.OcrMs, fromJson = analysis.FromJson },
        blocks = analysis.Blocks.Select(block =>
        {
            var rendered = overlay.Blocks.FirstOrDefault(r => r.Block.Number == block.Number);
            return new
            {
                nr = block.Number,
                key = block.Key,
                text = block.Text,
                translation = block.Translation,
                origin = block.Origin,
                x = block.Box.X,
                y = block.Box.Y,
                width = block.Box.Width,
                height = block.Box.Height,
                lineHeight = block.LineHeight,
                textRgb = Hex(block.Colors.TextRgb),
                backgroundRgb = Hex(block.Colors.BackgroundRgb),
                outlineRgb = Hex(block.Colors.OutlineRgb),
                texture = block.Colors.Texture is { } texture ? $"{texture.Columns}x{texture.Rows}" : null,
                style = rendered?.Style,
                fontSizePx = rendered is null ? (double?)null : Math.Round(rendered.FontSizePx, 2),
                elementPx = rendered is null ? null : Rect(rendered.ElementPx),
                patchPx = rendered is null ? null : Rect(rendered.PatchPx),
                textPx = rendered is null ? null : Rect(rendered.TextPx),
            };
        }),
        rejected = analysis.Rejected.Select(static r => new { text = r.Text, x = r.Box.X, y = r.Box.Y, width = r.Box.Width, height = r.Box.Height }),
    };

    private static object Rect(RectPx rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };

    private static string? Hex(int rgb) => rgb < 0 ? null : "#" + rgb.ToString("X6", CultureInfo.InvariantCulture);

    private static void Save(Pixels pixels, string path, List<string> files)
    {
        pixels.SavePng(path);
        files.Add(path);
    }

    private static Pixels SideBySide(string name, Pixels original, Pixels composite, FrameAnalysis analysis, RenderedOverlay overlay, AppSettings settings, double dpi)
    {
        var left = original.HalfSize();
        var right = composite.HalfSize();
        const int header = 44;
        const int gap = 12;
        var width = left.Width + gap + right.Width;
        var height = header + Math.Max(left.Height, right.Height);
        return Draw(width, height, dc =>
        {
            dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, width, height));
            dc.DrawImage(left.ToSource(), new Rect(0, header, left.Width, left.Height));
            dc.DrawImage(right.ToSource(), new Rect(left.Width + gap, header, right.Width, right.Height));
            Text(dc, $"{name} • oryginał", 8, 8, 20, Brushes.White);
            Text(dc, $"nakładka: {settings.OverlayPlacement}, {settings.OverlayFontFamily}, krycie {settings.OverlayBackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture)}, DPI {dpi:0}", left.Width + gap + 8, 8, 20, Brushes.White);

            var outline = new Pen(OcrBoxBrush, 1.5);
            var rejected = new Pen(RejectedBrush, 1.5) { DashStyle = DashStyles.Dash };
            foreach (var block in analysis.Rejected)
                dc.DrawRectangle(null, rejected, Half(block.Box, 0, header));
            foreach (var block in analysis.Blocks)
            {
                dc.DrawRectangle(null, outline, Half(block.Box, 0, header));
                var mock = block.Origin.Contains("Mock", StringComparison.Ordinal);
                Badge(dc, block.Number, Half(block.Box, 0, header), mock, block.Translation is null);
                if (overlay.Blocks.Any(r => r.Block.Number == block.Number))
                    Badge(dc, block.Number, Half(block.Box, left.Width + gap, header), mock, false);
            }
        });
    }

    private static Rect Half(RectPx box, double offsetX, double offsetY) =>
        new(offsetX + box.X / 2.0, offsetY + box.Y / 2.0, Math.Max(1, box.Width / 2.0), Math.Max(1, box.Height / 2.0));

    private static void Badge(DrawingContext dc, int number, Rect anchor, bool mock, bool untranslated)
    {
        var text = Format(number.ToString(CultureInfo.InvariantCulture), 15, Brushes.Black);
        var rect = new Rect(Math.Max(0, anchor.X - 2), Math.Max(0, anchor.Y - text.Height - 4), text.Width + 8, text.Height + 2);
        var fill = untranslated ? RejectedBrush : mock ? MockBrush : PatchBrush;
        dc.DrawRoundedRectangle(fill, null, rect, 3, 3);
        dc.DrawText(text, new Point(rect.X + 4, rect.Y + 1));
    }

    private static Pixels BlockSheet(RenderedBlock rendered, Pixels original, Pixels composite, int zoom)
    {
        var block = rendered.Block;
        var pad = Math.Max(16, block.LineHeight / 2);
        var area = block.Box.Union(rendered.ElementPx).Union(rendered.TextPx).Inflate(pad)
            .Intersect(new RectPx(0, 0, original.Width, original.Height));
        var factor = zoom;
        while (factor > 1 && area.Width * factor > 2400) factor--;

        var before = original.Crop(area).ScaleNearest(factor);
        var after = composite.Crop(area).ScaleNearest(factor);
        const int header = 112;
        const int label = 30;
        var width = Math.Max(1100, before.Width);
        var height = header + 3 * (label + before.Height);
        var mock = block.Origin.Contains("Mock", StringComparison.Ordinal);
        return Draw(width, height, dc =>
        {
            dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, width, height));
            var widthRatio = rendered.PatchPx.Width == 0 ? "—" : Ratio(rendered.TextPx.Width, rendered.PatchPx.Width);
            var heightRatio = rendered.PatchPx.Height == 0 ? "—" : Ratio(rendered.TextPx.Height, rendered.PatchPx.Height);
            Text(dc, $"#{block.Number} • {block.Origin} • {rendered.Style} • czcionka {rendered.FontSizePx:0.#} px przy linii oryginału {block.LineHeight} px • box {block.Box.Width}×{block.Box.Height} px • tekst/łatka {widthRatio} szer., {heightRatio} wys. • powiększenie {factor}×",
                8, 6, 17, mock ? MockBrush : Brushes.White);
            Text(dc, "EN: " + Shorten(block.Text), 8, 36, 17, Brushes.Gainsboro);
            Text(dc, "PL: " + Shorten(block.Translation ?? "—"), 8, 64, 17, Brushes.Gainsboro);
            Text(dc, $"kolory: tekst {Hex(block.Colors.TextRgb) ?? "?"}, tło {Hex(block.Colors.BackgroundRgb) ?? "?"}, kontur {Hex(block.Colors.OutlineRgb) ?? "?"}", 8, 88, 14, Brushes.Gray);

            var y = (double)header;
            Text(dc, "oryginał", 8, y + 4, 16, Brushes.White);
            dc.DrawImage(before.ToSource(), new Rect(0, y + label, before.Width, before.Height));
            y += label + before.Height;
            Text(dc, "nakładka (jak w grze)", 8, y + 4, 16, Brushes.White);
            dc.DrawImage(after.ToSource(), new Rect(0, y + label, after.Width, after.Height));
            y += label + after.Height;
            Text(dc, "nakładka + obrysy (turkus: box OCR, żółty: łatka, różowy: tekst)", 8, y + 4, 16, Brushes.White);
            var top = y + label;
            dc.DrawImage(after.ToSource(), new Rect(0, top, after.Width, after.Height));
            Outline(dc, block.Box, area, factor, top, OcrBoxBrush);
            if (rendered.PatchPx.Width > 0) Outline(dc, rendered.PatchPx, area, factor, top, PatchBrush);
            if (rendered.TextPx.Width > 0) Outline(dc, rendered.TextPx, area, factor, top, TextBrush);
        });
    }

    private static void Outline(DrawingContext dc, RectPx box, RectPx area, int factor, double top, Brush brush) =>
        dc.DrawRectangle(null, new Pen(brush, 2), new Rect(
            (box.X - area.X) * factor + 1, top + (box.Y - area.Y) * factor + 1,
            Math.Max(1, box.Width * factor - 2), Math.Max(1, box.Height * factor - 2)));

    private static string Shorten(string text)
    {
        var flat = text.Replace("\r", "").Replace("\n", " ⏎ ");
        return flat.Length <= 150 ? flat : flat[..147] + "…";
    }

    private static Pixels Draw(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen()) draw(dc);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return Pixels.FromSource(target);
    }

    private static FormattedText Format(string text, double size, Brush brush) =>
        new(text, CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight, Label, size, brush, 1.0);

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush) =>
        dc.DrawText(Format(text, size, brush), new Point(x, y));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
