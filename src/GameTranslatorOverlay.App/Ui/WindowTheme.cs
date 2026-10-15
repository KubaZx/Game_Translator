using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GameTranslatorOverlay.App.Interop;

namespace GameTranslatorOverlay.App.Ui;

public static class WindowTheme
{
    public static readonly DependencyProperty DarkFrameProperty = DependencyProperty.RegisterAttached(
        "DarkFrame", typeof(bool), typeof(WindowTheme), new PropertyMetadata(false, OnDarkFrameChanged));

    public static bool GetDarkFrame(DependencyObject element) => (bool)element.GetValue(DarkFrameProperty);

    public static void SetDarkFrame(DependencyObject element, bool value) => element.SetValue(DarkFrameProperty, value);

    public static Thickness MaximizedInset(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var dpiValue = (uint)Math.Round(dpi.PixelsPerInchX);
        var frame = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSIZEFRAME, dpiValue)
                    + NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpiValue);
        return frame > 0 ? new Thickness(frame / dpi.DpiScaleX) : new Thickness(7);
    }

    public static int SmallIconPixels(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var pixels = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, (uint)Math.Round(dpi.PixelsPerInchX));
        return pixels >= 16 ? pixels : 16;
    }

    private static void OnDarkFrameChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window || e.NewValue is not true) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            Apply(handle);
            return;
        }
        window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        Apply(new WindowInteropHelper(window).Handle);
    }

    private static void Apply(IntPtr handle)
    {
        var enabled = 1;
        if (NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
        }
        var corners = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        var border = ColorRef(0x1F, 0x26, 0x33);
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var caption = ColorRef(0x0B, 0x0D, 0x12);
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        var text = ColorRef(0xE8, 0xEC, 0xF4);
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private static int ColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);
}
