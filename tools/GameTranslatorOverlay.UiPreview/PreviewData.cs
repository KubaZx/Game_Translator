using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Content;
using OcrTextBlock = GameTranslatorOverlay.Core.Text.TextBlock;
using TextBlock = System.Windows.Controls.TextBlock;

internal static class PreviewData
{
    private const string SourceLine = "The lighthouse keeper left a sealed note on the desk.";
    private const string TargetLine = "Latarnik zostawił na biurku zapieczętowany list.";

    public static void SeedGlossary(UserGlossaryStore store)
    {
        store.AddTerm(new GlossaryTerm("lighthouse", "latarnia", Priority: 100), "en", "pl");
        store.AddTerm(new GlossaryTerm("keeper", "latarnik", Priority: 100, Note: "postać z rozdziału 2"), "en", "pl");
        store.AddTerm(new GlossaryTerm("sealed note", "zapieczętowany list", Priority: 120), "en", "pl");
        store.AddTerm(new GlossaryTerm("Open", "Otwórz", CaseSensitive: true, Priority: 100, Scope: GlossaryScope.Label), "en", "pl");
    }

    public static void SeedQualityWarnings(QualityWarningLog log, UsageTracker usage)
    {
        TranslationOutcome Warned(string source, string translated, TranslationQualityFlags issues) =>
            new(source, source, translated, TranslationOrigin.Provider) { QualityIssues = issues };

        log.Record(Warned("Talk to the keeper about the beacon", "Talk to the keeper about the beacon", TranslationQualityFlags.Untranslated));
        log.Record(Warned("Light the beacon before 10:30 PM.", "Zapal latarnię przed 22:30.", TranslationQualityFlags.NumbersChanged));
        log.Record(Warned("Yes", "Tak. (Uwaga tłumacza: w tym miejscu „yes” oznacza zgodę na zadanie latarnika, a nie zwykłe potwierdzenie.)",
            TranslationQualityFlags.Runaway));
        log.Record(Warned("The keeper owes you 150 gold for the 3 sealed notes.", "Latarnik jest ci winien złoto za zapieczętowane listy.",
            TranslationQualityFlags.NumbersChanged | TranslationQualityFlags.Runaway));
        log.MarkCorrected("Light the beacon before 10:30 PM.", "Zapal latarnię przed 22:30.");
        usage.RecordQualityIssue(TranslationQualityFlags.NumbersChanged);
        usage.RecordQualityIssue(TranslationQualityFlags.Untranslated);
        usage.RecordQualityRetry();
        usage.RecordQualityRetry();
    }

    public static void Fill(Window window, UsageTracker usage)
    {
        var explorerPid = 0;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            if (explorerPid == 0) explorerPid = process.Id;
            process.Dispose();
        }
        var windows = new List<TargetWindow>
        {
            new(IntPtr.Zero, "Mroczna Latarnia: Opowieść o Strażniku Świateł Północy — Rozdział 2", "MrocznaLatarnia.exe", Environment.ProcessId) { Area = 1920L * 1080 },
            new(IntPtr.Zero, "Eksplorator plików — Pobrane", "explorer.exe", explorerPid) { Area = 1280L * 800 },
            new(IntPtr.Zero, "Notatki do zagadek.txt — Notatnik", "Notepad.exe", 0) { Area = 900L * 600 },
            new(IntPtr.Zero, "Czat drużyny — komunikator", "Komunikator.exe", 0) { Area = 1100L * 700 },
        };
        if (window.FindName("WindowsList") is Selector list)
        {
            list.ItemsSource = windows;
            list.SelectedIndex = 0;
        }

        SetText(window, "TxtLastOcr", SourceLine + "\n\nOpen the drawer");
        SetText(window, "TxtLastTranslation", TargetLine + "\n\nOtwórz szufladę");
        SetText(window, "TxtTiming", "Czasy: " + new PipelineTimings(14, 88, 402, 517));
        SetText(window, "TxtCacheStatus", "Cache SQLite: 1 284 wpisów (12 ręcznych korekt), 412 KB.");
        SetText(window, "TxtStatus", "Przetłumaczono 2 bloki tekstu (517 ms).");
        if (window.FindName("PreviewImage") is Image preview) preview.Source = Scene(1280, 720);

        for (var i = 0; i < 23; i++) usage.RecordApiRequest(64 + i * 5);
        for (var i = 0; i < 64; i++) usage.RecordCacheHit();
        for (var i = 0; i < 7; i++) usage.RecordGlossaryHit();
        usage.RecordFailure();
        foreach (var ms in new[] { 820.0, 940, 1100, 760, 1300, 880 }) usage.Latency.Record(LatencyStage.ChangeToText, ms);
        foreach (var ms in new[] { 610.0, 700, 820, 655, 590 }) usage.Latency.Record(LatencyStage.NewText, ms);
        foreach (var ms in new[] { 190.0, 210, 205 }) usage.Latency.Record(LatencyStage.KnownText, ms);
        foreach (var ms in new[] { 380.0, 420, 455, 510, 390 }) usage.Latency.Record(LatencyStage.Provider, ms);
        foreach (var ms in new[] { 85.0, 92, 101 }) usage.Latency.Record(LatencyStage.Ocr, ms);
        foreach (var ms in new[] { 14.0, 18, 16 }) usage.Latency.Record(LatencyStage.Capture, ms);
    }

    public static RegionTranslationResult PanelResult()
    {
        TranslatedBlock Block(string source, string? target, TranslationOrigin origin, int y,
            TranslationQualityFlags issues = TranslationQualityFlags.None) =>
            new(new OcrTextBlock(source, new RectPx(-30000, y, 600, 40), []),
                new TranslationOutcome(source, source, target, origin) { QualityIssues = issues });
        return new RegionTranslationResult(
            new RectPx(-30000, 300, 640, 160),
            [
                Block(SourceLine, TargetLine, TranslationOrigin.Provider, 300),
                Block("Open the drawer", "Otwórz szufladę", TranslationOrigin.Glossary, 350),
                Block("Careful now! The 3rd step creaks.", "Ostrożnie! Stopień skrzypi.", TranslationOrigin.Cache, 400,
                    TranslationQualityFlags.NumbersChanged),
            ],
            new PipelineTimings(14, 88, 402, 517),
            "Tryb Cache-only: tekst spoza cache zostanie pominięty.");
    }

    public static ImageSource GameBackdrop() => Scene(960, 540);

    private static void SetText(Window window, string name, string text)
    {
        switch (window.FindName(name))
        {
            case TextBox box:
                box.Text = text;
                break;
            case TextBlock block:
                block.Text = text;
                break;
        }
    }

    private static BitmapSource Scene(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x4A), Color.FromRgb(0x0B, 0x10, 0x20), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, width, height));
            var moonGlow = new RadialGradientBrush(Color.FromArgb(0x70, 0xF5, 0xF1, 0xD0), Color.FromArgb(0, 0xF5, 0xF1, 0xD0));
            dc.DrawEllipse(moonGlow, null, new Point(width * 0.78, height * 0.2), width * 0.12, width * 0.12);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF5, 0xF1, 0xD0)), null, new Point(width * 0.78, height * 0.2), width * 0.04, width * 0.04);
            var sea = new LinearGradientBrush(Color.FromRgb(0x0E, 0x3A, 0x4F), Color.FromRgb(0x06, 0x14, 0x1C), 90);
            dc.DrawRectangle(sea, null, new Rect(0, height * 0.68, width, height * 0.32));
            var beam = new StreamGeometry();
            using (var g = beam.Open())
            {
                g.BeginFigure(new Point(width * 0.24, height * 0.3), true, true);
                g.LineTo(new Point(width * 0.62, height * 0.18), false, false);
                g.LineTo(new Point(width * 0.62, height * 0.36), false, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xE7, 0x9A)), null, beam);
            var tower = new Rect(width * 0.215, height * 0.3, width * 0.05, height * 0.4);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDA)), null, tower);
            for (var i = 0; i < 3; i++)
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xB2, 0x3A, 0x3A)), null,
                    new Rect(tower.X, tower.Y + tower.Height * (0.15 + i * 0.3), tower.Width, tower.Height * 0.12));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0xE7, 0x9A)), null, new Rect(tower.X - 4, tower.Y - 18, tower.Width + 8, 18));
            var dialog = new Rect(width * 0.04, height * 0.76, width * 0.92, height * 0.18);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD0, 0x05, 0x07, 0x0C)), new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xC9, 0xB2, 0x7C)), 2), dialog, 10, 10);
            var text = new FormattedText(SourceLine, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Georgia"), height * 0.042, Brushes.White, 1.0) { MaxTextWidth = dialog.Width - 48 };
            dc.DrawText(text, new Point(dialog.X + 24, dialog.Y + (dialog.Height - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
