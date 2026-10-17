using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Gdi = System.Drawing;

internal static class GdiFallback
{
    public static int Count { get; private set; }

    public static bool IsBlank(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return Array.TrueForAll(pixels, static b => b == 0);
    }

    public static BitmapSource Render(FrameworkElement root, double width, double height, double scale, Brush? background)
    {
        Count++;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale - 0.01));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale - 0.01));
        using var bitmap = new Gdi.Bitmap(pixelWidth, pixelHeight, Gdi.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Gdi.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.ScaleTransform((float)scale, (float)scale);
            var area = new Gdi.RectangleF(0, 0, (float)width, (float)height);
            if (background is not null) Fill(graphics, background, area, 0, 1);
            Draw(graphics, root, root, area, 1);
        }

        var data = bitmap.LockBits(new Gdi.Rectangle(0, 0, pixelWidth, pixelHeight), ImageLockMode.ReadOnly, Gdi.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var source = BitmapSource.Create(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * pixelHeight, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void Draw(Gdi.Graphics graphics, DependencyObject node, FrameworkElement root, Gdi.RectangleF clip, double opacity)
    {
        if (node is UIElement element)
        {
            if (!element.IsVisible) return;
            opacity *= element.Opacity;
            if (opacity < 0.02) return;
            if (element is FrameworkElement framework && framework.ActualWidth > 0 && framework.ActualHeight > 0)
            {
                Rect bounds;
                try
                {
                    bounds = framework.TransformToAncestor(root).TransformBounds(new Rect(framework.RenderSize));
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                var box = new Gdi.RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
                if (VisualTreeHelper.GetClip(framework) is { } geometry)
                {
                    var limit = framework.TransformToAncestor(root).TransformBounds(geometry.Bounds);
                    clip = Gdi.RectangleF.Intersect(clip, new Gdi.RectangleF((float)limit.X, (float)limit.Y, (float)limit.Width, (float)limit.Height));
                }
                if (clip.Width <= 0 || clip.Height <= 0) return;
                if (framework is not TextBox)
                {
                    graphics.SetClip(clip);
                    Paint(graphics, framework, box, opacity);
                    graphics.ResetClip();
                }
                DrawChildren(graphics, node, root, clip, opacity);
                if (framework is TextBox)
                {
                    graphics.SetClip(clip);
                    Paint(graphics, framework, box, opacity);
                    graphics.ResetClip();
                }
                return;
            }
        }

        DrawChildren(graphics, node, root, clip, opacity);
    }

    private static void DrawChildren(Gdi.Graphics graphics, DependencyObject node, FrameworkElement root, Gdi.RectangleF clip, double opacity)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) Draw(graphics, VisualTreeHelper.GetChild(node, i), root, clip, opacity);
    }

    private static void Paint(Gdi.Graphics graphics, FrameworkElement element, Gdi.RectangleF box, double opacity)
    {
        switch (element)
        {
            case Border border:
                var radius = (float)(border.CornerRadius.TopLeft + border.CornerRadius.BottomRight) / 2;
                if (border.Background is { } background) Fill(graphics, background, box, radius, opacity);
                if (border.BorderBrush is { } stroke && border.BorderThickness.Left + border.BorderThickness.Top > 0)
                    Stroke(graphics, stroke, box, radius, (float)Math.Max(border.BorderThickness.Left, border.BorderThickness.Top), opacity);
                break;
            case Panel { Background: { } panelBackground }:
                Fill(graphics, panelBackground, box, 0, opacity);
                break;
            case Rectangle rectangle when rectangle.Fill is not null:
                Fill(graphics, rectangle.Fill, box, (float)rectangle.RadiusX, opacity);
                break;
            case Ellipse ellipse when ellipse.Fill is not null:
                using (var brush = new Gdi.SolidBrush(ColorOf(ellipse.Fill, opacity))) graphics.FillEllipse(brush, box);
                break;
            case Image:
                using (var brush = new Gdi.SolidBrush(Gdi.Color.FromArgb((int)(60 * opacity), 120, 140, 180))) graphics.FillRectangle(brush, box);
                break;
            case TextBlock text when Renderer.TextOf(text) is { Length: > 0 } content:
                Text(graphics, content, text.FontFamily, text.FontSize, text.FontWeight, text.Foreground, box, text.Padding,
                    text.TextWrapping != TextWrapping.NoWrap, text.TextTrimming != TextTrimming.None, text.TextAlignment, opacity);
                break;
            case TextBox textBox when !string.IsNullOrEmpty(textBox.Text):
                var padding = new Thickness(textBox.Padding.Left + 2, textBox.Padding.Top + 1, textBox.Padding.Right, textBox.Padding.Bottom);
                if (textBox.TextWrapping == TextWrapping.NoWrap)
                {
                    var middle = Math.Max(0, (box.Height - textBox.FontSize * 1.35) / 2);
                    padding = new Thickness(padding.Left, middle, padding.Right, 0);
                }
                Text(graphics, textBox.Text, textBox.FontFamily, textBox.FontSize, textBox.FontWeight, textBox.Foreground, box, padding,
                    textBox.TextWrapping != TextWrapping.NoWrap, false, textBox.TextAlignment, opacity);
                break;
        }
    }

    private static void Text(Gdi.Graphics graphics, string text, FontFamily family, double size, FontWeight weight, Brush? foreground,
        Gdi.RectangleF box, Thickness padding, bool wrap, bool trim, TextAlignment alignment, double opacity)
    {
        using var font = CreateFont(family, size, weight);
        using var brush = new Gdi.SolidBrush(ColorOf(foreground, opacity));
        using var format = new Gdi.StringFormat(Gdi.StringFormat.GenericTypographic)
        {
            Trimming = trim ? Gdi.StringTrimming.EllipsisCharacter : Gdi.StringTrimming.None,
            Alignment = alignment switch
            {
                TextAlignment.Center => Gdi.StringAlignment.Center,
                TextAlignment.Right => Gdi.StringAlignment.Far,
                _ => Gdi.StringAlignment.Near,
            },
        };
        format.FormatFlags |= Gdi.StringFormatFlags.MeasureTrailingSpaces;
        if (!wrap) format.FormatFlags |= Gdi.StringFormatFlags.NoWrap;
        var area = new Gdi.RectangleF(box.X + (float)padding.Left, box.Y + (float)padding.Top,
            Math.Max(1, box.Width - (float)(padding.Left + padding.Right) + (wrap ? 1 : 6)), Math.Max(1, box.Height - (float)(padding.Top + padding.Bottom) + 3));
        graphics.DrawString(text, font, brush, area, format);
    }

    private static Gdi.Font CreateFont(FontFamily family, double size, FontWeight weight)
    {
        var style = weight.ToOpenTypeWeight() >= 600 ? Gdi.FontStyle.Bold : Gdi.FontStyle.Regular;
        foreach (var name in family.Source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = name.Contains('#') ? name[(name.IndexOf('#') + 1)..] : name;
            try
            {
                using var candidate = new Gdi.FontFamily(clean);
                return new Gdi.Font(candidate, (float)size, style, Gdi.GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
            }
        }
        return new Gdi.Font("Segoe UI", (float)size, style, Gdi.GraphicsUnit.Pixel);
    }

    private static void Fill(Gdi.Graphics graphics, Brush brush, Gdi.RectangleF box, float radius, double opacity)
    {
        var color = ColorOf(brush, opacity);
        if (color.A == 0) return;
        using var fill = new Gdi.SolidBrush(color);
        if (radius <= 0.5)
        {
            graphics.FillRectangle(fill, box);
            return;
        }
        using var path = Rounded(box, radius);
        graphics.FillPath(fill, path);
    }

    private static void Stroke(Gdi.Graphics graphics, Brush brush, Gdi.RectangleF box, float radius, float thickness, double opacity)
    {
        var color = ColorOf(brush, opacity);
        if (color.A == 0) return;
        using var pen = new Gdi.Pen(color, thickness);
        var inset = new Gdi.RectangleF(box.X + thickness / 2, box.Y + thickness / 2, Math.Max(1, box.Width - thickness), Math.Max(1, box.Height - thickness));
        if (radius <= 0.5)
        {
            graphics.DrawRectangle(pen, inset.X, inset.Y, inset.Width, inset.Height);
            return;
        }
        using var path = Rounded(inset, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath Rounded(Gdi.RectangleF box, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        var path = new GraphicsPath();
        if (diameter <= 0.5)
        {
            path.AddRectangle(box);
            return path;
        }
        path.AddArc(box.X, box.Y, diameter, diameter, 180, 90);
        path.AddArc(box.Right - diameter, box.Y, diameter, diameter, 270, 90);
        path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(box.X, box.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Gdi.Color ColorOf(Brush? brush, double opacity)
    {
        var color = brush switch
        {
            SolidColorBrush solid => solid.Color,
            GradientBrush { GradientStops.Count: > 0 } gradient => Average(gradient.GradientStops),
            _ => Colors.White,
        };
        var alpha = color.A * opacity * (brush?.Opacity ?? 1);
        return Gdi.Color.FromArgb((int)Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);
    }

    private static Color Average(GradientStopCollection stops)
    {
        double a = 0, r = 0, g = 0, b = 0;
        foreach (var stop in stops)
        {
            a += stop.Color.A;
            r += stop.Color.R;
            g += stop.Color.G;
            b += stop.Color.B;
        }
        var n = stops.Count;
        return Color.FromArgb((byte)(a / n), (byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
