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
/// Przezroczysta nakładka click-through z trzema warstwami: bloki ręczne (auto-ukrywane),
/// bloki live (zarządzane diffem po kluczach) i pasek napisów. Nie przejmuje fokusu
/// ani kliknięć; jest wykluczana z przechwytywania ekranu (WDA_EXCLUDEFROMCAPTURE),
/// a gdy wykluczenie zawiedzie, pętla live ma dodatkowy filtr anty-sprzężeniowy.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _manualClearTimer = new();
    private readonly DispatcherTimer _subtitleTimer = new();
    private readonly Dictionary<string, Border> _liveElements = [];
    private readonly List<Border> _manualElements = [];
    private Border? _subtitleElement;
    private MonitorArea? _monitor;
    private bool _hiddenByUser;

    /// <summary>Czy okno jest realnie wykluczone z przechwytywania ekranu.</summary>
    public bool IsCaptureExclusionActive { get; private set; }

    public OverlayWindow()
    {
        InitializeComponent();
        _manualClearTimer.Tick += (_, _) => ClearManualBlocks();
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
        IsCaptureExclusionActive = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Tworzy HWND bez pokazywania okna — pozwala wcześnie sprawdzić wykluczenie z capture.</summary>
    public void EnsureHandleCreated() => new WindowInteropHelper(this).EnsureHandle();

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
        // Gdy użytkownik ukrył nakładkę (Ctrl+Shift+H), pozycjonujemy BEZ SWP_SHOWWINDOW —
        // natywne pokazanie obchodziłoby WPF-owe Hide() i przybijało nad grą zamrożoną
        // klatkę sprzed ukrycia. Pokazywaniem zarządza wyłącznie ShowIfAllowed()/Show().
        var flags = NativeMethods.SWP_NOACTIVATE;
        if (!_hiddenByUser)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height,
            flags);
    }

    private void ShowIfAllowed()
    {
        if (!_hiddenByUser)
        {
            Show();
        }
    }

    private static bool IsCoverPlacement(AppSettings settings) =>
        settings.OverlayPlacement.Equals("cover", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackgroundless(AppSettings settings) =>
        settings.OverlayBackgroundOpacity < 0.05;

    /// <summary>
    /// Rozmiar czcionki: jawny z ustawień albo (przy 0 = auto) dopasowany do wysokości
    /// oryginalnej linii tekstu z OCR — tłumaczenie wygląda wtedy jak tekst gry.
    /// </summary>
    private static double ResolveFontSize(AppSettings settings, int lineHeightPx, double scale)
    {
        if (settings.OverlayFontSize >= 9) return settings.OverlayFontSize;
        if (lineHeightPx > 0) return Math.Clamp(lineHeightPx / scale * 0.75, 9, 48);
        return 15;
    }

    private static TextBlock CreateBlockText(string text, AppSettings settings, double fontSize, int colorRgb = -1)
    {
        Brush foreground = Brushes.White;
        if (colorRgb >= 0)
        {
            var r = (byte)(colorRgb >> 16);
            var g = (byte)(colorRgb >> 8);
            var b = (byte)colorRgb;
            // Zbyt ciemny kolor (nieudane próbkowanie) psułby czytelność — zostaje biały.
            if (0.299 * r + 0.587 * g + 0.114 * b >= 90)
            {
                foreground = new SolidColorBrush(Color.FromRgb(r, g, b));
            }
        }

        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
        };

        if (!string.IsNullOrWhiteSpace(settings.OverlayFontFamily))
        {
            textBlock.FontFamily = new FontFamily(settings.OverlayFontFamily);
        }

        // Bez tła tekst dostaje czarną poświatę — inaczej ginąłby na jasnych scenach.
        if (IsBackgroundless(settings))
        {
            textBlock.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 5,
                ShadowDepth = 0,
                Opacity = 1.0,
            };
        }

        return textBlock;
    }

    private Border CreateBlockElement(string text, AppSettings settings, double scale, int lineHeightPx, int colorRgb = -1)
    {
        Brush background;
        if (IsBackgroundless(settings))
        {
            background = Brushes.Transparent;
        }
        else
        {
            // W trybie zakrywania tło musi realnie schować oryginalny tekst pod spodem.
            var opacity = IsCoverPlacement(settings)
                ? Math.Max(settings.OverlayBackgroundOpacity, 0.95)
                : settings.OverlayBackgroundOpacity;
            background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(opacity * 255, 0, 255), 0x0B, 0x0E, 0x11));
        }

        var textBlock = CreateBlockText(text, settings, ResolveFontSize(settings, lineHeightPx, scale), colorRgb);
        if (IsCoverPlacement(settings))
        {
            // W trybie zakrywania tekst centruje się w prostokącie oryginału.
            textBlock.VerticalAlignment = VerticalAlignment.Center;
        }

        var element = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            // Bez tła liczy się precyzyjne trafienie w pole oryginału — zero odstępu.
            Padding = IsBackgroundless(settings) ? new Thickness(0) : new Thickness(7, 4, 7, 4),
            Child = textBlock,
        };

        // Płynne pojawianie zamiast wyskakiwania.
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        return element;
    }

    private void PositionBlockElement(Border element, RectPx box, MonitorArea monitor, AppSettings settings)
    {
        var scale = monitor.Scale;
        var cover = IsCoverPlacement(settings);
        var monitorHeightDip = monitor.Bounds.Height / scale;

        if (cover)
        {
            // Dymek ma pokryć cały prostokąt oryginalnego tekstu; polski tekst bywa
            // dłuższy, więc blok może urosnąć w dół — nie ściskamy go na siłę.
            element.MinWidth = Math.Max(0, box.Width / scale);
            element.MinHeight = Math.Max(0, box.Height / scale);
        }
        else
        {
            element.MinWidth = 0;
            element.MinHeight = 0;
        }

        element.MaxWidth = Math.Max(140, (monitor.Bounds.Right - box.X) / scale - 12);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var left = (box.X - monitor.Bounds.X) / scale;
        double top;

        if (cover)
        {
            // Dokładnie na oryginale; przy dolnej krawędzi dosuwamy w górę, żeby nie uciąć.
            top = (box.Y - monitor.Bounds.Y) / scale;
            if (top + element.DesiredSize.Height > monitorHeightDip)
            {
                top = Math.Max(0, monitorHeightDip - element.DesiredSize.Height);
            }
        }
        else
        {
            // Tłumaczenie pojawia się pod oryginałem; przy dolnej krawędzi — nad nim.
            top = (box.Bottom - monitor.Bounds.Y) / scale + 4;
            if (top + element.DesiredSize.Height > monitorHeightDip)
            {
                top = Math.Max(0, (box.Y - monitor.Bounds.Y) / scale - element.DesiredSize.Height - 4);
            }
        }

        Canvas.SetLeft(element, Math.Max(0, left));
        Canvas.SetTop(element, top);
    }

    /// <summary>Jednorazowe wyświetlenie bloków z tłumaczenia ręcznego (auto-ukrywane).</summary>
    public void ShowBlocks(IReadOnlyList<(RectPx Box, string Text, int LineHeight, int ColorRgb)> blocks, AppSettings settings)
    {
        if (blocks.Count == 0) return;

        var overall = blocks.Aggregate(default(RectPx), static (acc, b) => acc.Union(b.Box));
        _monitor = Displays.FromRect(overall);
        var monitor = _monitor;

        ClearManualBlocks();
        _hiddenByUser = false;

        foreach (var (box, text, lineHeight, colorRgb) in blocks)
        {
            var element = CreateBlockElement(text, settings, monitor.Scale, lineHeight, colorRgb);
            PositionBlockElement(element, box, monitor, settings);
            _manualElements.Add(element);
            RootCanvas.Children.Add(element);
        }

        CoverMonitor(monitor);
        Show();

        if (settings.ResultAutoHideSeconds > 0)
        {
            _manualClearTimer.Interval = TimeSpan.FromSeconds(settings.ResultAutoHideSeconds);
            _manualClearTimer.Start();
        }
    }

    /// <summary>
    /// Aktualizacja bloków w trybie live: elementy o istniejących kluczach są przesuwane,
    /// nowe dodawane, nieaktualne usuwane — bez migotania całej nakładki.
    /// </summary>
    public void UpdateLiveBlocks(IReadOnlyList<LiveDisplayBlock> blocks, AppSettings settings)
    {
        // Warstwy ręczna i napisów nie mogą zalegać pod aktualizacjami live.
        ClearManualBlocks();
        ClearSubtitle();

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
            // Element odtwarzamy tylko przy realnej zmianie rozmiaru oryginału —
            // drobne wahania OCR wygładza histereza po stronie sesji.
            if (_liveElements.TryGetValue(block.Key, out var element)
                && element.Tag is int previousLineHeight
                && previousLineHeight == block.LineHeight)
            {
                if (element.Child is TextBlock textBlock && textBlock.Text != block.TranslatedText)
                {
                    textBlock.Text = block.TranslatedText;
                }
            }
            else
            {
                if (element is not null)
                {
                    RootCanvas.Children.Remove(element);
                }
                element = CreateBlockElement(block.TranslatedText, settings, monitor.Scale, block.LineHeight, block.ColorRgb);
                element.Tag = block.LineHeight;
                _liveElements[block.Key] = element;
                RootCanvas.Children.Add(element);
            }

            PositionBlockElement(element, block.ScreenBox, monitor, settings);
        }

        CoverMonitor(monitor);
        ShowIfAllowed();
    }

    /// <summary>Pasek napisów na dole okna gry (tryb Subtitle) — pokazuje najnowszy tekst.</summary>
    public void ShowSubtitle(string text, RectPx gameWindowBounds, AppSettings settings)
    {
        // Warstwy ręczna i bloków live nie mogą zalegać pod napisami.
        ClearManualBlocks();
        ClearLiveBlocks();

        _monitor = Displays.FromRect(gameWindowBounds);
        var monitor = _monitor;

        if (_subtitleElement is null)
        {
            var subtitleFontSize = settings.OverlayFontSize >= 9 ? settings.OverlayFontSize + 2 : 18;
            var subtitleContent = CreateBlockText(string.Empty, settings, subtitleFontSize);
            subtitleContent.TextAlignment = TextAlignment.Center;
            _subtitleElement = new Border
            {
                Background = IsBackgroundless(settings)
                    ? Brushes.Transparent
                    : new SolidColorBrush(Color.FromArgb(
                        (byte)Math.Clamp(settings.OverlayBackgroundOpacity * 255, 0, 255), 0x0B, 0x0E, 0x11)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Child = subtitleContent,
            };
            RootCanvas.Children.Add(_subtitleElement);
        }

        var subtitleText = (TextBlock)_subtitleElement.Child;
        subtitleText.Text = text;
        subtitleText.FontSize = settings.OverlayFontSize >= 9 ? settings.OverlayFontSize + 2 : 18;
        PositionSubtitle(gameWindowBounds, monitor);

        CoverMonitor(monitor);
        ShowIfAllowed();

        _subtitleTimer.Stop();
        if (settings.SubtitleSeconds > 0)
        {
            _subtitleTimer.Interval = TimeSpan.FromSeconds(settings.SubtitleSeconds);
            _subtitleTimer.Start();
        }
    }

    /// <summary>Przesuwa istniejący pasek napisów, gdy okno gry zmieniło pozycję (bez zmiany tekstu).</summary>
    public void RepositionSubtitle(RectPx gameWindowBounds)
    {
        if (_subtitleElement is null) return;
        _monitor = Displays.FromRect(gameWindowBounds);
        PositionSubtitle(gameWindowBounds, _monitor);
        CoverMonitor(_monitor);
    }

    private void PositionSubtitle(RectPx gameWindowBounds, MonitorArea monitor)
    {
        if (_subtitleElement is null) return;
        var scale = monitor.Scale;
        _subtitleElement.MaxWidth = Math.Max(320, gameWindowBounds.Width * 0.7 / scale);
        _subtitleElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var centerX = (gameWindowBounds.X + gameWindowBounds.Width / 2.0 - monitor.Bounds.X) / scale;
        var bottomY = (gameWindowBounds.Bottom - monitor.Bounds.Y) / scale;
        Canvas.SetLeft(_subtitleElement, Math.Max(0, centerX - _subtitleElement.DesiredSize.Width / 2));
        Canvas.SetTop(_subtitleElement, Math.Max(0, bottomY - _subtitleElement.DesiredSize.Height - 48));
    }

    private void HideIfEmpty()
    {
        if (RootCanvas.Children.Count == 0)
        {
            Hide();
        }
    }

    private void ClearManualBlocks()
    {
        _manualClearTimer.Stop();
        foreach (var element in _manualElements)
        {
            RootCanvas.Children.Remove(element);
        }
        _manualElements.Clear();
        HideIfEmpty();
    }

    private void ClearSubtitle()
    {
        _subtitleTimer.Stop();
        if (_subtitleElement is not null)
        {
            RootCanvas.Children.Remove(_subtitleElement);
            _subtitleElement = null;
        }
        HideIfEmpty();
    }

    private void ClearLiveBlocks()
    {
        foreach (var element in _liveElements.Values)
        {
            RootCanvas.Children.Remove(element);
        }
        _liveElements.Clear();
        HideIfEmpty();
    }

    public void ClearBlocks()
    {
        _manualClearTimer.Stop();
        _subtitleTimer.Stop();
        RootCanvas.Children.Clear();
        _liveElements.Clear();
        _manualElements.Clear();
        _subtitleElement = null;
        _hiddenByUser = false;
        Hide();
    }

    public void ToggleVisibility()
    {
        // Rozstrzygamy po DECYZJI użytkownika, nie po IsVisible — okno bywa ukryte
        // automatycznie (ruch sceny) albo pokazane natywnie poza wiedzą WPF,
        // a skrót ma zawsze przełączać intencję: „chcę widzieć / nie chcę widzieć”.
        if (_hiddenByUser)
        {
            _hiddenByUser = false;
            if (RootCanvas.Children.Count > 0)
            {
                Show();
            }
        }
        else
        {
            _hiddenByUser = true;
            Hide();
        }
    }
}
