using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.App.Ui;

/// <summary>
/// Przezroczysta nakładka click-through: wyświetla tłumaczenia przy oryginalnym tekście,
/// nie przejmuje fokusu ani kliknięć — sterowanie grą pozostaje nietknięte.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _clearTimer = new();
    private MonitorArea? _monitor;

    public OverlayWindow()
    {
        InitializeComponent();
        _clearTimer.Tick += (_, _) => ClearBlocks();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public void ShowBlocks(IReadOnlyList<(RectPx Box, string Text)> blocks, AppSettings settings)
    {
        if (blocks.Count == 0) return;

        var overall = blocks.Aggregate(default(RectPx), static (acc, b) => acc.Union(b.Box));
        _monitor = Displays.FromRect(overall);
        var monitor = _monitor;
        var scale = monitor.Scale;

        RootCanvas.Children.Clear();

        foreach (var (box, text) in blocks)
        {
            var background = Color.FromArgb(
                (byte)Math.Clamp(settings.OverlayBackgroundOpacity * 255, 0, 255), 0x0B, 0x0E, 0x11);

            var border = new Border
            {
                Background = new SolidColorBrush(background),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 4, 7, 4),
                MaxWidth = Math.Max(140, (monitor.Bounds.Right - box.X) / scale - 12),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = Math.Max(9, settings.OverlayFontSize),
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var left = (box.X - monitor.Bounds.X) / scale;
            var top = (box.Bottom - monitor.Bounds.Y) / scale + 4;

            // Tłumaczenie pojawia się pod oryginałem; przy dolnej krawędzi — nad nim.
            var monitorHeightDip = monitor.Bounds.Height / scale;
            if (top + border.DesiredSize.Height > monitorHeightDip)
            {
                top = Math.Max(0, (box.Y - monitor.Bounds.Y) / scale - border.DesiredSize.Height - 4);
            }

            Canvas.SetLeft(border, Math.Max(0, left));
            Canvas.SetTop(border, top);
            RootCanvas.Children.Add(border);
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        Show();

        _clearTimer.Stop();
        if (settings.ResultAutoHideSeconds > 0)
        {
            _clearTimer.Interval = TimeSpan.FromSeconds(settings.ResultAutoHideSeconds);
            _clearTimer.Start();
        }
    }

    public void ClearBlocks()
    {
        _clearTimer.Stop();
        RootCanvas.Children.Clear();
        Hide();
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else if (RootCanvas.Children.Count > 0)
        {
            Show();
        }
    }
}
