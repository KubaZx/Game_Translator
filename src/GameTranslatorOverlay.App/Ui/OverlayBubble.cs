using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameTranslatorOverlay.App.Ui;

public sealed class OverlayBubble : Border
{
    public static readonly DependencyProperty QualityMarkProperty = DependencyProperty.Register(
        nameof(QualityMark), typeof(bool), typeof(OverlayBubble),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush MarkFill = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
    private static readonly Pen MarkEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0))), 1));

    public bool QualityMark
    {
        get => (bool)GetValue(QualityMarkProperty);
        set => SetValue(QualityMarkProperty, value);
    }

    public bool IsBare => Background is null or SolidColorBrush { Color.A: 0 };

    public static Rect MarkBounds(Size size, bool bare)
    {
        if (bare)
        {
            var radius = DotRadius(size);
            return new Rect(size.Width + 2 - 0.5, 1 - 0.5, 2 * radius + 1, 2 * radius + 1);
        }
        var mark = CornerSize(size);
        return new Rect(size.Width - mark, 0, mark, mark);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = RenderSize;
        if (!QualityMark || size.Width < 4 || size.Height < 4) return;

        if (IsBare)
        {
            var radius = DotRadius(size);
            dc.DrawEllipse(MarkFill, MarkEdge, new Point(size.Width + 2 + radius, 1 + radius), radius, radius);
            return;
        }

        var mark = CornerSize(size);
        var corner = new StreamGeometry();
        using (var figure = corner.Open())
        {
            figure.BeginFigure(new Point(size.Width - mark, 0), true, true);
            figure.LineTo(new Point(size.Width, 0), false, false);
            figure.LineTo(new Point(size.Width, mark), false, false);
        }
        corner.Freeze();

        var radiusTopRight = CornerRadius.TopRight;
        if (radiusTopRight > 0) dc.PushClip(new RectangleGeometry(new Rect(size), radiusTopRight, radiusTopRight));
        dc.DrawGeometry(MarkFill, null, corner);
        if (radiusTopRight > 0) dc.Pop();
    }

    private static double CornerSize(Size size) =>
        Math.Min(Math.Clamp(size.Height * 0.3, 6, 11), Math.Min(size.Width, size.Height) / 2);

    private static double DotRadius(Size size) => Math.Clamp(size.Height * 0.11, 2.5, 4);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
