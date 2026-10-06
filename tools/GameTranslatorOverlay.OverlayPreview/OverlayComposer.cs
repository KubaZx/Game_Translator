using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Infrastructure.Settings;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace GameTranslatorOverlay.OverlayPreview;

internal sealed record RenderedBlock(
    PreviewBlock Block, RectPx ElementPx, RectPx PatchPx, RectPx TextPx, double FontSizePx, string Style);

internal sealed record RenderedOverlay(Pixels Layer, IReadOnlyList<RenderedBlock> Blocks, double LayoutMs = 0);

internal static class OverlayComposer
{
    public static RenderedOverlay Render(FrameAnalysis analysis, AppSettings settings, double dpi, string? profileFont)
    {
        var width = analysis.Frame.Width;
        var height = analysis.Frame.Height;
        var scale = dpi / 96.0;
        var bounds = new RectPx(0, 0, width, height);
        var monitor = new MonitorArea(IntPtr.Zero, bounds, bounds, scale);
        var size = new Size(width / scale, height / scale);

        var canvas = new Canvas { Width = size.Width, Height = size.Height };
        VisualTreeHelper.SetRootDpi(canvas, new DpiScale(scale, scale));
        var fitSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
        var layoutWatch = System.Diagnostics.Stopwatch.StartNew();
        var elements = new List<(PreviewBlock Block, Border Element)>();
        foreach (var block in analysis.Blocks)
        {
            if (block.Translation is { } voted && block.Cover is { } voteCover)
                OverlayBlockRenderer.VoteNativeWeight(voteCover, voted, settings, profileFont, scale);
        }
        foreach (var block in analysis.Blocks)
        {
            if (block.Translation is not { } translation) continue;
            if (OverlayBlockRenderer.HidesIdenticalText(settings)
                && string.Equals(TextNormalizer.Normalize(translation), TextNormalizer.Normalize(block.Text), StringComparison.OrdinalIgnoreCase))
                continue;
            Border element;
            if (OverlayBlockRenderer.UsesNativeCover(settings, block.Cover))
            {
                element = OverlayBlockRenderer.CreateNativeElement(translation, block.Cover!, scale);
                canvas.Children.Add(element);
                OverlayBlockRenderer.LayoutNativeElement(element, block.Box, monitor, settings, profileFont, FreeSpaceRight(block, analysis.Blocks, width));
            }
            else
            {
                var colors = block.Colors;
                element = OverlayBlockRenderer.CreateBlockElement(
                    translation, settings, scale, block.LineHeight,
                    colors.TextRgb, colors.BackgroundRgb, colors.OutlineRgb, colors.Texture);
                element.Tag = block.LineHeight;
                canvas.Children.Add(element);
                OverlayBlockRenderer.PositionBlockElement(element, block.Box, monitor, settings, scale, fitSignatures, block.Key);
            }
            elements.Add((block, element));
        }

        canvas.Measure(size);
        canvas.Arrange(new Rect(size));
        canvas.UpdateLayout();
        var layoutMs = layoutWatch.Elapsed.TotalMilliseconds;

        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(canvas);
        target.Freeze();

        var rendered = elements.Select(e => Describe(e.Block, e.Element, scale)).ToList();
        return new RenderedOverlay(Pixels.FromSource(target), rendered, layoutMs);
    }

    private static double FreeSpaceRight(PreviewBlock block, IReadOnlyList<PreviewBlock> all, int frameWidth)
    {
        var box = block.Box;
        double free = frameWidth - box.Right;
        foreach (var other in all)
        {
            if (ReferenceEquals(other, block)) continue;
            var o = other.Box;
            var overlap = Math.Min(o.Bottom, box.Bottom) - Math.Max(o.Y, box.Y);
            if (overlap < box.Height * 0.3 || o.X < box.Right - 2) continue;
            free = Math.Min(free, o.X - box.Right);
        }
        return Math.Max(0, free);
    }

    private static RenderedBlock Describe(PreviewBlock block, Border element, double scale)
    {
        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        var elementRect = ToPx(left, top, element.DesiredSize.Width, element.DesiredSize.Height, scale);

        var patch = default(RectPx);
        string style;
        UIElement? content = element.Child;
        if (element.Child is GameTextElement native)
        {
            var ink = native.InkBounds;
            var cover = block.Cover;
            patch = ToPx(left + native.PatchRect.X, top + native.PatchRect.Y, native.PatchRect.Width, native.PatchRect.Height, scale);
            style = cover is { Soft: true } ? "natywnie: miękka łatka" : "natywnie: wypełnione litery";
            style += $" • {native.Typeface.FontFamily.Source.Split('#').Last()} {native.Typeface.Weight}";
            if (cover is not null) style += $" • maska {cover.MaskFraction:0.00} • {cover.BuildMs:0.0} ms";
            var inkRect = ink.IsEmpty ? default : ToPx(left + ink.X, top + ink.Y, ink.Width, ink.Height, scale);
            return new RenderedBlock(block, elementRect, patch, inkRect, native.EmSize * scale, style);
        }
        if (element.Child is CoverPatchHost host)
        {
            patch = ToPx(left, top, host.Patch.Width, host.Patch.Height, scale);
            style = host.Patch.Fill is ImageBrush ? "łatka z tekstury tła" : "łatka jednolita";
            content = host.Content;
        }
        else if (element.Background is SolidColorBrush { Color.A: > 0 })
        {
            style = "dymek";
            patch = elementRect;
        }
        else
        {
            style = "bez tła";
        }

        var primary = content switch
        {
            OutlinedTextBlock outlined => outlined.Primary,
            WpfTextBlock text => text,
            _ => null,
        };
        var textRect = default(RectPx);
        if (primary is not null)
        {
            var origin = primary.TranslatePoint(new Point(0, 0), element);
            textRect = ToPx(left + origin.X, top + origin.Y, primary.DesiredSize.Width, primary.DesiredSize.Height, scale);
        }

        return new RenderedBlock(block, elementRect, patch, textRect, OverlayBlockRenderer.GetFontSize(element) * scale, style);
    }

    private static RectPx ToPx(double left, double top, double width, double height, double scale)
    {
        var x = (int)Math.Floor(left * scale);
        var y = (int)Math.Floor(top * scale);
        var right = (int)Math.Ceiling((left + width) * scale);
        var bottom = (int)Math.Ceiling((top + height) * scale);
        return new RectPx(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
