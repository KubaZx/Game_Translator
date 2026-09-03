using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameTranslatorOverlay.App.Ui;

/// <summary>
/// Tekst z konturem w kolorze próbkowanym z gry: osiem kopii tekstu przesuniętych
/// o grubość obwódki pod właściwym tekstem. WPF nie ma natywnego obrysu glifów,
/// a rozmyty DropShadow wygląda jak poświata, nie jak czcionka gry.
/// </summary>
public sealed class OutlinedTextBlock : Grid
{
    private static readonly (int X, int Y)[] Directions =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    private readonly List<TextBlock> _outlines = [];

    public TextBlock Primary { get; }

    public OutlinedTextBlock(TextBlock primary, Color outlineColor)
    {
        Primary = primary;
        var brush = new SolidColorBrush(outlineColor);
        brush.Freeze();

        foreach (var _ in Directions)
        {
            var copy = new TextBlock
            {
                Text = primary.Text,
                FontFamily = primary.FontFamily,
                FontSize = primary.FontSize,
                FontWeight = primary.FontWeight,
                TextWrapping = primary.TextWrapping,
                TextAlignment = primary.TextAlignment,
                Foreground = brush,
                VerticalAlignment = primary.VerticalAlignment,
                HorizontalAlignment = primary.HorizontalAlignment,
            };
            _outlines.Add(copy);
            Children.Add(copy);
        }
        Children.Add(primary);
        ApplyThickness();
    }

    public string Text
    {
        get => Primary.Text;
        set
        {
            Primary.Text = value;
            foreach (var copy in _outlines) copy.Text = value;
        }
    }

    public double FontSize
    {
        get => Primary.FontSize;
        set
        {
            Primary.FontSize = value;
            foreach (var copy in _outlines) copy.FontSize = value;
            ApplyThickness();
        }
    }

    public void SetOutlineColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        foreach (var copy in _outlines) copy.Foreground = brush;
    }

    public void SetLineHeight(double lineHeight, LineStackingStrategy strategy)
    {
        Primary.LineHeight = lineHeight;
        Primary.LineStackingStrategy = strategy;
        foreach (var copy in _outlines)
        {
            copy.LineHeight = lineHeight;
            copy.LineStackingStrategy = strategy;
        }
    }

    /// <summary>Grubość konturu rośnie z czcionką — 1 px przy ~14 px, 2 px przy ~28 px.</summary>
    private void ApplyThickness()
    {
        var thickness = Math.Max(1, Math.Round(Primary.FontSize / 14.0));
        for (var i = 0; i < _outlines.Count; i++)
        {
            var (dx, dy) = Directions[i];
            _outlines[i].Margin = new Thickness(dx * thickness, dy * thickness, -dx * thickness, -dy * thickness);
        }
    }
}
