using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

// Stanowisko diagnostyczne trybu live: własne okno testowe (ciemne tło, subtelny
// „ambient”, tekst zmieniający się w czasie, na końcu ruchomy prostokąt symulujący bieg)
// + LiveTranslationSession z Mockiem, wypisująca każde zdarzenie na konsolę.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Użycie: LiveDiag [sekundy]  — okno testowe z animacjami
        //         LiveDiag "fragment tytułu" [sekundy] — podłączenie pod ISTNIEJĄCE okno
        string? attachTitle = null;
        var seconds = 36;
        if (args.Length > 0 && !int.TryParse(args[0], out seconds))
        {
            attachTitle = args[0];
            seconds = args.Length > 1 && int.TryParse(args[1], out var s2) ? s2 : 25;
        }

        var hwnd = IntPtr.Zero;

        if (attachTitle is not null)
        {
            var target = GameTranslatorOverlay.App.Interop.WindowEnumerator.GetOpenWindows()
                .FirstOrDefault(w => w.Title.Contains(attachTitle, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.WriteLine($"Nie znalazłem okna zawierającego „{attachTitle}”. Dostępne okna:");
                foreach (var w in GameTranslatorOverlay.App.Interop.WindowEnumerator.GetOpenWindows())
                {
                    Console.WriteLine($"  • {w.DisplayName}");
                }
                return 2;
            }
            Console.WriteLine($"== podłączam się pod: {target.DisplayName} ==");
            hwnd = target.Handle;
        }
        else
        {
            using var windowReady = new ManualResetEventSlim();
            var uiThread = new Thread(() =>
            {
                var window = BuildTestWindow();
                window.SourceInitialized += (_, _) =>
                {
                    hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    windowReady.Set();
                };
                window.Show();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            windowReady.Wait();
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "gto-livediag-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(tempRoot);
        paths.EnsureCreated();
        var settings = new AppSettings { Provider = "Mock" };
        var cache = new SqliteTranslationCache(paths.DatabasePath);
        cache.Initialize();
        using var loggerFactory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));

        var orchestrator = new TranslationOrchestrator(
            settings, cache, new GlossaryService(),
            GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths), new UserGlossaryStore(paths),
            new MockTranslationProvider(), new DeepLTranslationProvider(new HttpClient(), static () => null),
            new WindowsOcrProvider(), new UsageTracker(), loggerFactory);
        orchestrator.Initialize();

        var frameDumpDir = Path.Combine(Path.GetTempPath(), "gto-livediag-frames");
        Console.WriteLine($"== zrzuty klatek przy whiffach OCR: {frameDumpDir} ==");

        var started = DateTime.UtcNow;
        using var session = new LiveTranslationSession(
            orchestrator, new WindowsOcrProvider(), hwnd,
            new LiveSessionOptions { DebugFrameDumpDir = frameDumpDir },
            update =>
            {
                var t = (DateTime.UtcNow - started).TotalSeconds;
                var blocks = update.Blocks is null ? "-" : update.Blocks.Count.ToString();
                Console.WriteLine($"[{t,6:0.00}s] {update.StatusLine}  [blocks={blocks} hide={update.HideOverlay} clear={update.ClearOverlay}]");
                if (update.Blocks is { Count: > 0 })
                {
                    foreach (var block in update.Blocks.Take(4))
                    {
                        var text = block.TranslatedText.Length > 60 ? block.TranslatedText[..60] + "…" : block.TranslatedText;
                        Console.WriteLine($"         → ({block.ScreenBox.X},{block.ScreenBox.Y} {block.ScreenBox.Width}×{block.ScreenBox.Height} lh={block.LineHeight}) \"{text.Replace('\n', '|')}\"");
                    }
                }
            },
            loggerFactory.CreateLogger("LiveDiag"));

        Console.WriteLine($"== LiveDiag start: hwnd={hwnd}, czas {seconds}s ==");
        Console.WriteLine("== plan: 0–8s statyczny tekst • 8–24s zmiana nagłówka co 5s • 24–30s ruchomy prostokąt („bieg”) • potem spokój ==");
        session.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        session.Stop();
        Thread.Sleep(500);
        Console.WriteLine("== LiveDiag koniec ==");

        try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        return 0;
    }

    private static Window BuildTestWindow()
    {
        var headline = new TextBlock
        {
            Text = "Fireball deals 25% increased damage",
            FontSize = 34,
            Foreground = Brushes.White,
        };
        Canvas.SetLeft(headline, 60);
        Canvas.SetTop(headline, 80);

        var body = new TextBlock
        {
            Text = "Level 20   Strength 15   Energy Shield 120",
            FontSize = 22,
            Foreground = Brushes.Gold,
        };
        Canvas.SetLeft(body, 60);
        Canvas.SetTop(body, 170);

        var mover = new Border
        {
            Width = 260,
            Height = 140,
            Background = new SolidColorBrush(Color.FromRgb(210, 70, 40)),
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetTop(mover, 330);

        var ambient = new System.Windows.Shapes.Rectangle
        {
            Width = 2000,
            Height = 1200,
            Fill = Brushes.White,
            Opacity = 0.0,
            IsHitTestVisible = false,
        };

        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1C)) };
        canvas.Children.Add(ambient);
        canvas.Children.Add(headline);
        canvas.Children.Add(body);
        canvas.Children.Add(mover);

        var window = new Window
        {
            Title = "GTO LiveDiag Test Window",
            Width = 900,
            Height = 600,
            Left = 60,
            Top = 60,
            Content = canvas,
        };

        var phrases = new[]
        {
            "Fireball deals 25% increased damage",
            "The Miller wants to talk to you",
            "Buy or Sell items at the vendor",
            "Quest complete: Clearfell Encampment",
        };
        var phraseIndex = 0;
        var start = DateTime.UtcNow;
        var lastHeadlineChange = 0.0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - start).TotalSeconds;

            // Subtelny „ambient” jak mgła w grze — ledwo widoczne pulsowanie jasności.
            ambient.Opacity = 0.015 + 0.015 * Math.Sin(t * 2.3);

            if (t is > 8 and < 24 && t - lastHeadlineChange >= 5)
            {
                lastHeadlineChange = t;
                phraseIndex = (phraseIndex + 1) % phrases.Length;
                headline.Text = phrases[phraseIndex];
            }

            if (t is > 24 and < 30)
            {
                mover.Visibility = Visibility.Visible;
                Canvas.SetLeft(mover, 40 + (t - 24) * 120);
            }
            else if (t >= 30 && mover.Visibility == Visibility.Visible)
            {
                mover.Visibility = Visibility.Collapsed;
            }
        };
        window.Loaded += (_, _) => timer.Start();
        return window;
    }
}
