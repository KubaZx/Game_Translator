namespace GameTranslatorOverlay.Core.Ocr;

public static class OcrGeometry
{
    public const double MinimumAngle = 0.05;

    public static RectPx Unrotate(double x, double y, double width, double height, double? angleDegrees, int imageWidth, int imageHeight)
    {
        if (angleDegrees is not { } angle || Math.Abs(angle) < MinimumAngle)
            return new RectPx((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Ceiling(width), (int)Math.Ceiling(height));
        var radians = angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = imageWidth / 2.0;
        var cy = imageHeight / 2.0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in (ReadOnlySpan<(double, double)>)[(x, y), (x + width, y), (x, y + height), (x + width, y + height)])
        {
            var dx = px - cx;
            var dy = py - cy;
            var rx = cx + dx * cos - dy * sin;
            var ry = cy + dx * sin + dy * cos;
            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
            maxX = Math.Max(maxX, rx);
            maxY = Math.Max(maxY, ry);
        }
        var left = (int)Math.Floor(minX);
        var top = (int)Math.Floor(minY);
        return new RectPx(left, top, (int)Math.Ceiling(maxX) - left, (int)Math.Ceiling(maxY) - top);
    }
}
