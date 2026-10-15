using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTranslatorOverlay.App;
using GameTranslatorOverlay.App.Hotkeys;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

internal static class PreviewSession
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task<int> RunAsync(string output)
    {
        var root = Path.Combine(Path.GetTempPath(), "gto-uipreview-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var report = new PreviewReport(output);

        var settings = new AppSettings
        {
            Provider = MockTranslationProvider.ProviderName,
            PrivateMode = true,
            DisclaimerAcknowledged = true,
            TranslateHotkey = "Ctrl+Alt+Shift+F21",
            ToggleOverlayHotkey = "Ctrl+Alt+Shift+F22",
            LiveToggleHotkey = "Ctrl+Alt+Shift+F23",
        };

        using var loggerFactory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        using var http = new HttpClient(new NoNetworkHandler());
        var cache = new SqliteTranslationCache(paths.DatabasePath);
        cache.Initialize();
        var usage = new UsageTracker();
        var ocr = new WindowsOcrProvider();
        var userGlossary = new UserGlossaryStore(paths);
        PreviewData.SeedGlossary(userGlossary);

        ITranslationProvider[] providers =
        [
            new AzureTranslatorProvider(http, static () => null, () => settings.AzureRegion),
            new GoogleTranslateProvider(http, static () => null),
            new OpenAiCompatibleTranslationProvider(http, static () => null, () => settings.LlmKeyHost,
                () => settings.LlmEndpoint, () => settings.LlmModel, serverOptionsAccessor: () => settings.LlmServerOptions),
            new ClaudeTranslationProvider(http, static () => null, () => settings.ClaudeModel),
        ];
        var orchestrator = new TranslationOrchestrator(settings, cache, new GlossaryService(),
            GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths), userGlossary,
            new MockTranslationProvider { Delay = TimeSpan.Zero }, new DeepLTranslationProvider(http, static () => null),
            ocr, usage, loggerFactory, providers, CorpusCatalog.CreateDefault(paths));
        orchestrator.Initialize();

        MainWindow? window = null;
        var exitCode = 0;
        try
        {
            window = new MainWindow(orchestrator, settings, new JsonSettingsStore(paths), new MemorySecrets(), usage, ocr,
                cache, new HotkeyManager(), userGlossary, paths, loggerFactory.CreateLogger<MainWindow>())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = Offscreen.Left,
                Top = Offscreen.Top,
            };
            window.Show();
            await Waits.UntilAsync(() => window.IsLoaded, TimeSpan.FromSeconds(10), "Loaded");
            await Waits.UntilAsync(() => Find<ItemsControl>(window, "WindowsList")?.ItemsSource is not null,
                TimeSpan.FromSeconds(15), "lista okien");
            await Waits.IdleAsync();

            settings.TranslateHotkey = "Ctrl+Shift+T";
            settings.ToggleOverlayHotkey = "Ctrl+Shift+H";
            settings.LiveToggleHotkey = "Ctrl+Shift+L";
            Invoke(window, "UpdateLiveControls");
            Invoke(window, "UpdateHotkeyLabels");

            PreviewData.Fill(window, usage);
            Invoke(window, "OnStatusTimerTick", null, EventArgs.Empty);
            await Task.Delay(500);
            await Waits.IdleAsync();

            report.Environment(window);
            await CheckSettingsAsync(window, settings, report);
            await RenderMainAsync(window, report);
            await CheckMaximizedAsync(window, report);
            await RenderVariantsAsync(window, report);
            await RenderIconsAsync(window, report);
            await RenderTrayMenuAsync(window, report);
            await RenderGlossaryEditorAsync(userGlossary, orchestrator, report);
            await RenderResultPanelAsync(settings, report);
        }
        catch (Exception ex)
        {
            report.Line("BŁĄD: " + ex);
            Console.Error.WriteLine(ex);
            exitCode = 3;
        }
        finally
        {
            window?.Close();
            await Waits.IdleAsync();
            report.Finish();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"Zapisano {report.FileCount} plików do {output}; ucięte napisy: {report.ClippedCount}; zasłonięte kontrolki: {report.BlockedCount}.");
        return exitCode;
    }

    private static async Task RenderMainAsync(MainWindow window, PreviewReport report)
    {
        var defaultSize = new Size(window.Width, window.Height);
        var sizes = new (string Name, Size Size)[]
        {
            ("domyslny", defaultSize),
            ("min", new Size(window.MinWidth, window.MinHeight)),
            ("pelny-ekran", new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height)),
        };
        var pages = NavigationPages(window);
        foreach (var (sizeName, size) in sizes)
        {
            window.Width = size.Width;
            window.Height = size.Height;
            await Waits.IdleAsync();
            var index = 0;
            foreach (var page in pages)
            {
                index++;
                await SelectAsync(page.Button);
                var name = $"{index:00}-{page.Slug}-{sizeName}";
                report.CheckClipping(window, name);
                report.CheckHitTesting(window, name);
                report.Save(Renderer.Window(window, 1.0), name + "-100.png");
                if (sizeName != "pelny-ekran") report.Save(Renderer.Window(window, 1.5), name + "-150.png");
                if (sizeName == "domyslny" && Renderer.PageContent(window) is { } full)
                    report.Save(full, name + "-cala-strona.png");
            }
        }
        window.Width = defaultSize.Width;
        window.Height = defaultSize.Height;
        await SelectAsync(pages[0].Button);
    }

    private static async Task CheckSettingsAsync(MainWindow window, AppSettings settings, PreviewReport report)
    {
        var checks = new (string Control, int Index, Func<string> Read, string Expected)[]
        {
            ("CmbPlacement", 1, () => settings.OverlayPlacement, "cover"),
            ("CmbPlacement", 0, () => settings.OverlayPlacement, "below"),
            ("CmbLiveStyle", 1, () => settings.LiveDisplayMode, "subtitle"),
            ("CmbLiveStyle", 0, () => settings.LiveDisplayMode, "at-source"),
            ("CmbBackground", 2, () => settings.OverlayBackgroundOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture), "0"),
            ("CmbBackground", 1, () => settings.OverlayBackgroundOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture), "0.4"),
            ("CmbBackground", 0, () => settings.OverlayBackgroundOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture), "0.85"),
            ("CmbDisplayMode", 1, () => settings.ResultDisplayMode, "overlay"),
            ("CmbDisplayMode", 0, () => settings.ResultDisplayMode, "panel"),
            ("CmbPlayerGender", 2, () => settings.PlayerGender, "female"),
            ("CmbPlayerGender", 0, () => settings.PlayerGender, "unknown"),
            ("CmbProvider", 0, () => settings.Provider, TranslationProviderCatalog.DeepL.Id),
            ("CmbProvider", 5, () => settings.Provider, TranslationProviderCatalog.Mock.Id),
        };
        var failures = 0;
        foreach (var (control, index, read, expected) in checks)
        {
            if (Find<Selector>(window, control) is not { } selector) continue;
            selector.SelectedIndex = index;
            await Waits.IdleAsync();
            var actual = read();
            var ok = actual == expected;
            if (!ok) failures++;
            report.Line($"Zapis ustawień: {control}.SelectedIndex = {index} → {actual} ({(ok ? "zgodnie" : "oczekiwano " + expected)})");
        }
        if (Find<ToggleButton>(window, "ChkCacheOnly") is { } cacheOnly)
        {
            cacheOnly.IsChecked = true;
            await Waits.IdleAsync();
            var on = settings.CacheOnlyMode;
            cacheOnly.IsChecked = false;
            await Waits.IdleAsync();
            var off = settings.CacheOnlyMode;
            if (!on || off) failures++;
            report.Line($"Zapis ustawień: ChkCacheOnly wł./wył. → {on}/{off}");
        }
        report.Line($"Zapis ustawień: niezgodności {failures}");
        report.Line(string.Empty);
        if (Find<TextBlock>(window, "TxtStatus") is { } status) status.Text = "Przetłumaczono 2 bloki tekstu (517 ms).";
    }

    private static async Task CheckMaximizedAsync(MainWindow window, PreviewReport report)
    {
        if (window.FindName("RootGrid") is not FrameworkElement root) return;
        window.WindowState = WindowState.Maximized;
        await Task.Delay(400);
        await Waits.IdleAsync();
        var topLeft = root.PointToScreen(new Point(0, 0));
        var bottomRight = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        var work = Monitors.WorkArea(window);
        report.Line($"Maksymalizacja: treść {topLeft.X:0},{topLeft.Y:0} – {bottomRight.X:0},{bottomRight.Y:0} px; " +
                    $"obszar roboczy monitora {work.Left},{work.Top} – {work.Right},{work.Bottom} px; " +
                    $"odchyłka L {topLeft.X - work.Left:0.#} G {topLeft.Y - work.Top:0.#} P {work.Right - bottomRight.X:0.#} D {work.Bottom - bottomRight.Y:0.#} px; " +
                    $"margines {root.Margin}");
        report.Save(Renderer.Window(window, 1.0), "maksymalizacja-100.png");
        window.WindowState = WindowState.Normal;
        window.Left = Offscreen.Left;
        window.Top = Offscreen.Top;
        await Task.Delay(200);
        await Waits.IdleAsync();
        report.Line($"Po przywróceniu: margines {root.Margin}");
    }

    private static async Task RenderVariantsAsync(MainWindow window, PreviewReport report)
    {
        if (Find<Selector>(window, "CmbProvider") is { } providerSelector)
        {
            var keyBox = Find<PasswordBox>(window, "PwdApiKey");
            foreach (var provider in new[] { TranslationProviderCatalog.DeepL, TranslationProviderCatalog.Claude, TranslationProviderCatalog.Llm })
            {
                providerSelector.SelectedItem = provider;
                if (keyBox is not null) keyBox.Password = provider == TranslationProviderCatalog.DeepL ? "przykladowy-klucz-podgladu" : string.Empty;
                await SelectPageContainingAsync(window, providerSelector);
                var name = "wariant-dostawca-" + provider.Id.ToLowerInvariant();
                report.CheckClipping(window, name);
                report.Save(Renderer.Window(window, 1.0), name + "-100.png");
                report.Save(Renderer.Window(window, 1.5), name + "-150.png");
                if (Renderer.PageContent(window) is { } full) report.Save(full, name + "-cala-strona.png");
            }
            keyBox?.Clear();
            providerSelector.SelectedItem = TranslationProviderCatalog.Mock;
            await Waits.IdleAsync();
        }

        if (Find<FrameworkElement>(window, "LiveIndicator") is { } indicator)
        {
            await SelectPageContainingAsync(window, indicator);
            var start = Find<UIElement>(window, "BtnStartLive");
            var stop = Find<UIElement>(window, "BtnStopLive");
            var status = Find<TextBlock>(window, "TxtLiveStatus");
            var initialStatus = status?.Text;
            foreach (var (state, label, line) in new[]
            {
                ("Active", "dziala", "Live: obserwuję (zmiana 3%, istotne 1%, mocne 0%, bloki 2)."),
                ("Paused", "wstrzymany", "Live: zmiana widoku — usuwam nieaktualne napisy."),
            })
            {
                if (start is not null) start.IsEnabled = false;
                if (stop is not null) stop.IsEnabled = true;
                if (status is not null) status.Text = line;
                InvokeLiveIndicator(window, state);
                await Waits.IdleAsync();
                var name = "wariant-live-" + label;
                report.CheckClipping(window, name);
                report.Save(Renderer.Window(window, 1.0), name + "-100.png");
                report.Save(Renderer.Window(window, 1.5), name + "-150.png");
            }
            if (start is not null) start.IsEnabled = true;
            if (stop is not null) stop.IsEnabled = false;
            if (status is not null) status.Text = initialStatus ?? string.Empty;
            InvokeLiveIndicator(window, "Idle");
            await Waits.IdleAsync();
        }

        foreach (var comboName in new[] { "CmbFont", "CmbProfile" })
        {
            if (Find<ComboBox>(window, comboName) is not { } combo) continue;
            await SelectPageContainingAsync(window, combo);
            combo.IsDropDownOpen = true;
            await Task.Delay(400);
            await Waits.IdleAsync();
            if (combo.Template?.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement child })
            {
                report.Save(Renderer.Element(child, 1.0, null), $"wariant-lista-{comboName.ToLowerInvariant()}-100.png");
                report.Save(Renderer.Element(child, 1.5, null), $"wariant-lista-{comboName.ToLowerInvariant()}-150.png");
            }
            combo.IsDropDownOpen = false;
            await Waits.IdleAsync();
        }

        if (Find<Selector>(window, "WindowsList") is { } windows && Find<Image>(window, "PreviewImage") is { } preview)
        {
            var selected = windows.SelectedItem;
            var image = preview.Source;
            windows.SelectedItem = null;
            preview.Source = null;
            foreach (var target in new[] { "BtnStartLive", "PreviewImage" })
            {
                if (Find<FrameworkElement>(window, target) is not { } element) continue;
                await SelectPageContainingAsync(window, element);
                var name = target == "BtnStartLive" ? "wariant-bez-wybranej-gry" : "wariant-bez-podgladu";
                report.CheckClipping(window, name);
                report.Save(Renderer.Window(window, 1.0), name + "-100.png");
            }
            windows.SelectedItem = selected;
            preview.Source = image;
            await Waits.IdleAsync();
        }

        if (Find<FrameworkElement>(window, "BtnStartLive") is { } anchor)
        {
            await SelectPageContainingAsync(window, anchor);
            var tip = new ToolTip
            {
                Content = "Krótkie ostrzeżenia nad grą — okno aplikacji jest w trakcie gry zwykle schowane.",
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
            };
            tip.IsOpen = true;
            await Task.Delay(400);
            await Waits.IdleAsync();
            report.Save(Renderer.Element(tip, 1.5, null), "wariant-podpowiedz-150.png");
            tip.IsOpen = false;
            await Waits.IdleAsync();
        }
    }

    private static async Task RenderIconsAsync(MainWindow window, PreviewReport report)
    {
        if (window.Icon is { } icon)
        {
            if (icon is BitmapFrame { Decoder.Frames: { Count: > 1 } frames })
            {
                foreach (var frame in frames) report.Save(frame, $"ikona-okna-{frame.PixelWidth}.png");
            }
            else
            {
                foreach (var size in new[] { 16, 24, 32, 48, 256 })
                    report.Save(Renderer.Image(icon, size), $"ikona-okna-{size}.png");
            }
        }
        if (Field(window, "_trayIcon") is { } tray && tray.GetType().GetProperty("Icon")?.GetValue(tray) is System.Drawing.Icon trayIcon)
        {
            using var bitmap = trayIcon.ToBitmap();
            var fileName = $"ikona-zasobnika-{bitmap.Width}.png";
            bitmap.Save(Path.Combine(report.Folder, fileName), System.Drawing.Imaging.ImageFormat.Png);
            report.Saved(fileName);
        }
        await Waits.IdleAsync();
    }

    private static async Task RenderTrayMenuAsync(MainWindow window, PreviewReport report)
    {
        if (Field(window, "_trayIcon") is not FrameworkElement { ContextMenu: { } menu }) return;
        var work = SystemParameters.WorkArea;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = work.Right - 360;
        menu.VerticalOffset = work.Bottom - 260;
        menu.IsOpen = true;
        await Task.Delay(400);
        await Waits.IdleAsync();
        report.Save(Renderer.Element(menu, 1.0, null), "menu-zasobnika-100.png");
        report.Save(Renderer.Element(menu, 1.5, null), "menu-zasobnika-150.png");
        menu.IsOpen = false;
        await Waits.IdleAsync();
    }

    private static async Task RenderGlossaryEditorAsync(UserGlossaryStore store, TranslationOrchestrator orchestrator, PreviewReport report)
    {
        var editor = new GlossaryEditorWindow(store, orchestrator, "en", "pl")
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = Offscreen.Left,
            Top = Offscreen.Top,
        };
        editor.Show();
        await Waits.UntilAsync(() => editor.IsLoaded, TimeSpan.FromSeconds(10), "słownik");
        await Waits.IdleAsync();
        report.CheckClipping(editor, "slownik");
        report.Save(Renderer.Window(editor, 1.0), "slownik-100.png");
        report.Save(Renderer.Window(editor, 1.5), "slownik-150.png");
        editor.Close();
        await Waits.IdleAsync();
    }

    private static async Task RenderResultPanelAsync(AppSettings settings, PreviewReport report)
    {
        var panel = new ResultPanelWindow();
        panel.ShowResults(PreviewData.PanelResult(), settings, static (_, _) => Task.CompletedTask, static _ => Task.CompletedTask);
        await Task.Delay(400);
        await Waits.IdleAsync();
        report.CheckClipping(panel, "panel-wyniku");
        var backdrop = PreviewData.GameBackdrop();
        report.Save(Renderer.Window(panel, 1.0, backdrop), "panel-wyniku-100.png");
        report.Save(Renderer.Window(panel, 1.5, backdrop), "panel-wyniku-150.png");
        panel.ForceClose();
        await Waits.IdleAsync();
    }

    private sealed record NavPage(string Slug, ToggleButton? Button);

    private static List<NavPage> NavigationPages(Window window)
    {
        var buttons = Renderer.LogicalDescendants<RadioButton>(window)
            .Where(static r => r.GroupName == "Nawigacja")
            .ToList();
        if (buttons.Count == 0) return [new NavPage("okno", null)];
        return buttons.Select(static b => new NavPage(Slug(b.Content as string ?? b.Name), b)).ToList();
    }

    private static async Task SelectAsync(ToggleButton? button)
    {
        if (button is not null) button.IsChecked = true;
        await Waits.IdleAsync();
    }

    private static async Task SelectPageContainingAsync(Window window, FrameworkElement element)
    {
        foreach (var page in NavigationPages(window))
        {
            await SelectAsync(page.Button);
            if (element.IsVisible) return;
        }
    }

    private static string Slug(string text)
    {
        var map = new Dictionary<char, char>
        {
            ['ą'] = 'a', ['ć'] = 'c', ['ę'] = 'e', ['ł'] = 'l', ['ń'] = 'n', ['ó'] = 'o', ['ś'] = 's', ['ź'] = 'z', ['ż'] = 'z',
        };
        var chars = text.ToLowerInvariant().Select(c => map.TryGetValue(c, out var r) ? r : char.IsLetterOrDigit(c) ? c : '-');
        return string.Join('-', new string(chars.ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static T? Find<T>(FrameworkElement scope, string name) where T : class => scope.FindName(name) as T;

    private static object? Field(object target, string name) => target.GetType().GetField(name, AnyInstance)?.GetValue(target);

    private static void Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, AnyInstance)?.Invoke(target, args);

    private static void InvokeLiveIndicator(MainWindow window, string state)
    {
        var method = window.GetType().GetMethod("SetLiveIndicator", AnyInstance);
        if (method?.GetParameters() is not [{ ParameterType: { IsEnum: true } enumType }]) return;
        method.Invoke(window, [Enum.Parse(enumType, state)]);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP jest wyłączone w UiPreview.");
    }

    private sealed class MemorySecrets : ISecretsStore
    {
        private readonly Dictionary<string, string> _values = [];
        public void Save(string name, string value) => _values[name] = value;
        public string? Load(string name) => _values.GetValueOrDefault(name);
        public void Delete(string name) => _values.Remove(name);
    }
}

internal static class Offscreen
{
    public static double Left => SystemParameters.VirtualScreenLeft - 2600;
    public static double Top => SystemParameters.VirtualScreenTop + 40;
}

internal static class Waits
{
    public static async Task IdleAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException($"Nie doczekano: {what}.");
            await Task.Delay(50);
        }
    }
}
