using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.App.Ui;

/// <summary>
/// Przezroczysta nakładka click-through: wyświetla tłumaczenia przy oryginalnym tekście
/// albo jako pasek napisów, nie przejmuje fokusu ani kliknięć. Jest wykluczona
/// z przechwytywania ekranu (WDA_EXCLUDEFROMCAPTURE), więc OCR nigdy nie czyta
/// własnych tłumaczeń.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _clearTimer = new();
    private readonly DispatcherTimer _subtitleTimer = new();
    private readonly Dictionary<string, Border> _liveElements = [];
    private Border? _subtitleElement;
    private MonitorArea? _monitor;

    public OverlayWindow()
    {
        InitializeComponent();
        _clearTimer.Tick += (_, _) => ClearBlocks();
        _subtitleTimer.Tick += (_, _) => ClearSubtitle();
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

        // Nakładka nie może trafiać do przechwytywanego obrazu — inaczej OCR
        // czytałby własne tłumaczenia (pętla sprzężenia zwrotnego).
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Przy przejściu na monitor o innym DPI WPF sam skaluje rozmiar okna wg
        // WM_DPICHANGED — wymuszamy ponownie pełne pokrycie monitora w fizycznych px.
        if (_monitor is { } monitor)
        {
            Dispatcher.BeginInvoke(() => CoverMonitor(monitor));
        }
    }

    private void CoverMonitor(MonitorArea monitor)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    private Border CreateBlockElement(string text, AppSettings settings)
    {
        var background = Color.FromArgb(
            (byte)Math.Clamp(settings.OverlayBackgroundOpacity * 255, 0, 255), 0x0B, 0x0E, 0x11);

        return new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = Math.Max(9, settings.OverlayFontSize),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private void PositionBlockElement(Border element, RectPx box, MonitorArea monitor)
    {
        var scale = monitor.Scale;
        element.MaxWidth = Math.Max(140, (monitor.Bounds.Right - box.X) / scale - 12);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var left = (box.X - monitor.Bounds.X) / scale;
        var top = (box.Bottom - monitor.Bounds.Y) / scale + 4;

        // Tłumaczenie pojawia się pod oryginałem; przy dolnej krawędzi — nad nim.
        var monitorHeightDip = monitor.Bounds.Height / scale;
        if (top + element.DesiredSize.Height > monitorHeightDip)
        {
            top = Math.Max(0, (box.Y - monitor.Bounds.Y) / scale - element.DesiredSize.Height - 4);
        }

        Canvas.SetLeft(element, Math.Max(0, left));
        Canvas.SetTop(element, top);
    }

    /// <summary>Jednorazowe wyświetlenie bloków (tryb ręczny). Czyści wynik poprzedni i elementy live.</summary>
    public void ShowBlocks(IReadOnlyList<(RectPx Box, string Text)> blocks, AppSettings settings)
    {
        if (blocks.Count == 0) return;

        var overall = blocks.Aggregate(default(RectPx), static (acc, b) => acc.Union(b.Box));
        _monitor = Displays.FromRect(overall);
        var monitor = _monitor;

        RootCanvas.Children.Clear();
        _liveElements.Clear();
        _subtitleElement = null;

        foreach (var (box, text) in blocks)
        {
            var element = CreateBlockElement(text, settings);
            PositionBlockElement(element, box, monitor);
            RootCanvas.Children.Add(element);
        }

        CoverMonitor(monitor);
        Show();

        _clearTimer.Stop();
        if (settings.ResultAutoHideSeconds > 0)
        {
            _clearTimer.Interval = TimeSpan.FromSeconds(settings.ResultAutoHideSeconds);
            _clearTimer.Start();
        }
    }

    /// <summary>
    /// Aktualizacja bloków w trybie live: elementy o istniejących kluczach są przesuwane,
    /// nowe dodawane, nieaktualne usuwane — bez migotania całej nakładki.
    /// </summary>
    public void UpdateLiveBlocks(IReadOnlyList<LiveDisplayBlock> blocks, AppSettings settings)
    {
        _clearTimer.Stop();

        if (blocks.Count == 0)
        {
            ClearLiveBlocks();
            return;
        }

        var overall = blocks.Aggregate(default(RectPx), static (acc, b) => acc.Union(b.ScreenBox));
        _monitor = Displays.FromRect(overall);
        var monitor = _monitor;

        var incomingKeys = blocks.Select(static b => b.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _liveElements.Keys.Where(key => !incomingKeys.Contains(key)).ToList())
        {
            RootCanvas.Children.Remove(_liveElements[staleKey]);
            _liveElements.Remove(staleKey);
        }

        foreach (var block in blocks)
        {
            if (_liveElements.TryGetValue(block.Key, out var element))
            {
                if (element.Child is TextBlock textBlock && textBlock.Text != block.TranslatedText)
                {
                    textBlock.Text = block.TranslatedText;
                }
            }
            else
            {
                element = CreateBlockElement(block.TranslatedText, settings);
                _liveElements[block.Key] = element;
                RootCanvas.Children.Add(element);
            }

            PositionBlockElement(element, block.ScreenBox, monitor);
        }

        CoverMonitor(monitor);
        Show();
    }

    /// <summary>Pasek napisów na dole okna gry (tryb Subtitle) — pokazuje najnowszy tekst.</summary>
    public void ShowSubtitle(string text, RectPx gameWindowBounds, AppSettings settings)
    {
        _monitor = Displays.FromRect(gameWindowBounds);
        var monitor = _monitor;
        var scale = monitor.Scale;

        if (_subtitleElement is null)
        {
            _subtitleElement = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Clamp(settings.OverlayBackgroundOpacity * 255, 0, 255), 0x0B, 0x0E, 0x11)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Child = new TextBlock
                {
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            };
            RootCanvas.Children.Add(_subtitleElement);
        }

        var subtitleText = (TextBlock)_subtitleElement.Child;
        subtitleText.Text = text;
        subtitleText.FontSize = Math.Max(12, settings.OverlayFontSize + 2);
        _subtitleElement.MaxWidth = Math.Max(320, gameWindowBounds.Width * 0.7 / scale);
        _subtitleElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var centerX = (gameWindowBounds.X + gameWindowBounds.Width / 2.0 - monitor.Bounds.X) / scale;
        var bottomY = (gameWindowBounds.Bottom - monitor.Bounds.Y) / scale;
        Canvas.SetLeft(_subtitleElement, Math.Max(0, centerX - _subtitleElement.DesiredSize.Width / 2));
        Canvas.SetTop(_subtitleElement, Math.Max(0, bottomY - _subtitleElement.DesiredSize.Height - 48));

        CoverMonitor(monitor);
        Show();

        _subtitleTimer.Stop();
        if (settings.SubtitleSeconds > 0)
        {
            _subtitleTimer.Interval = TimeSpan.FromSeconds(settings.SubtitleSeconds);
            _subtitleTimer.Start();
        }
    }

    private void ClearSubtitle()
    {
        _subtitleTimer.Stop();
        if (_subtitleElement is not null)
        {
            RootCanvas.Children.Remove(_subtitleElement);
            _subtitleElement = null;
        }
        if (RootCanvas.Children.Count == 0)
        {
            Hide();
        }
    }

    private void ClearLiveBlocks()
    {
        foreach (var element in _liveElements.Values)
        {
            RootCanvas.Children.Remove(element);
        }
        _liveElements.Clear();
        if (RootCanvas.Children.Count == 0)
        {
            Hide();
        }
    }

    public void ClearBlocks()
    {
        _clearTimer.Stop();
        _subtitleTimer.Stop();
        RootCanvas.Children.Clear();
        _liveElements.Clear();
        _subtitleElement = null;
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
