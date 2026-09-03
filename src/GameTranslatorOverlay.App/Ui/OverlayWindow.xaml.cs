using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;
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
    private readonly Dictionary<string, BackgroundTexture> _liveTextures = [];
    private readonly Dictionary<string, (int Color, int Background, int Outline)> _liveColors = [];
    private readonly Dictionary<string, string> _liveFit = [];
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
        // Zakrywanie ZASTĘPUJE napis gry — rozmiar musi wynikać z oryginału, inaczej
        // ręczne „15” zmienia 60-pikselowy tytuł w drobny druk w rogu wielkiej łatki.
        // Ręczny rozmiar obowiązuje w panelu, pod oryginałem i w napisach.
        if (settings.OverlayFontSize >= 9 && !(IsCoverPlacement(settings) && lineHeightPx > 0))
        {
            return settings.OverlayFontSize;
        }
        if (lineHeightPx > 0) return Math.Clamp(lineHeightPx / scale * 0.75, 9, 72);
        return 15;
    }

    /// <summary>Łatka jako rozmyta kopia tła spod tekstu (mini-siatka rozciągnięta z interpolacją).</summary>
    private static Brush CreateTextureBrush(BackgroundTexture texture, double opacity)
    {
        var bitmap = BitmapSource.Create(
            texture.Columns, texture.Rows, 96, 96, PixelFormats.Rgb24, null, texture.Rgb, texture.Columns * 3);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            Opacity = opacity,
        };
        brush.Freeze();
        return brush;
    }

    private static double Luminance(int rgb) =>
        0.299 * ((rgb >> 16) & 0xFF) + 0.587 * ((rgb >> 8) & 0xFF) + 0.114 * (rgb & 0xFF);

    /// <summary>Podmiana koloru tekstu/konturu istniejącego dymka bez jego odtwarzania.</summary>
    private static void ApplyTextColors(Border element, AppSettings settings, int colorRgb, int backgroundRgb, int outlineRgb)
    {
        var sampledCover = IsCoverPlacement(settings) && !IsBackgroundless(settings) && backgroundRgb >= 0;
        var foreground = ResolveForeground(colorRgb, sampledCover ? backgroundRgb : -1);
        switch (Inner(element))
        {
            case OutlinedTextBlock outlined:
                outlined.Primary.Foreground = foreground;
                if (outlineRgb >= 0)
                {
                    outlined.SetOutlineColor(Color.FromRgb((byte)(outlineRgb >> 16), (byte)(outlineRgb >> 8), (byte)outlineRgb));
                }
                break;
            case TextBlock text:
                text.Foreground = foreground;
                break;
        }
    }

    private static Brush ResolveForeground(int colorRgb, int backgroundRgb)
    {
        Brush foreground = Brushes.White;
        if (colorRgb >= 0 && backgroundRgb >= 0)
        {
            if (Math.Abs(Luminance(colorRgb) - Luminance(backgroundRgb)) >= 60)
            {
                foreground = new SolidColorBrush(Color.FromRgb(
                    (byte)(colorRgb >> 16), (byte)(colorRgb >> 8), (byte)colorRgb));
            }
            else
            {
                foreground = Luminance(backgroundRgb) >= 128 ? Brushes.Black : Brushes.White;
            }
        }
        else if (colorRgb >= 0 && Luminance(colorRgb) >= 90)
        {
            foreground = new SolidColorBrush(Color.FromRgb(
                (byte)(colorRgb >> 16), (byte)(colorRgb >> 8), (byte)colorRgb));
        }
        return foreground;
    }

    private static TextBlock CreateBlockText(
        string text, AppSettings settings, double fontSize, int colorRgb = -1, int backgroundRgb = -1)
    {
        Brush foreground = Brushes.White;
        if (colorRgb >= 0 && backgroundRgb >= 0)
        {
            // Znamy tło łatki: kolor z próbkowania zostaje, o ile realnie kontrastuje —
            // dzięki temu ciemny tekst na jasnym oknie (visual novele) też jest wierny.
            if (Math.Abs(Luminance(colorRgb) - Luminance(backgroundRgb)) >= 60)
            {
                foreground = new SolidColorBrush(Color.FromRgb(
                    (byte)(colorRgb >> 16), (byte)(colorRgb >> 8), (byte)colorRgb));
            }
            else
            {
                foreground = Luminance(backgroundRgb) >= 128 ? Brushes.Black : Brushes.White;
            }
        }
        else if (colorRgb >= 0)
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

    /// <summary>Właściwy element tekstowy dymka (z pominięciem hosta łatki, jeśli jest).</summary>
    private static UIElement? Inner(Border element) => element.Child is CoverPatchHost host ? host.Content : element.Child;

    private static double GetFontSize(Border element) => Inner(element) switch
    {
        OutlinedTextBlock outlined => outlined.FontSize,
        TextBlock text => text.FontSize,
        _ => 0,
    };

    private static void SetFontSize(Border element, double fontSize)
    {
        switch (Inner(element))
        {
            case OutlinedTextBlock outlined: outlined.FontSize = fontSize; break;
            case TextBlock text: text.FontSize = fontSize; break;
        }
    }

    private static string GetText(Border element) => Inner(element) switch
    {
        OutlinedTextBlock outlined => outlined.Text,
        TextBlock text => text.Text,
        _ => string.Empty,
    };

    private static void SetText(Border element, string value)
    {
        switch (Inner(element))
        {
            case OutlinedTextBlock outlined: outlined.Text = value; break;
            case TextBlock text: text.Text = value; break;
        }
    }

    private static void SetLineHeight(Border element, double lineHeight)
    {
        switch (Inner(element))
        {
            case OutlinedTextBlock outlined:
                outlined.SetLineHeight(lineHeight, LineStackingStrategy.BlockLineHeight);
                break;
            case TextBlock text:
                text.LineHeight = lineHeight;
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                break;
        }
    }

    private static void SetPatchFill(Border element, Brush fill)
    {
        if (element.Child is CoverPatchHost host) host.SetFill(fill);
        else element.Background = fill;
    }

    private Border CreateBlockElement(
        string text, AppSettings settings, double scale, int lineHeightPx, int colorRgb = -1, int backgroundRgb = -1,
        int outlineRgb = -1, BackgroundTexture? texture = null)
    {
        var cover = IsCoverPlacement(settings);
        var sampledCover = cover && !IsBackgroundless(settings) && (backgroundRgb >= 0 || texture is not null);

        Brush background;
        if (IsBackgroundless(settings))
        {
            background = Brushes.Transparent;
        }
        else if (sampledCover && texture is not null)
        {
            // Wtapianie: łatka to rozmyta kopia tła spod tekstu — na grafice podąża za
            // gradientem, na oknie dialogowym jest płaska; oryginał znika pod nią.
            background = CreateTextureBrush(texture, 1.0);
        }
        else if (sampledCover)
        {
            background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(Math.Max(settings.OverlayBackgroundOpacity, 0.97) * 255, 0, 255),
                (byte)(backgroundRgb >> 16), (byte)(backgroundRgb >> 8), (byte)backgroundRgb));
        }
        else
        {
            // W trybie zakrywania tło musi realnie schować oryginalny tekst pod spodem.
            var opacity = cover
                ? Math.Max(settings.OverlayBackgroundOpacity, 0.95)
                : settings.OverlayBackgroundOpacity;
            background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(opacity * 255, 0, 255), 0x0B, 0x0E, 0x11));
        }

        var textBlock = CreateBlockText(text, settings, ResolveFontSize(settings, lineHeightPx, scale), colorRgb, sampledCover ? backgroundRgb : -1);
        var multiLine = text.Contains('\n');
        if (cover)
        {
            // Jednoliniowy tekst centruje się w polu oryginału; wieloliniowy trzyma górę,
            // bo jego wiersze dostają wysokość linii oryginału (PositionBlockElement).
            textBlock.VerticalAlignment = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center;
        }

        // Kontur w kolorze z gry — to on robi „natywność” czcionki. Zastępuje rozmytą
        // czarną poświatę trybu bez tła, a nad światem 3D pozwala w ogóle zrezygnować z łatki.
        // W zakrywaniu dłuższy polski tekst wystaje poza łatkę nad grafikę — bez konturu
        // z gry dostaje domyślny (czarny pod jasnym tekstem, biały pod ciemnym).
        if (outlineRgb < 0 && sampledCover)
        {
            var textLum = colorRgb >= 0 ? Luminance(colorRgb) : 255;
            outlineRgb = textLum >= 128 ? 0x000000 : 0xFFFFFF;
        }
        FrameworkElement content = textBlock;
        if (outlineRgb >= 0)
        {
            textBlock.Effect = null;
            content = new OutlinedTextBlock(textBlock, Color.FromRgb(
                (byte)(outlineRgb >> 16), (byte)(outlineRgb >> 8), (byte)outlineRgb));
        }

        // Wtapianie: miękka łatka wyłącznie pod boxem oryginału; tekst może wystawać.
        UIElement child = content;
        if (sampledCover)
        {
            child = new CoverPatchHost(content, background);
            background = Brushes.Transparent;
        }

        var element = new Border
        {
            Background = background,
            // Wtopiona łatka ma udawać tekst gry: bez dymkowych rogów i grubego paddingu.
            CornerRadius = new CornerRadius(sampledCover ? 0 : 4),
            Padding = IsBackgroundless(settings) || sampledCover ? new Thickness(0) : new Thickness(7, 4, 7, 4),
            Child = child,
        };

        // Mini-siatka tła rozciąga się z interpolacją liniową — gradient, nie kafelki.
        RenderOptions.SetBitmapScalingMode(element, BitmapScalingMode.Linear);

        // Płynne pojawianie zamiast wyskakiwania.
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        return element;
    }

    /// <summary>Szerokość tekstu w DIP dla danej czcionki — bez udziału układu WPF (deterministycznie).</summary>
    private double MeasureTextWidth(string text, AppSettings settings, double fontSize)
    {
        var family = string.IsNullOrWhiteSpace(settings.OverlayFontFamily)
            ? new FontFamily("Segoe UI")
            : new FontFamily(settings.OverlayFontFamily);
        var formatted = new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // Kontur (8 kopii przesuniętych o grubość) poszerza tekst o 2× grubość.
        var outline = 2 * Math.Max(1, Math.Round(fontSize / 14.0));
        return formatted.WidthIncludingTrailingWhitespace + outline;
    }

    private void PositionBlockElement(Border element, RectPx box, MonitorArea monitor, AppSettings settings, string? fitKey = null)
    {
        var scale = monitor.Scale;
        var cover = IsCoverPlacement(settings);
        var monitorHeightDip = monitor.Bounds.Height / scale;

        // Zapas zakrycia: krawędzie antyaliasingu oryginalnych glifów wystają poza
        // box OCR — bez niego spod łatki prześwituje obwódka starego tekstu. Miękka
        // (rozmyta) łatka potrzebuje większego zapasu, bo jej brzeg wygasa.
        var coverInsetPx = element.Child is CoverPatchHost ? 8.0 : 3.0;
        var inset = cover ? coverInsetPx / scale : 0;

        if (cover)
        {
            // Dymek ma pokryć cały prostokąt oryginalnego tekstu; polski tekst bywa
            // dłuższy, więc blok może urosnąć w dół — nie ściskamy go na siłę.
            element.MinWidth = Math.Max(0, box.Width / scale + 2 * inset);
            element.MinHeight = Math.Max(0, box.Height / scale + 2 * inset);
            if (element.Child is CoverPatchHost host)
            {
                host.SetPatchSize(element.MinWidth, element.MinHeight);
            }
        }
        else
        {
            element.MinWidth = 0;
            element.MinHeight = 0;
        }

        element.MaxWidth = Math.Max(140, (monitor.Bounds.Right - box.X) / scale - 12);

        // Wtapianie: polski bywa ~20% dłuższy — zmniejszamy czcionkę, ale najwyżej do 85%
        // oryginału (dalej tekst wystaje poza łatkę, czytelny dzięki konturowi).
        // Szerokość tekstu liczymy DETERMINISTYCZNIE (FormattedText), nie przez Measure
        // elementu — wynik Measure zależał od stanu układu z poprzedniej aktualizacji
        // i ten sam blok raz wychodził duży, raz mały. Rozmiar przeliczamy tylko, gdy
        // zmieni się tekst, box albo wysokość linii; w innym razie czcionki nie ruszamy.
        if (cover && element.Tag is int coverLineHeight && coverLineHeight > 0)
        {
            var text = GetText(element);
            var signature = $"{text}|{coverLineHeight}|{box.Width}|{box.Height}|{settings.OverlayFontFamily}";
            if (fitKey is null || !_liveFit.TryGetValue(fitKey, out var previousSignature) || previousSignature != signature)
            {
                var lineCount = Math.Max(1, text.Count(static c => c == '\n') + 1);
                var fontSize = ResolveFontSize(settings, coverLineHeight, scale);
                if (lineCount > 1)
                {
                    var pitch = box.Height / scale / lineCount;
                    fontSize = Math.Min(fontSize, Math.Max(9, pitch / 1.25));
                    SetLineHeight(element, pitch);
                }

                var floor = Math.Max(9, fontSize * 0.85);
                var limit = element.MinWidth * 1.08;
                for (var i = 0; i < 8 && fontSize > floor; i++)
                {
                    if (MeasureTextWidth(text, settings, fontSize) <= limit) break;
                    fontSize = Math.Max(floor, fontSize * 0.93);
                }
                SetFontSize(element, fontSize);
                if (fitKey is not null) _liveFit[fitKey] = signature;
            }
        }

        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var left = (box.X - monitor.Bounds.X) / scale - inset;
        double top;

        if (cover)
        {
            // Dokładnie na oryginale; przy dolnej krawędzi dosuwamy w górę, żeby nie uciąć.
            top = (box.Y - monitor.Bounds.Y) / scale - inset;
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
            _liveTextures.Remove(staleKey);
            _liveColors.Remove(staleKey);
            _liveFit.Remove(staleKey);
        }

        foreach (var block in blocks)
        {
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
        _liveTextures.Clear();
        _liveColors.Clear();
        _liveFit.Clear();
        HideIfEmpty();
    }

    public void ClearBlocks()
    {
        _manualClearTimer.Stop();
        _subtitleTimer.Stop();
        RootCanvas.Children.Clear();
        _liveElements.Clear();
        _liveTextures.Clear();
        _liveColors.Clear();
        _liveFit.Clear();
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
