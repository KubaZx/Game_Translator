using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Renderer
{
    public static BitmapSource Window(Window window, double scale, ImageSource? backdrop = null)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var width = root.ActualWidth;
        var height = root.ActualHeight;
        var bitmap = new RenderTargetBitmap(Px(width, scale), Px(height, scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (backdrop is not null)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(backdrop, new Rect(0, 0, width, height));
            bitmap.Render(visual);
        }
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource Element(FrameworkElement element, double scale, Brush? background)
    {
        element.UpdateLayout();
        var offset = VisualTreeHelper.GetOffset(element);
        var width = Math.Max(1, Math.Max(0, offset.X) + element.ActualWidth + element.Margin.Right);
        var height = Math.Max(1, Math.Max(0, offset.Y) + element.ActualHeight + element.Margin.Bottom);
        var area = new Rect(0, 0, width, height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background is not null) dc.DrawRectangle(background, null, area);
            dc.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = area,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = area,
            }, null, area);
        }
        var bitmap = new RenderTargetBitmap(Px(width, scale), Px(height, scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource? PageContent(Window window)
    {
        var viewer = VisualDescendants<ScrollViewer>(window)
            .Where(static s => s.IsVisible && s.ViewportWidth > 300 && s.ScrollableHeight > 1)
            .OrderByDescending(static s => s.ExtentHeight)
            .FirstOrDefault();
        if (viewer?.Content is not FrameworkElement content) return null;
        return Element(content, 1.0, window.Background);
    }

    public static BitmapSource Image(ImageSource source, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in LogicalDescendants<T>(child)) yield return nested;
        }
    }

    public static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }

    private static int Px(double dip, double scale) => Math.Max(1, (int)Math.Ceiling(dip * scale - 0.01));
}

internal static class ClipChecker
{
    public static List<(bool Clipped, string Text)> Find(FrameworkElement root)
    {
        var findings = new List<(bool, string)>();
        foreach (var block in Renderer.VisualDescendants<TextBlock>(root))
        {
            if (!block.IsVisible || block.ActualWidth < 1 || string.IsNullOrWhiteSpace(block.Text)) continue;
            var formatted = new FormattedText(block.Text, CultureInfo.CurrentCulture, block.FlowDirection,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
            var innerWidth = block.ActualWidth - block.Padding.Left - block.Padding.Right;
            var innerHeight = block.ActualHeight - block.Padding.Top - block.Padding.Bottom;
            string? problem = null;
            var clipped = true;
            if (block.TextWrapping == TextWrapping.NoWrap)
            {
                if (formatted.Width > innerWidth + 0.75)
                {
                    clipped = block.TextTrimming == TextTrimming.None;
                    problem = clipped ? "ucięty w poziomie" : "skrócony wielokropkiem";
                }
            }
            else
            {
                formatted.MaxTextWidth = Math.Max(1, innerWidth + 1);
                if (!double.IsNaN(block.LineHeight)) formatted.LineHeight = block.LineHeight;
                if (formatted.Height > innerHeight + 0.75)
                {
                    clipped = block.TextTrimming == TextTrimming.None;
                    problem = clipped ? "ucięty w pionie" : "skrócony wielokropkiem";
                }
            }
            if (problem is null && ClippedByAncestor(block, root) is { } container)
            {
                problem = "przycięty przez " + container;
                clipped = true;
            }
            if (problem is not null) findings.Add((clipped, $"{problem}: {Describe(block)} „{Short(block.Text)}”"));
        }
        return findings;
    }

    private static string? ClippedByAncestor(FrameworkElement element, FrameworkElement root)
    {
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        DependencyObject current = element;
        while (!ReferenceEquals(current, root) && VisualTreeHelper.GetParent(current) is Visual parent)
        {
            if (VisualTreeHelper.GetClip(parent) is { } clip)
            {
                var rect = element.TransformToAncestor(parent).TransformBounds(bounds);
                var limit = clip.Bounds;
                var horizontal = rect.Right > limit.Right + 1.5 || rect.Left < limit.Left - 1.5;
                var vertical = rect.Bottom > limit.Bottom + 1.5 || rect.Top < limit.Top - 1.5;
                if (parent is ScrollContentPresenter) return horizontal ? Describe(parent) : null;
                if (horizontal || vertical) return Describe(parent);
            }
            current = parent;
        }
        return null;
    }

    private static string Describe(DependencyObject element)
    {
        var path = new List<string>();
        for (var current = element; current is not null && path.Count < 4; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Name.Length: > 0 } named) path.Add(named.Name);
        }
        return element.GetType().Name + (path.Count > 0 ? " w " + string.Join(" < ", path) : string.Empty);
    }

    private static string Short(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 70 ? single : single[..67] + "…";
    }
}
