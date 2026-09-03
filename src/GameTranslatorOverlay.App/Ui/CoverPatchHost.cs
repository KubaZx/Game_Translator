using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GameTranslatorOverlay.App.Ui;

/// <summary>
/// Dymek trybu zakrywania: miękka łatka (rozmyta kopia tła) dokładnie pod boxem
/// oryginalnego napisu + tekst tłumaczenia, który może wystawać poza łatkę.
/// Twarda, szeroka łatka pod całym polskim tekstem wyglądała jak czarny kleks
/// obok napisu; miękkie krawędzie wtapiają ją w grafikę, a dłuższy tekst
/// zostaje czytelny dzięki konturowi.
/// </summary>
public sealed class CoverPatchHost : Grid
{
    public Rectangle Patch { get; }

    public FrameworkElement Content { get; }

    public CoverPatchHost(FrameworkElement content, Brush fill)
    {
        Patch = new Rectangle
        {
            Fill = fill,
            RadiusX = 6,
            RadiusY = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new BlurEffect { Radius = 7 },
        };
        Content = content;
        Children.Add(Patch);
        Children.Add(content);
    }

    public void SetPatchSize(double width, double height)
    {
        Patch.Width = Math.Max(0, width);
        Patch.Height = Math.Max(0, height);
    }

    public void SetFill(Brush fill) => Patch.Fill = fill;
}
