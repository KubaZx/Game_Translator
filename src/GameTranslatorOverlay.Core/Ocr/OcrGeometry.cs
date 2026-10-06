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
        var dx = x + width / 2.0 - cx;
        var dy = y + height / 2.0 - cy;
        var centerX = cx + dx * cos - dy * sin;
        var centerY = cy + dx * sin + dy * cos;
        var left = (int)Math.Floor(centerX - width / 2.0);
        var top = (int)Math.Floor(centerY - height / 2.0);
        return new RectPx(left, top, (int)Math.Ceiling(width), (int)Math.Ceiling(height));
    }
}
