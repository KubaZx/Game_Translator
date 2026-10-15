using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameTranslatorOverlay.App.Ui;

public static class Visuals
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Visuals), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.RegisterAttached(
        "GlyphSize", typeof(double), typeof(Visuals), new FrameworkPropertyMetadata(14.0));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
        "Caption", typeof(string), typeof(Visuals), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Visuals), new FrameworkPropertyMetadata(new CornerRadius(9)));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);

    public static double GetGlyphSize(DependencyObject element) => (double)element.GetValue(GlyphSizeProperty);

    public static void SetGlyphSize(DependencyObject element, double value) => element.SetValue(GlyphSizeProperty, value);

    public static string? GetCaption(DependencyObject element) => (string?)element.GetValue(CaptionProperty);

    public static void SetCaption(DependencyObject element, string? value) => element.SetValue(CaptionProperty, value);

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);
}

public static class SelectionGuard
{
    public static readonly DependencyProperty KeepSelectedProperty = DependencyProperty.RegisterAttached(
        "KeepSelected", typeof(bool), typeof(SelectionGuard), new PropertyMetadata(false, OnKeepSelectedChanged));

    public static bool GetKeepSelected(DependencyObject element) => (bool)element.GetValue(KeepSelectedProperty);

    public static void SetKeepSelected(DependencyObject element, bool value) => element.SetValue(KeepSelectedProperty, value);

    private static void OnKeepSelectedChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ListBox list) return;
        list.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        list.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is not true) return;
        list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        list.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || sender is not ListBox list) return;
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is ListBoxItem { IsSelected: true })
        {
            e.Handled = true;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0) e.Handled = true;
    }
}
