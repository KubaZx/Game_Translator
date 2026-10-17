using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.App.Ui;

public sealed record ProfileChoice(string Label, object? Value);

public sealed class IgnoreRegionRow : ObservableObject
{
    public IgnoreRegionRow(int number, RelativeRegion? region = null)
    {
        _title = TitleFor(number);
        if (region is null) return;
        _x = ProfileNumbers.Format(region.X);
        _y = ProfileNumbers.Format(region.Y);
        _width = ProfileNumbers.Format(region.Width);
        _height = ProfileNumbers.Format(region.Height);
    }

    private string _title;
    public string Title { get => _title; private set => SetField(ref _title, value); }

    private string _x = string.Empty;
    public string X { get => _x; set => SetField(ref _x, value); }

    private string _y = string.Empty;
    public string Y { get => _y; set => SetField(ref _y, value); }

    private string _width = string.Empty;
    public string Width { get => _width; set => SetField(ref _width, value); }

    private string _height = string.Empty;
    public string Height { get => _height; set => SetField(ref _height, value); }

    public void Renumber(int number) => Title = TitleFor(number);

    public bool TryRead(out RelativeRegion region)
    {
        region = new RelativeRegion();
        if (!ProfileNumbers.TryParse(X, out var x) || !ProfileNumbers.TryParse(Y, out var y)
            || !ProfileNumbers.TryParse(Width, out var width) || !ProfileNumbers.TryParse(Height, out var height)
            || x is not { } left || y is not { } top || width is not { } w || height is not { } h)
        {
            return false;
        }
        region = new RelativeRegion { X = left, Y = top, Width = w, Height = h };
        return true;
    }

    private static string TitleFor(int number) => $"Obszar {number}";
}
