using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.App.Ui;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && value is not string { Length: 0 };
        if (Invert) present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class WidthToColumnsConverter : IValueConverter
{
    public double Threshold { get; set; } = 720;

    public int Narrow { get; set; } = 2;

    public int Wide { get; set; } = 3;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && width > 0 && width < Threshold ? Narrow : Wide;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ProviderPresentationConverter : IValueConverter
{
    public bool Glyph { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var id = value as string ?? string.Empty;
        return Glyph ? ProviderPresentation.Glyph(id) : ProviderPresentation.Tagline(id);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class ProviderPresentation
{
    public static string Glyph(string providerId) => providerId switch
    {
        OpenAiCompatibleTranslationProvider.ProviderName => "",
        ClaudeTranslationProvider.ProviderName => "",
        MockTranslationProvider.ProviderName => "",
        _ => "",
    };

    public static string Tagline(string providerId) => providerId switch
    {
        DeepLTranslationProvider.ProviderName => "Online · plan API Free: 500 tys. znaków/mies.",
        AzureTranslatorProvider.ProviderName => "Online · plan F0: 2 mln znaków/mies.",
        GoogleTranslateProvider.ProviderName => "Online · 500 tys. znaków/mies. bez opłat",
        OpenAiCompatibleTranslationProvider.ProviderName => "Lokalnie (Ollama, LM Studio) albo w chmurze",
        ClaudeTranslationProvider.ProviderName => "Online · tłumaczy z kontekstem całego ekranu",
        MockTranslationProvider.ProviderName => "Test bez internetu i bez klucza",
        _ => string.Empty,
    };
}
