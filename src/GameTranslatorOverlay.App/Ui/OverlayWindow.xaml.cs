using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Vision;
using GameTranslatorOverlay.Infrastructure.Settings;
using static GameTranslatorOverlay.App.Ui.OverlayBlockRenderer;

namespace GameTranslatorOverlay.App.Ui;

/// <summary>
/// Przezroczysta nakładka click-through z trzema warstwami: bloki ręczne (auto-ukrywane),
/// bloki live (zarządzane diffem po kluczach) i pasek napisów — oraz osobną warstwą
/// krótkich komunikatów dla gracza (przy górnej krawędzi okna gry). Nie przejmuje fokusu
/// ani kliknięć; jest wykluczana z przechwytywania ekranu (WDA_EXCLUDEFROMCAPTURE),
/// a gdy wykluczenie zawiedzie, pętla live ma dodatkowy filtr anty-sprzężeniowy.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _manualClearTimer = new();
    private readonly DispatcherTimer _subtitleTimer = new();
    private readonly Dictionary<string, Border> _liveElements = [];
    private readonly Dictionary<string, BackgroundTexture> _liveTextures = [];
    private readonly Dictionary<string, (int Color, int Background, int Outline)> _liveColors = [];
    private readonly Dictionary<string, string> _liveFit = [];
    private readonly Dictionary<string, GlyphCover> _liveCovers = [];
    private readonly List<Border> _manualElements = [];
    private Border? _subtitleElement;
    private MonitorArea? _monitor;
    private bool _hiddenByUser;

    private readonly DispatcherTimer _noticeTimer = new();
    private Border? _noticeElement;
    private RectPx _lastNoticeAnchor;

    // Krytyczny komunikat przy nakładce schowanej skrótem: okno pokazujemy tylko dla niego,
    // a warstwy tłumaczeń zostają schowane (decyzja gracza dotyczy napisów, nie ostrzeżeń).
    private bool _shownForNoticeOnly;

    /// <summary>Czy okno jest realnie wykluczone z przechwytywania ekranu.</summary>
    public bool IsCaptureExclusionActive { get; private set; }

    public string? ProfileFontFamily
    {
        get => OverlayFonts.ProfileFont;
        set => OverlayFonts.ProfileFont = value;
    }

    public OverlayWindow()
    {
        InitializeComponent();
        _manualClearTimer.Tick += (_, _) => ClearManualBlocks();
        _subtitleTimer.Tick += (_, _) => ClearSubtitle();
        _noticeTimer.Tick += (_, _) => FadeOutNotice();
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
        // Diagnostyka dev: GTO_DIAG_CAPTURABLE=1 zostawia nakładkę widoczną dla zrzutów
        // ekranu (żeby móc obejrzeć, co faktycznie rysujemy); filtr anty-sprzężeniowy
        // sesji live przejmuje wtedy ochronę.
        IsCaptureExclusionActive = Environment.GetEnvironmentVariable("GTO_DIAG_CAPTURABLE") != "1"
            && NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
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
            RestoreContentLayer();
            Show();
        }
    }

    /// <summary>Warstwa napisów wraca po komunikacie pokazanym przy schowanej nakładce.</summary>
    private void RestoreContentLayer()
    {
        _shownForNoticeOnly = false;
        RootCanvas.Visibility = Visibility.Visible;
    }

    private Border CreateBlockElement(
        string text, AppSettings settings, double scale, int lineHeightPx, int colorRgb = -1, int backgroundRgb = -1,
        int outlineRgb = -1, BackgroundTexture? texture = null, bool fadeIn = true)
    {
        var element = OverlayBlockRenderer.CreateBlockElement(
            text, settings, scale, lineHeightPx, colorRgb, backgroundRgb, outlineRgb, texture);

        // Płynne pojawianie zamiast wyskakiwania.
        if (fadeIn)
            element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        return element;
    }

    private void PositionBlockElement(Border element, RectPx box, MonitorArea monitor, AppSettings settings, string? fitKey = null) =>
        OverlayBlockRenderer.PositionBlockElement(
            element, box, monitor, settings, VisualTreeHelper.GetDpi(this).PixelsPerDip, _liveFit, fitKey);

    /// <summary>Jednorazowe wyświetlenie bloków z tłumaczenia ręcznego (auto-ukrywane).</summary>
    public void ShowBlocks(IReadOnlyList<(RectPx Box, string Text, int LineHeight, int ColorRgb)> blocks, AppSettings settings)
    {
        if (blocks.Count == 0) return;

        var overall = blocks.Aggregate(default(RectPx), static (acc, b) => acc.Union(b.Box));
        _monitor = Displays.FromRect(overall);
        var monitor = _monitor;

        ClearManualBlocks();
        _hiddenByUser = false;
        RestoreContentLayer();

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
        _liveBlocksForLayout = blocks;

        var hideIdentical = HidesIdenticalText(settings);
        var incomingKeys = blocks
            .Where(b => !(hideIdentical && b.SameAsSource))
            .Select(static b => b.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _liveElements.Keys.Where(key => !incomingKeys.Contains(key)).ToList())
        {
            RemoveLiveElement(staleKey);
        }

        foreach (var block in blocks)
        {
            if (incomingKeys.Contains(block.Key) && block.Cover is { } voteCover)
                VoteNativeWeight(voteCover, block.TranslatedText, settings, ProfileFontFamily, monitor.Scale);
        }

        foreach (var block in blocks)
        {
            if (!incomingKeys.Contains(block.Key)) continue;
            if (UsesNativeCover(settings, block.Cover))
            {
                UpdateNativeBlock(block, settings, monitor);
                continue;
            }
            if (_liveElements.TryGetValue(block.Key, out var existing) && IsNative(existing))
            {
                ReplaceLiveElement(block.Key, CreateBlockElement(
                    block.TranslatedText, settings, monitor.Scale, block.LineHeight,
                    block.ColorRgb, block.BackgroundRgb, block.OutlineRgb, block.Texture, fadeIn: false));
                _liveElements[block.Key].Tag = block.LineHeight;
                if (block.Texture is not null) _liveTextures[block.Key] = block.Texture;
                _liveColors[block.Key] = (block.ColorRgb, block.BackgroundRgb, block.OutlineRgb);
                PositionBlockElement(_liveElements[block.Key], block.ScreenBox, monitor, settings, block.Key);
                continue;
            }

            // Istniejący dymek aktualizujemy W MIEJSCU — także przy zmianie rozmiaru oryginału
            // (najechany element menu rośnie): czcionka i łatka skalują się jak napis w grze,
            // bez odtwarzania i fade-inu. Fade-in dostają tylko naprawdę nowe bloki.
            if (_liveElements.TryGetValue(block.Key, out var element))
            {
                element.Tag = block.LineHeight;
                if (GetText(element) != block.TranslatedText)
                {
                    SetText(element, block.TranslatedText);
                }

                // Grafika pod napisem mogła się przewinąć — podmieniamy samą teksturę tła
                // w miejscu, bez odtwarzania (i fade-inu) dymka.
                if (block.Texture is not null && IsCoverPlacement(settings) && !IsBackgroundless(settings)
                    && (!_liveTextures.TryGetValue(block.Key, out var shown) || !ReferenceEquals(shown, block.Texture)))
                {
                    SetPatchFill(element, CreateTextureBrush(block.Texture, 1.0));
                    _liveTextures[block.Key] = block.Texture;
                }

                // Zmiana tła pod napisem (najechany rząd) przeliczyła kolory — podmieniamy
                // kolor tekstu i konturu w miejscu, bez odtwarzania dymka.
                var colors = (block.ColorRgb, block.BackgroundRgb, block.OutlineRgb);
                if (!_liveColors.TryGetValue(block.Key, out var shownColors) || shownColors != colors)
                {
                    ApplyTextColors(element, settings, block.ColorRgb, block.BackgroundRgb, block.OutlineRgb);
                    _liveColors[block.Key] = colors;
                }
            }
            else
            {
                element = CreateBlockElement(
                    block.TranslatedText, settings, monitor.Scale, block.LineHeight,
                    block.ColorRgb, block.BackgroundRgb, block.OutlineRgb, block.Texture);
                element.Tag = block.LineHeight;
                _liveElements[block.Key] = element;
                if (block.Texture is not null) _liveTextures[block.Key] = block.Texture;
                _liveColors[block.Key] = (block.ColorRgb, block.BackgroundRgb, block.OutlineRgb);
                RootCanvas.Children.Add(element);
            }

            PositionBlockElement(element, block.ScreenBox, monitor, settings, block.Key);
        }

        CoverMonitor(monitor);
        ShowIfAllowed();
    }

    private void UpdateNativeBlock(LiveDisplayBlock block, AppSettings settings, MonitorArea monitor)
    {
        var cover = block.Cover!;
        if (_liveElements.TryGetValue(block.Key, out var element) && IsNative(element))
        {
            if (!_liveCovers.TryGetValue(block.Key, out var shown) || !ReferenceEquals(shown, cover))
            {
                ApplyNativeCover(element, cover, monitor.Scale);
                _liveCovers[block.Key] = cover;
            }
            if (GetText(element) != block.TranslatedText) SetText(element, block.TranslatedText);
        }
        else
        {
            var created = CreateNativeElement(block.TranslatedText, cover, monitor.Scale);
            if (element is not null)
            {
                ReplaceLiveElement(block.Key, created);
            }
            else
            {
                created.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
                _liveElements[block.Key] = created;
                RootCanvas.Children.Add(created);
            }
            _liveCovers[block.Key] = cover;
            _liveTextures.Remove(block.Key);
            _liveColors.Remove(block.Key);
            _liveFit.Remove(block.Key);
            element = created;
        }
        LayoutNativeElement(element, block.ScreenBox, monitor, settings, ProfileFontFamily, FreeSpaceRight(block, monitor));
    }

    private double FreeSpaceRight(LiveDisplayBlock block, MonitorArea monitor)
    {
        var box = block.ScreenBox;
        var free = (double)(monitor.Bounds.Right - box.Right);
        foreach (var other in _liveBlocksForLayout)
        {
            if (ReferenceEquals(other, block)) continue;
            var o = other.ScreenBox;
            var overlap = Math.Min(o.Bottom, box.Bottom) - Math.Max(o.Y, box.Y);
            if (overlap < box.Height * 0.3 || o.X < box.Right - 2) continue;
            free = Math.Min(free, o.X - box.Right);
        }
        return Math.Max(0, free);
    }

    private IReadOnlyList<LiveDisplayBlock> _liveBlocksForLayout = [];

    private void ReplaceLiveElement(string key, Border replacement)
    {
        var index = _liveElements.TryGetValue(key, out var old) ? RootCanvas.Children.IndexOf(old) : -1;
        if (old is not null) RootCanvas.Children.Remove(old);
        if (index >= 0 && index <= RootCanvas.Children.Count) RootCanvas.Children.Insert(index, replacement);
        else RootCanvas.Children.Add(replacement);
        _liveElements[key] = replacement;
        _liveCovers.Remove(key);
        _liveTextures.Remove(key);
        _liveColors.Remove(key);
        _liveFit.Remove(key);
    }

    private void RemoveLiveElement(string key)
    {
        if (_liveElements.Remove(key, out var element)) RootCanvas.Children.Remove(element);
        _liveTextures.Remove(key);
        _liveColors.Remove(key);
        _liveFit.Remove(key);
        _liveCovers.Remove(key);
    }

    /// <summary>Pasek napisów na dole okna gry (tryb Subtitle) — pokazuje najnowszy tekst.</summary>
    public void ShowSubtitle(string text, RectPx gameWindowBounds, AppSettings settings, bool preserveLifetime = false)
    {
        // Removing one source from an existing subtitle must neither restart its
        // timer nor bring an already expired subtitle back onto the screen.
        if (preserveLifetime && _subtitleElement is null) return;
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

        if (!preserveLifetime)
        {
            _subtitleTimer.Stop();
            if (settings.SubtitleSeconds > 0)
            {
                _subtitleTimer.Interval = TimeSpan.FromSeconds(settings.SubtitleSeconds);
                _subtitleTimer.Start();
            }
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
        // Komunikat dla gracza trzyma okno widoczne aż do swojego wygaśnięcia.
        if (RootCanvas.Children.Count == 0 && _noticeElement is null)
        {
            Hide();
        }
    }

    /// <summary>
    /// Krótki komunikat (~13 px, półprzezroczyste tło) wyśrodkowany przy górnej krawędzi
    /// okna gry, z płynnym pojawieniem i wygaszeniem. Nie przyjmuje kliknięć i nie znika
    /// przy czyszczeniu bloków. Gdy gracz schował nakładkę, przechodzą tylko komunikaty
    /// krytyczne (zatrzymany live, klucz, limit) i odpowiedzi na ręczne tłumaczenie
    /// (<see cref="OverlayNotice.ShowsWhenHiddenByUser"/>). Zwraca false, gdy komunikat pominięto.
    /// </summary>
    public bool ShowNotice(OverlayNotice notice, RectPx gameWindowBounds)
    {
        if (_hiddenByUser && !notice.ShowsWhenHiddenByUser) return false;

        if (!gameWindowBounds.IsEmpty) _lastNoticeAnchor = gameWindowBounds;
        var anchor = _lastNoticeAnchor;
        // Bez znanego okna gry (np. stop zanim przyszła pierwsza klatka) — monitor z kursorem.
        var monitor = RootCanvas.Children.Count > 0 && _monitor is not null
            ? _monitor
            : anchor.IsEmpty ? Displays.FromCursor() : Displays.FromRect(anchor);
        if (anchor.IsEmpty) anchor = monitor.Bounds;

        if (_noticeElement is null)
        {
            _noticeElement = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x0B, 0x0E, 0x11)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                IsHitTestVisible = false,
                // Start od zera — animacja bez From jedzie od bieżącej wartości, a przy 1,0
                // pojawienie się byłoby skokowe.
                Opacity = 0,
                Child = new TextBlock
                {
                    FontSize = 13,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.NoWrap,
                },
            };
            NoticeCanvas.Children.Add(_noticeElement);
        }

        var text = (TextBlock)_noticeElement.Child;
        text.Text = notice.Text;
        text.Foreground = notice.Severity switch
        {
            NoticeSeverity.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)),
            NoticeSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
            _ => Brushes.White,
        };

        _noticeElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = monitor.Scale;
        var centerX = (anchor.X + anchor.Width / 2.0 - monitor.Bounds.X) / scale;
        var top = (anchor.Y - monitor.Bounds.Y) / scale + 12;
        Canvas.SetLeft(_noticeElement, Math.Max(0, centerX - _noticeElement.DesiredSize.Width / 2));
        Canvas.SetTop(_noticeElement, Math.Max(0, top));

        _noticeElement.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _noticeTimer.Stop();
        _noticeTimer.Interval = notice.Duration > TimeSpan.Zero ? notice.Duration : OverlayNotices.InfoDuration;
        _noticeTimer.Start();

        if (RootCanvas.Children.Count == 0 || _monitor is null) _monitor = monitor;
        // Okno schowane skrótem albo chwilowo (ruch sceny) — pokazujemy sam komunikat;
        // nieaktualne napisy nie mogą wrócić na ekran razem z nim.
        if (_hiddenByUser || (!IsVisible && RootCanvas.Children.Count > 0))
        {
            _shownForNoticeOnly = true;
            RootCanvas.Visibility = Visibility.Collapsed;
        }
        CoverMonitor(_monitor);
        Show();
        return true;
    }

    private void FadeOutNotice()
    {
        _noticeTimer.Stop();
        if (_noticeElement is not { } element) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            // Nowy komunikat mógł przyjść w trakcie wygaszania — wtedy element żyje dalej.
            if (!ReferenceEquals(_noticeElement, element) || _noticeTimer.IsEnabled) return;
            RemoveNotice();
        };
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void RemoveNotice()
    {
        _noticeTimer.Stop();
        if (_noticeElement is not null)
        {
            NoticeCanvas.Children.Remove(_noticeElement);
            _noticeElement = null;
        }
        if (_shownForNoticeOnly)
        {
            // Okno było schowane przed komunikatem (skrót gracza albo ruch sceny) — wraca
            // do tego stanu; napisy pokaże dopiero zwykła aktualizacja.
            RestoreContentLayer();
            Hide();
            return;
        }
        HideIfEmpty();
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

    public void ClearSubtitle()
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
        _liveTextures.Clear();
        _liveColors.Clear();
        _liveFit.Clear();
        _liveCovers.Clear();
        HideIfEmpty();
    }

    public void ClearBlocks(bool preserveUserHidden = false)
    {
        _manualClearTimer.Stop();
        _subtitleTimer.Stop();
        RootCanvas.Children.Clear();
        _liveElements.Clear();
        _liveTextures.Clear();
        _liveColors.Clear();
        _liveFit.Clear();
        _liveCovers.Clear();
        _manualElements.Clear();
        _subtitleElement = null;
        if (!preserveUserHidden)
        {
            _hiddenByUser = false;
            RestoreContentLayer();
        }
        // Komunikat dla gracza (np. „live zatrzymany”) nie znika razem z napisami.
        HideIfEmpty();
    }

    public void ToggleVisibility()
    {
        // Rozstrzygamy po DECYZJI użytkownika, nie po IsVisible — okno bywa ukryte
        // automatycznie (ruch sceny) albo pokazane natywnie poza wiedzą WPF,
        // a skrót ma zawsze przełączać intencję: „chcę widzieć / nie chcę widzieć”.
        if (_hiddenByUser)
        {
            _hiddenByUser = false;
            RestoreContentLayer();
            if (RootCanvas.Children.Count > 0 || _noticeElement is not null)
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
