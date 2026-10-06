using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;
using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.App.Ui;

public static class OverlayBlockRenderer
{
    public static bool IsCoverPlacement(AppSettings settings) =>
        settings.OverlayPlacement.Equals("cover", StringComparison.OrdinalIgnoreCase);

    public static bool IsBackgroundless(AppSettings settings) =>
        settings.OverlayBackgroundOpacity < 0.05;

    /// <summary>
    /// Rozmiar czcionki: jawny z ustawień albo (przy 0 = auto) dopasowany do wysokości
    /// oryginalnej linii tekstu z OCR — tłumaczenie wygląda wtedy jak tekst gry.
    /// </summary>
    public static double ResolveFontSize(AppSettings settings, int lineHeightPx, double scale)
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
    public static Brush CreateTextureBrush(BackgroundTexture texture, double opacity)
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
    public static void ApplyTextColors(Border element, AppSettings settings, int colorRgb, int backgroundRgb, int outlineRgb)
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

    public static TextBlock CreateBlockText(
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

        textBlock.FontFamily = OverlayFonts.Resolve(settings);

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

    public static double GetFontSize(Border element) => Inner(element) switch
    {
        OutlinedTextBlock outlined => outlined.FontSize,
        TextBlock text => text.FontSize,
        GameTextElement native => native.EmSize,
        _ => 0,
    };

    public static bool IsNative(Border element) => element.Child is GameTextElement;

    public static bool UsesNativeCover(AppSettings settings, GlyphCover? cover) => cover is not null && IsCoverPlacement(settings);

    public static bool HidesIdenticalText(AppSettings settings) => IsCoverPlacement(settings);

    public static string StripIconToken(string text, GlyphCover cover)
    {
        var result = text;
        if (cover.IconToken.Length > 0)
        {
            var trimmed = result.TrimStart();
            if (trimmed.Length > cover.IconToken.Length
                && trimmed.StartsWith(cover.IconToken, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[cover.IconToken.Length]))
            {
                result = trimmed[cover.IconToken.Length..].TrimStart();
            }
        }
        if (cover.TailToken.Length > 0)
        {
            var trimmed = result.TrimEnd();
            if (trimmed.Length > cover.TailToken.Length
                && trimmed.EndsWith(cover.TailToken, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[^(cover.TailToken.Length + 1)]))
            {
                result = trimmed[..^cover.TailToken.Length].TrimEnd();
            }
        }
        return result;
    }

    public static Border CreateNativeElement(string text, GlyphCover cover, double scale)
    {
        var native = new GameTextElement();
        var element = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Child = native,
        };
        ApplyNativeCover(element, cover, scale);
        SetText(element, text);
        return element;
    }

    public static void ApplyNativeCover(Border element, GlyphCover cover, double scale)
    {
        if (element.Child is not GameTextElement native) return;
        var bitmap = BitmapSource.Create(
            cover.PatchPixelWidth, cover.PatchPixelHeight, 96, 96, PixelFormats.Pbgra32, null,
            cover.PatchPbgra, cover.PatchPixelWidth * 4);
        bitmap.Freeze();
        native.SetPatch(bitmap, new Rect(0, 0, cover.PatchWidth / scale, cover.PatchHeight / scale));
        RenderOptions.SetBitmapScalingMode(native,
            Math.Abs(cover.PatchWidth - cover.PatchPixelWidth) < 0.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);

        var fill = new SolidColorBrush(cover.TextRgb >= 0 ? ToColor(cover.TextRgb) : Colors.White);
        fill.Freeze();
        native.Fill = fill;
        native.SetOutline(
            cover.OutlineRgb >= 0 ? ToColor(cover.OutlineRgb) : null,
            cover.OutlinePx / scale,
            new Vector(cover.ShadowDx / scale, cover.ShadowDy / scale));
        native.Tag = cover;
        if (native.RawText.Length > 0) native.Text = StripIconToken(native.RawText, cover);
    }

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private const double StyleAscentTolerance = 0.08;

    private static bool CloseColor(int a, int b)
    {
        if (a < 0 || b < 0) return a == b;
        return Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF))
            + Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF))
            + Math.Abs((a & 0xFF) - (b & 0xFF)) <= 90;
    }

    public static void LayoutNativeElement(
        Border element, RectPx box, MonitorArea monitor, AppSettings settings, string? profileFont, double freeRightPx = 0)
    {
        if (element.Child is not GameTextElement { Tag: GlyphCover cover } native) return;
        var scale = monitor.Scale;
        var family = OverlayFonts.ResolveFamilyName(settings, profileFont);
        var text = native.Text;
        var lines = cover.Lines;

        Typeface typeface;
        double em;
        double baseline;
        double anchor;
        var alignment = TextAlignment.Left;
        double available;
        double lineAdvance = 0;
        if (lines.Count == 0)
        {
            typeface = OverlayFonts.ChooseWeight(family, string.Empty, 0);
            em = Math.Max(6, box.Height / scale * 0.75);
            baseline = (box.Height * 0.8 - cover.PatchY) / scale;
            anchor = (cover.IconSkipPx - cover.PatchX) / scale;
            available = (box.Width - cover.IconSkipPx) / scale;
        }
        else
        {
            var (reference, referenceText) = WeightReference(cover, text)!.Value;
            var ascent = (reference.Baseline - reference.InkTop) / scale;
            var style = $"{family}|{cover.OutlinePx > 0}";
            if (native.StyleReference == style && native.StyleAscent > 0 && CloseColor(native.StyleTextRgb, cover.TextRgb)
                && Math.Abs(ascent - native.StyleAscent) <= Math.Max(1.0, native.StyleAscent * StyleAscentTolerance))
            {
                ascent = native.StyleAscent;
                typeface = native.Typeface;
            }
            else
            {
                typeface = OverlayFonts.ClassWeight(family, ascent, cover.TextRgb, cover.OutlinePx > 0,
                    OverlayFonts.ChooseStyleWeight(family, referenceText, reference.Density, ascent, cover.TextRgb, cover.OutlinePx > 0));
                native.StyleReference = style;
                native.StyleTextRgb = cover.TextRgb;
                native.StyleAscent = ascent;
            }
            var metrics = OverlayFonts.Measure(typeface, referenceText);
            em = metrics.Ascent > 0.05 ? ascent / metrics.Ascent : ascent / 0.7;
            em = Math.Clamp(em, 6, Math.Max(6, box.Height / scale * 1.6));
            baseline = (lines[0].Baseline - cover.PatchY) / scale + metrics.BaselineShift * em;
            var left = lines.Min(static l => l.InkLeft);
            var right = lines.Max(static l => l.InkRight);
            available = lines.Count > 1
                ? (right - left) / scale
                : Math.Max(right - left, box.Width - cover.IconSkipPx - cover.TailSkipPx) / scale;
            if (cover.TailSkipPx > 0) freeRightPx = 0;
            alignment = cover.Align switch
            {
                TextAlignHint.Center => TextAlignment.Center,
                TextAlignHint.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            if (IsScreenCentered(cover, box, monitor, left, right)) alignment = TextAlignment.Center;
            anchor = alignment switch
            {
                TextAlignment.Center => (lines.Average(static l => (l.InkLeft + l.InkRight) / 2) - cover.PatchX) / scale,
                TextAlignment.Right => (right - cover.PatchX) / scale,
                _ => (left - cover.PatchX) / scale,
            };
            if (lines.Count > 1 && cover.LinePitch > 0) lineAdvance = cover.LinePitch / scale;
        }

        var limit = lines.Count > 1
            ? Math.Max(1, available) * 1.08
            : alignment == TextAlignment.Center
                ? Math.Max(1, available) * 1.6
                : Math.Max(Math.Max(1, available) * 1.25, Math.Min(Math.Max(1, available) * 3.0, available + freeRightPx / scale * 0.9));
        var floor = 0.85;
        if (cover.TailSkipPx > 0)
        {
            limit = Math.Max(1, available) * 1.02;
            floor = 0.7;
        }
        var natural = WidestLine(text, typeface, em);
        var fitted = natural > limit ? em * Math.Max(floor, limit / natural) : em;
        double wrap = 0;
        if (lines.Count > 1 && WidestLine(text, typeface, fitted) > limit)
            wrap = Math.Max(Math.Max(1, available) * 1.04, WidestWord(text, typeface, fitted) + 1);

        native.Typeface = typeface;
        native.EmSize = fitted;
        native.SetLayout(anchor, baseline, alignment, wrap, lineAdvance);
        var ink = native.InkBounds;
        if (!ink.IsEmpty)
        {
            var shift = alignment switch
            {
                TextAlignment.Left => anchor - ink.Left,
                TextAlignment.Right => anchor - ink.Right,
                _ => 0,
            };
            if (Math.Abs(shift) > 0.01)
            {
                anchor += shift;
                native.SetLayout(anchor, baseline, alignment, wrap, lineAdvance);
            }
        }

        var originX = (box.X + cover.PatchX - monitor.Bounds.X) / scale;
        var originY = (box.Y + cover.PatchY - monitor.Bounds.Y) / scale;
        ink = native.InkBounds;
        if (!ink.IsEmpty)
        {
            var margin = native.OutlineThickness + Math.Max(0, native.ShadowOffset.X) + 2;
            var overflowRight = originX + ink.Right + margin - monitor.Bounds.Width / scale;
            var overflowLeft = -(originX + ink.Left - margin);
            var nudge = overflowRight > 0 ? -overflowRight : overflowLeft > 0 ? overflowLeft : 0;
            if (nudge != 0) native.SetLayout(anchor + nudge, baseline, alignment, wrap, lineAdvance);
        }

        element.MinWidth = 0;
        element.MinHeight = 0;
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(element, originX);
        Canvas.SetTop(element, originY);
    }

    private static (GlyphCoverLine Line, string Text)? WeightReference(GlyphCover cover, string text)
    {
        if (cover.Lines.Count == 0) return null;
        var reference = cover.Lines.OrderByDescending(static l => l.Baseline - l.InkTop).First();
        var referenceText = StripIconToken(reference.Text, cover).Trim();
        if (referenceText.Length == 0) referenceText = FirstLine(text);
        return (reference, referenceText);
    }

    public static void VoteNativeWeight(GlyphCover cover, string text, AppSettings settings, string? profileFont, double scale)
    {
        if (!UsesNativeCover(settings, cover) || WeightReference(cover, StripIconToken(text, cover)) is not { } reference) return;
        OverlayFonts.VoteWeight(
            OverlayFonts.ResolveFamilyName(settings, profileFont), reference.Text, reference.Line.Density,
            (reference.Line.Baseline - reference.Line.InkTop) / scale, cover.TextRgb, cover.OutlinePx > 0);
    }

    private static bool IsScreenCentered(GlyphCover cover, RectPx box, MonitorArea monitor, double left, double right)
    {
        if (cover.Lines.Count != 1 || cover.Align != TextAlignHint.Unknown || cover.IconSkipPx > 0 || cover.TailSkipPx > 0) return false;
        var inkCenter = box.X + (left + right) / 2;
        var screenCenter = monitor.Bounds.X + monitor.Bounds.Width / 2.0;
        return Math.Abs(inkCenter - screenCenter) <= Math.Max(8, monitor.Bounds.Width * 0.01);
    }

    private static string FirstLine(string text)
    {
        var cut = text.IndexOf('\n');
        return (cut >= 0 ? text[..cut] : text).Trim();
    }

    private static double WidestWord(string text, Typeface typeface, double em)
    {
        var widest = 0.0;
        foreach (var word in text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var formatted = GameTextElement.Format(word, typeface, em, Brushes.Black, 1.0);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }
        return widest;
    }

    private static double WidestLine(string text, Typeface typeface, double em)
    {
        var widest = 0.0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            var formatted = GameTextElement.Format(line, typeface, em, Brushes.Black, 1.0);
            widest = Math.Max(widest, formatted.Width);
        }
        return widest;
    }

    private static void SetFontSize(Border element, double fontSize)
    {
        switch (Inner(element))
        {
            case OutlinedTextBlock outlined: outlined.FontSize = fontSize; break;
            case TextBlock text: text.FontSize = fontSize; break;
        }
    }

    public static string GetText(Border element) => Inner(element) switch
    {
        OutlinedTextBlock outlined => outlined.Text,
        TextBlock text => text.Text,
        GameTextElement native => native.RawText,
        _ => string.Empty,
    };

    public static void SetText(Border element, string value)
    {
        switch (Inner(element))
        {
            case OutlinedTextBlock outlined: outlined.Text = value; break;
            case TextBlock text: text.Text = value; break;
            case GameTextElement native:
                native.RawText = value;
                native.Text = native.Tag is GlyphCover cover ? StripIconToken(value, cover) : value;
                break;
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

    public static void SetPatchFill(Border element, Brush fill)
    {
        if (element.Child is CoverPatchHost host) host.SetFill(fill);
        else element.Background = fill;
    }

    public static Border CreateBlockElement(
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
        return element;
    }

    /// <summary>Szerokość tekstu w DIP dla danej czcionki — bez udziału układu WPF (deterministycznie).</summary>
    private static double MeasureTextWidth(string text, AppSettings settings, double fontSize, double pixelsPerDip)
    {
        var family = OverlayFonts.Resolve(settings);
        var formatted = new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Black, pixelsPerDip);
        // Kontur (8 kopii przesuniętych o grubość) poszerza tekst o 2× grubość.
        var outline = 2 * Math.Max(1, Math.Round(fontSize / 14.0));
        return formatted.WidthIncludingTrailingWhitespace + outline;
    }

    public static void PositionBlockElement(
        Border element, RectPx box, MonitorArea monitor, AppSettings settings, double pixelsPerDip,
        IDictionary<string, string>? fitSignatures = null, string? fitKey = null)
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
            if (fitKey is null || fitSignatures is null
                || !fitSignatures.TryGetValue(fitKey, out var previousSignature) || previousSignature != signature)
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
                    if (MeasureTextWidth(text, settings, fontSize, pixelsPerDip) <= limit) break;
                    fontSize = Math.Max(floor, fontSize * 0.93);
                }
                SetFontSize(element, fontSize);
                if (fitKey is not null && fitSignatures is not null) fitSignatures[fitKey] = signature;
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
}
