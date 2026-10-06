using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GameTranslatorOverlay.App.Ui;

public sealed class GameTextElement : FrameworkElement
{
    private string _text = string.Empty;
    private Typeface _typeface = new("Segoe UI");
    private double _emSize = 15;
    private Brush _fill = Brushes.White;
    private Brush? _outlineBrush;
    private double _outlineThickness;
    private Vector _shadow;
    private double _lineAdvance;
    private double _wrapWidth;
    private TextAlignment _alignment = TextAlignment.Left;
    private double _anchorX;
    private double _baselineY;
    private double _stretchX = 1;
    private ImageSource? _patch;
    private Rect _patchRect;
    private Geometry? _geometry;
    private Rect _baseBounds = Rect.Empty;

    public GameTextElement()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public string RawText { get; set; } = string.Empty;

    public string? StyleReference { get; set; }

    public int StyleTextRgb { get; set; } = -1;

    public double StyleAscent { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value ?? string.Empty;
            Invalidate();
        }
    }

    public Typeface Typeface
    {
        get => _typeface;
        set
        {
            if (Equals(_typeface, value)) return;
            _typeface = value;
            Invalidate();
        }
    }

    public double EmSize
    {
        get => _emSize;
        set
        {
            var clamped = Math.Clamp(value, 4, 400);
            if (Math.Abs(_emSize - clamped) < 0.001) return;
            _emSize = clamped;
            Invalidate();
        }
    }

    public Brush Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            InvalidateVisual();
        }
    }

    public Color? OutlineColor { get; private set; }

    public double OutlineThickness => _outlineThickness;

    public Vector ShadowOffset => _shadow;

    public double LineAdvance => _lineAdvance;

    public double WrapWidth => _wrapWidth;

    public TextAlignment Alignment => _alignment;

    public double StretchX => _stretchX;

    public Rect PatchRect => _patchRect;

    public bool HasPatch => _patch is not null;

    public Rect InkBounds
    {
        get
        {
            EnsureGeometry();
            if (_baseBounds.IsEmpty) return Rect.Empty;
            var bounds = _baseBounds;
            bounds.Offset(_anchorX, _baselineY);
            return bounds;
        }
    }

    public void SetOutline(Color? color, double thickness, Vector shadow)
    {
        OutlineColor = color;
        if (color is { } c && (thickness > 0 || shadow.LengthSquared > 0.01))
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            _outlineBrush = brush;
            _outlineThickness = Math.Max(0, thickness);
            _shadow = shadow;
        }
        else
        {
            _outlineBrush = null;
            _outlineThickness = 0;
            _shadow = default;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetLayout(double anchorX, double baselineY, TextAlignment alignment, double wrapWidth, double lineAdvance, double stretchX = 1)
    {
        var shape = _alignment != alignment || _wrapWidth != Math.Max(0, wrapWidth)
            || _lineAdvance != Math.Max(0, lineAdvance) || _stretchX != Math.Clamp(stretchX, 0.5, 2);
        if (!shape && _anchorX == anchorX && _baselineY == baselineY) return;
        _anchorX = anchorX;
        _baselineY = baselineY;
        _alignment = alignment;
        _wrapWidth = Math.Max(0, wrapWidth);
        _lineAdvance = Math.Max(0, lineAdvance);
        _stretchX = Math.Clamp(stretchX, 0.5, 2);
        if (shape)
        {
            Invalidate();
        }
        else
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public void SetPatch(ImageSource? patch, Rect rect)
    {
        _patch = patch;
        _patchRect = rect;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public FormattedText CreateFormattedText(double pixelsPerDip) => Format(_text, _typeface, _emSize, _fill, pixelsPerDip, _wrapWidth, _lineAdvance);

    public static FormattedText Format(
        string text, Typeface typeface, double emSize, Brush fill, double pixelsPerDip, double wrapWidth = 0, double lineAdvance = 0)
    {
        var formatted = new FormattedText(
            text, CultureInfo.GetCultureInfo("pl-PL"), FlowDirection.LeftToRight, typeface, emSize, fill, pixelsPerDip)
        {
            Trimming = TextTrimming.None,
        };
        if (wrapWidth > 0) formatted.MaxTextWidth = wrapWidth;
        if (lineAdvance > 0) formatted.LineHeight = lineAdvance;
        return formatted;
    }

    private void Invalidate()
    {
        _geometry = null;
        _baseBounds = Rect.Empty;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void EnsureGeometry()
    {
        if (_geometry is not null) return;
        if (_text.Length == 0)
        {
            _geometry = Geometry.Empty;
            _baseBounds = Rect.Empty;
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = Format(_text, _typeface, _emSize, _fill, pixelsPerDip, 0, _lineAdvance);
        var natural = formatted.WidthIncludingTrailingWhitespace;
        var width = _wrapWidth > 0 ? Math.Min(_wrapWidth, natural + 1) : natural + 1;
        formatted = Format(_text, _typeface, _emSize, _fill, pixelsPerDip, width, _lineAdvance);
        formatted.TextAlignment = _alignment;
        var left = _alignment switch
        {
            TextAlignment.Center => -width * _stretchX / 2,
            TextAlignment.Right => -width * _stretchX,
            _ => 0,
        };
        var geometry = formatted.BuildGeometry(new Point(0, -formatted.Baseline)).Clone();
        geometry.Transform = new MatrixTransform(new Matrix(_stretchX, 0, 0, 1, left, 0));
        geometry.Freeze();
        _geometry = geometry;
        _baseBounds = geometry.Bounds;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var bounds = InkBounds;
        var right = 0.0;
        var bottom = 0.0;
        if (!bounds.IsEmpty)
        {
            var grow = _outlineThickness;
            right = bounds.Right + grow + Math.Max(0, _shadow.X);
            bottom = bounds.Bottom + grow + Math.Max(0, _shadow.Y);
        }
        if (_patch is not null)
        {
            right = Math.Max(right, _patchRect.Right);
            bottom = Math.Max(bottom, _patchRect.Bottom);
        }
        return new Size(Math.Max(0, right), Math.Max(0, bottom));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_patch is not null && !_patchRect.IsEmpty)
        {
            drawingContext.DrawImage(_patch, _patchRect);
        }

        EnsureGeometry();
        if (_geometry is null || _geometry.IsEmpty()) return;

        drawingContext.PushTransform(new TranslateTransform(_anchorX, _baselineY));
        if (_outlineBrush is not null)
        {
            Pen? pen = null;
            if (_outlineThickness > 0)
            {
                pen = new Pen(_outlineBrush, _outlineThickness * 2)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
            }
            if (_shadow.LengthSquared > 0.01)
            {
                drawingContext.PushTransform(new TranslateTransform(_shadow.X, _shadow.Y));
                drawingContext.DrawGeometry(_outlineBrush, pen, _geometry);
                drawingContext.Pop();
            }
            if (pen is not null) drawingContext.DrawGeometry(_outlineBrush, pen, _geometry);
        }
        drawingContext.DrawGeometry(_fill, null, _geometry);
        drawingContext.Pop();
    }
}
