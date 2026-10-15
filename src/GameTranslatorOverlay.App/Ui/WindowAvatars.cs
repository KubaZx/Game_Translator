using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GameTranslatorOverlay.App.Ui;

public static class WindowAvatars
{
    private static readonly (Color From, Color To)[] Palette =
    [
        (Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0x22, 0xD3, 0xEE)),
        (Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x8B, 0x5C, 0xF6)),
        (Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x06, 0xB6, 0xD4)),
        (Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF9, 0x73, 0x16)),
        (Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x63, 0x66, 0xF1)),
        (Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0xEC, 0x48, 0x99)),
    ];

    private static readonly Dictionary<int, Brush> Brushes = [];

    public static string Initial(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "?";
        foreach (var ch in title)
            if (char.IsLetterOrDigit(ch)) return char.ToUpper(ch, CultureInfo.CurrentCulture).ToString();
        return "?";
    }

    public static Brush Background(string? key)
    {
        var index = Stable(key ?? string.Empty) % Palette.Length;
        if (Brushes.TryGetValue(index, out var cached)) return cached;
        var (from, to) = Palette[index];
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        Brushes[index] = brush;
        return brush;
    }

    private static int Stable(string text)
    {
        var hash = 17;
        foreach (var ch in text.ToUpperInvariant()) hash = unchecked(hash * 31 + ch);
        return hash & int.MaxValue;
    }
}

public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => WindowAvatars.Initial(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class AvatarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => WindowAvatars.Background(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
