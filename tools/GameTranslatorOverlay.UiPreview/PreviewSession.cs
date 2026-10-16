using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTranslatorOverlay.App;
using GameTranslatorOverlay.App.Hotkeys;
using GameTranslatorOverlay.App.Interop;
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
            TranslateHotkey = PreviewHotkeys.Translate,
            ToggleOverlayHotkey = PreviewHotkeys.ToggleOverlay,
            LiveToggleHotkey = PreviewHotkeys.LiveToggle,
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
            PreviewData.SeedQualityWarnings(orchestrator.QualityWarnings, usage);
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
            await RenderProfileEditorAsync(window, orchestrator, ocr, paths, report, loggerFactory.CreateLogger("UiPreview"));
            await RenderHotkeyEditorAsync(window, settings, paths, report);
            await RenderGlossaryEditorAsync(userGlossary, orchestrator, report);
            await RenderResultPanelAsync(settings, report);
            await RenderQualityWarningVariantsAsync(window, report);
            RenderOverlayMarks(report);
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
                if (sizeName is "domyslny" or "min" && Renderer.PageContent(window) is { } full)
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

    private static async Task RenderTrayMenuAsync(MainWindow window, PreviewReport report, string name = "menu-zasobnika")
    {
        if (Field(window, "_trayIcon") is not FrameworkElement { ContextMenu: { } menu }) return;
        var work = SystemParameters.WorkArea;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = work.Right - 360;
        menu.VerticalOffset = work.Bottom - 260;
        menu.IsOpen = true;
        await Task.Delay(400);
        await Waits.IdleAsync();
        report.Save(Renderer.Element(menu, 1.0, null), name + "-100.png");
        report.Save(Renderer.Element(menu, 1.5, null), name + "-150.png");
        report.Line($"[{name}] pozycje: {string.Join(" | ", menu.Items.OfType<MenuItem>().Select(static i => i.Header))}");
        menu.IsOpen = false;
        await Waits.IdleAsync();
    }

    private static async Task ShotAsync(MainWindow window, PreviewReport report, string name, bool fullPage = false)
    {
        await Waits.IdleAsync();
        report.CheckClipping(window, name);
        report.CheckHitTesting(window, name);
        report.Save(Renderer.Window(window, 1.0), name + "-100.png");
        report.Save(Renderer.Window(window, 1.5), name + "-150.png");
        if (fullPage && Renderer.PageContent(window) is { } full) report.Save(full, name + "-cala-strona.png");
    }

    private static async Task NarrowShotAsync(MainWindow window, PreviewReport report, string name)
    {
        var (width, height) = (window.Width, window.Height);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await ShotAsync(window, report, name, fullPage: true);
        window.Width = width;
        window.Height = height;
        await Waits.IdleAsync();
    }

    private static string Text(FrameworkElement scope, string name) => scope.FindName(name) switch
    {
        TextBlock block => block.Text,
        TextBox box => box.Text,
        _ => "(brak)",
    };

    private static string ProfileFiles(AppPaths paths) => Directory.Exists(paths.ProfilesDirectory)
        ? string.Join(", ", Directory.GetFiles(paths.ProfilesDirectory, "profile.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(paths.RootDirectory, f)))
        : "(brak folderu)";

    private static async Task RenderProfileEditorAsync(
        MainWindow window, TranslationOrchestrator orchestrator, GameTranslatorOverlay.Core.Ocr.IOcrProvider ocr, AppPaths paths, PreviewReport report, ILogger logger)
    {
        if (Find<FrameworkElement>(window, "CardGameProfile") is not { } card
            || Find<Selector>(window, "WindowsList") is not { } list
            || Find<ComboBox>(window, "CmbProfile") is not { } profiles) return;

        await SelectPageContainingAsync(window, card);
        var game = list.SelectedItem;
        var noArgs = new object?[] { window, new RoutedEventArgs() };
        string Selected() => profiles.SelectedItem as string ?? "(brak)";

        await ShotAsync(window, report, "wariant-profil-brak", fullPage: true);

        Invoke(window, "OnCreateProfileClick", noArgs);
        await ShotAsync(window, report, "wariant-profil-blokada-prywatny");
        report.Line($"Profil: tryb prywatny — tworzenie {(Find<Button>(window, "BtnCreateProfile")?.IsEnabled == true ? "DOSTĘPNE (błąd)" : "zablokowane")}, komunikat „{Text(window, "TxtProfileLiveLock")}”");
        Invoke(window, "OnCancelProfileEditClick", noArgs);

        var privateToggle = Find<ToggleButton>(window, "ChkPrivate");
        if (privateToggle is not null) privateToggle.IsChecked = false;
        await Waits.IdleAsync();
        Invoke(window, "OnCreateProfileClick", noArgs);
        await ShotAsync(window, report, "wariant-profil-tworzenie", fullPage: true);
        await NarrowShotAsync(window, report, "wariant-profil-tworzenie-min");
        report.Line($"Profil: formularz — nazwa „{Text(window, "TxtNewProfileName")}”, proces „{Text(window, "TxtNewProfileProcess")}”");

        var bundled = orchestrator.Profiles.FirstOrDefault(static p => p.ProcessNames.Count > 0);
        if (bundled is not null && Find<TextBox>(window, "TxtNewProfileName") is { } nameBox)
        {
            var suggested = nameBox.Text;
            nameBox.Text = bundled.Name;
            Invoke(window, "OnSaveNewProfileClick", noArgs);
            await ShotAsync(window, report, "wariant-profil-blad-nazwy");
            report.Line($"Profil: nazwa profilu z zestawu → „{Text(window, "TxtProfileEditError")}”; pliki: {ProfileFiles(paths)}");
            nameBox.Text = suggested;
        }

        Invoke(window, "OnSaveNewProfileClick", noArgs);
        await Waits.IdleAsync();
        var created = orchestrator.ActiveProfile;
        var createdFile = orchestrator.ProfileFile(created);
        report.Line($"Profil: utworzony {created?.Id ?? "(brak)"} „{created?.Name}”; pliki: {ProfileFiles(paths)}; wybrany na liście: {Selected()}; status: {Text(window, "TxtStatus")}");
        if (createdFile is not null) report.Line("Profil: zawartość pliku: " + File.ReadAllText(createdFile).ReplaceLineEndings(" "));
        await ShotAsync(window, report, "wariant-profil-wlasny", fullPage: true);

        profiles.IsDropDownOpen = true;
        await Task.Delay(400);
        await Waits.IdleAsync();
        if (profiles.Template?.FindName("PART_Popup", profiles) is Popup { Child: FrameworkElement popup })
        {
            report.Save(Renderer.Element(popup, 1.0, null), "wariant-lista-cmbprofile-z-wlasnym-100.png");
        }
        profiles.IsDropDownOpen = false;
        await SelectPageContainingAsync(window, card);

        Invoke(window, "OnRenameProfileClick", noArgs);
        if (Find<TextBox>(window, "TxtRenameProfile") is { } renameBox) renameBox.Text = "Mroczna Latarnia — mój profil";
        await ShotAsync(window, report, "wariant-profil-zmiana-nazwy");
        Invoke(window, "OnSaveRenameClick", noArgs);
        await Waits.IdleAsync();
        report.Line($"Profil: po zmianie nazwy aktywny „{orchestrator.ActiveProfile?.Name}”, wybrany na liście: {Selected()}; status: {Text(window, "TxtStatus")}");
        await ShotAsync(window, report, "wariant-profil-po-zmianie-nazwy");

        var session = new LiveTranslationSession(orchestrator, ocr, IntPtr.Zero, new LiveSessionOptions(), static _ => { }, logger);
        SetField(window, "_liveSession", session);
        Invoke(window, "UpdateLiveControls");
        InvokeLiveIndicator(window, "Active");
        await ShotAsync(window, report, "wariant-profil-live-blokada");
        report.Line($"Profil: przy działającym live przyciski aktywne: zmień nazwę {Find<UIElement>(window, "BtnRenameProfile")?.IsEnabled}, usuń {Find<UIElement>(window, "BtnDeleteProfile")?.IsEnabled}");
        SetField(window, "_liveSession", null);
        session.Dispose();
        Invoke(window, "UpdateLiveControls");
        InvokeLiveIndicator(window, "Idle");

        if (orchestrator.ActiveProfile is { } own && orchestrator.ProfileFile(own) is { } ownFile)
        {
            Invoke(window, "DeleteUserProfile", own, ownFile);
        }
        await Waits.IdleAsync();
        report.Line($"Profil: po usunięciu pliki: {ProfileFiles(paths)}; aktywny: {orchestrator.ActiveProfile?.Id ?? "(brak)"}; wybrany na liście: {Selected()}; profili: {orchestrator.Profiles.Count}; status: {Text(window, "TxtStatus")}");
        await ShotAsync(window, report, "wariant-profil-po-usunieciu");

        if (bundled is not null && list.ItemsSource is IEnumerable<TargetWindow> windows)
        {
            var original = list.ItemsSource;
            var withBundled = windows.ToList();
            var bundledWindow = new TargetWindow(IntPtr.Zero, "Okno gry z profilem z zestawu", bundled.ProcessNames[0], 0) { Area = 1600L * 900 };
            withBundled.Insert(1, bundledWindow);
            list.ItemsSource = withBundled;
            list.SelectedItem = bundledWindow;
            await ShotAsync(window, report, "wariant-profil-z-zestawu");
            report.Line($"Profil: okno procesu z zestawu → wybrany na liście: {Selected()}; usuń widoczne: {Find<UIElement>(window, "BtnDeleteProfile")?.IsVisible}");
            list.ItemsSource = original;
            list.SelectedItem = game;
            await Waits.IdleAsync();
        }
        if (privateToggle is not null) privateToggle.IsChecked = true;
        await Waits.IdleAsync();
        report.Line(string.Empty);
    }

    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("Kontrolka nie ma okna.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
    }

    private static string SavedHotkey(AppPaths paths, string property)
    {
        if (!File.Exists(paths.SettingsPath)) return "(brak pliku)";
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.SettingsPath));
        return json.RootElement.TryGetProperty(property, out var value) ? value.GetString() ?? "(null)" : "(brak pola)";
    }

    private static async Task RenderHotkeyEditorAsync(MainWindow window, AppSettings settings, AppPaths paths, PreviewReport report)
    {
        if (Find<ToggleButton>(window, "BtnHotkeyLive") is not { } live
            || Find<ToggleButton>(window, "BtnHotkeyTranslate") is not { } translate
            || Find<ToggleButton>(window, "BtnHotkeyOverlay") is not { } overlay) return;

        settings.TranslateHotkey = PreviewHotkeys.Translate;
        settings.ToggleOverlayHotkey = PreviewHotkeys.ToggleOverlay;
        settings.LiveToggleHotkey = PreviewHotkeys.LiveToggle;
        Invoke(window, "UpdateHotkeyLabels");
        Invoke(window, "UpdateLiveControls");
        await SelectPageContainingAsync(window, live);

        live.IsChecked = true;
        await ShotAsync(window, report, "wariant-skroty-wcisnij-skrot", fullPage: true);
        await NarrowShotAsync(window, report, "wariant-skroty-wcisnij-skrot-min");
        report.Line($"Skróty: edycja live — pole „{Text(window, "TxtHotkeyLive")}”, podpowiedź „{Text(window, "TxtHotkeyLiveNote")}”");

        Press(live, Key.T);
        await ShotAsync(window, report, "wariant-skroty-bledny-klawisz");
        report.Line($"Skróty: goła litera T → „{Text(window, "TxtHotkeyLiveNote")}”, edycja trwa: {live.IsChecked}");

        Press(live, Key.F20);
        await Waits.IdleAsync();
        report.Line($"Skróty: live → F20: ustawienia {settings.LiveToggleHotkey}, settings.json {SavedHotkey(paths, "liveToggleHotkey")}, " +
                    $"zarejestrowany {Field(window, "_liveHotkeyRegistered")}, karta w nawigacji „{Text(window, "TxtNavLiveHotkey")}”, " +
                    $"pod Start live „{Text(window, "TxtLiveHotkeyHint")}”, status „{Text(window, "TxtStatus")}”");
        await ShotAsync(window, report, "wariant-skroty-zmieniony");

        translate.IsChecked = true;
        Press(translate, Key.F20);
        await ShotAsync(window, report, "wariant-skroty-duplikat");
        report.Line($"Skróty: F20 dla tłumaczenia regionu → „{Text(window, "TxtHotkeyTranslateNote")}”");
        Press(translate, Key.Escape);
        await Waits.IdleAsync();
        report.Line($"Skróty: Esc → edycja trwa: {translate.IsChecked}, skrót {settings.TranslateHotkey}, status „{Text(window, "TxtStatus")}”");

        var blocker = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = Offscreen.Left, Top = Offscreen.Top,
        };
        blocker.Show();
        var other = new HotkeyManager();
        other.Attach(blocker);
        var blocked = other.TryRegister("F19", static () => { }, out _);
        overlay.IsChecked = true;
        Press(overlay, Key.F19);
        await ShotAsync(window, report, "wariant-skroty-zajety");
        report.Line($"Skróty: F19 zajęty przez inne okno ({blocked}) → nakładka {settings.ToggleOverlayHotkey}, " +
                    $"zarejestrowany {Field(window, "_overlayHotkeyRegistered")}, komunikat „{Text(window, "TxtHotkeyOverlayNote")}”");
        other.Dispose();
        blocker.Close();

        if (Find<FrameworkElement>(window, "BtnTranslateRegion") is { } tile)
        {
            await SelectPageContainingAsync(window, tile);
            await ShotAsync(window, report, "wariant-skroty-pulpit");
            report.Line($"Skróty: kafelki — tłumaczenie {Visuals.GetCaption(tile)}, nakładka {(Find<FrameworkElement>(window, "BtnToggleOverlay") is { } t ? Visuals.GetCaption(t) : "?")}");
        }
        report.Line($"Skróty: Dane i słownik — „{Text(window, "TxtTimingHint")}”; Wygląd — „{Text(window, "TxtHotkeyResult")}”");
        await RenderTrayMenuAsync(window, report, "menu-zasobnika-po-zmianie-skrotu");
        report.Line(string.Empty);
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

    private static async Task RenderQualityWarningVariantsAsync(MainWindow window, PreviewReport report)
    {
        if (Find<FrameworkElement>(window, "CardQualityWarnings") is not { } card) return;
        if (Field(window, "_qualityWarnings") is System.Collections.ObjectModel.ObservableCollection<QualityWarningItem> { Count: > 0 } items)
        {
            var item = items[0];
            item.EditText = item.TranslatedText;
            item.IsEditing = true;
            await SelectPageContainingAsync(window, card);
            card.BringIntoView();
            await Task.Delay(200);
            await Waits.IdleAsync();
            const string editing = "wariant-ostrzezenia-edycja";
            report.CheckClipping(window, editing);
            report.CheckHitTesting(window, editing);
            report.Save(Renderer.Window(window, 1.0), editing + "-100.png");
            report.Save(Renderer.Window(window, 1.5), editing + "-150.png");
            item.IsEditing = false;
            await Waits.IdleAsync();
        }

        Invoke(window, "OnClearQualityWarningsClick", window, new RoutedEventArgs());
        await SelectPageContainingAsync(window, card);
        card.BringIntoView();
        await Task.Delay(200);
        await Waits.IdleAsync();
        const string empty = "wariant-ostrzezenia-puste";
        report.CheckClipping(window, empty);
        report.CheckHitTesting(window, empty);
        report.Save(Renderer.Window(window, 1.0), empty + "-100.png");
        if (Renderer.PageContent(window) is { } page) report.Save(page, empty + "-cala-strona.png");
    }

    private static void RenderOverlayMarks(PreviewReport report)
    {
        const double scale = 1.5;
        var size = new Size(960, 540);
        var dark = new AppSettings { OverlayPlacement = "below", OverlayBackgroundOpacity = 0.85 };
        var soft = new AppSettings { OverlayPlacement = "below", OverlayBackgroundOpacity = 0.40 };
        var bare = new AppSettings { OverlayPlacement = "below", OverlayBackgroundOpacity = 0 };
        var cover = new AppSettings { OverlayPlacement = "cover", OverlayBackgroundOpacity = 0.85 };

        var canvas = new Canvas { Width = size.Width, Height = size.Height };
        VisualTreeHelper.SetRootDpi(canvas, new DpiScale(scale, scale));
        canvas.Children.Add(new Image { Source = PreviewData.GameBackdrop(), Width = size.Width, Height = size.Height });
        var samples = new List<(string Name, Border Element, bool Visible)>();
        void Add(string name, Border element, double left, double top, bool visible)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            canvas.Children.Add(element);
            samples.Add((name, element, visible));
        }
        Add("dymek ciemny", OverlayBlockRenderer.CreateBlockElement("Latarnik jest ci winien złoto.", dark, scale, 33, 0xF2EEE3),
            40, 40, dark.ShowsQualityMark(warning: true));
        Add("dymek delikatny", OverlayBlockRenderer.CreateBlockElement("Zapal latarnię przed 22:30.", soft, scale, 33, 0xF2EEE3),
            40, 110, soft.ShowsQualityMark(warning: true));
        Add("sam tekst", OverlayBlockRenderer.CreateBlockElement("Porozmawiaj z latarnikiem.", bare, scale, 33, 0xF2EEE3),
            40, 180, bare.ShowsQualityMark(warning: true));
        Add("na oryginale (zakrywa)", OverlayBlockRenderer.CreateBlockElement("Zbadaj latarnię", cover, scale, 33, 0xF2EEE3),
            560, 40, cover.ShowsQualityMark(warning: true));
        var subtitle = OverlayBlockRenderer.CreateSubtitleElement(cover);
        ((TextBlock)subtitle.Child).Text = "Latarnik jest ci winien złoto za zapieczętowane listy.";
        Add("pasek napisów (przy zakrywaniu też)", subtitle, 200, 440, cover.ShowsQualityMark(warning: true, subtitleBar: true));

        Layout(canvas, size);
        var off = RenderCanvas(canvas, size, scale);
        foreach (var (_, element, visible) in samples) OverlayBlockRenderer.SetQualityMark(element, visible);
        Layout(canvas, size);
        var on = RenderCanvas(canvas, size, scale);
        report.Save(off, "nakladka-bez-znacznika-150.png");
        report.Save(on, "nakladka-znacznik-150.png");

        var width = off.PixelWidth;
        var height = off.PixelHeight;
        var before = new int[width * height];
        var after = new int[width * height];
        off.CopyPixels(before, width * 4, 0);
        on.CopyPixels(after, width * 4, 0);
        var corners = samples.Select(s =>
        {
            var bounds = OverlayBubble.MarkBounds(s.Element.RenderSize, s.Element is OverlayBubble { IsBare: true });
            bounds.Offset(Canvas.GetLeft(s.Element), Canvas.GetTop(s.Element));
            var x = (int)Math.Floor(bounds.Left * scale) - 1;
            var y = (int)Math.Floor(bounds.Top * scale) - 1;
            return new Int32Rect(x, y, (int)Math.Ceiling(bounds.Right * scale) + 1 - x, (int)Math.Ceiling(bounds.Bottom * scale) + 1 - y);
        }).ToList();
        var changedIn = new int[samples.Count];
        var outside = 0;
        var mask = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (before[i] == after[i]) continue;
                mask[i] = unchecked((int)0xFFFBBF24);
                var owner = corners.FindIndex(c => x >= c.X && x < c.X + c.Width && y >= c.Y && y < c.Y + c.Height);
                if (owner >= 0) changedIn[owner]++;
                else outside++;
            }
        }
        var diff = BitmapSource.Create(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, mask, width * 4);
        diff.Freeze();
        report.Save(diff, "nakladka-znacznik-roznica-150.png");
        report.Line($"Znacznik ostrzeżeń w nakładce (skala {scale:0.0}): zmienione piksele poza obszarami znaczników: {outside}");
        for (var k = 0; k < samples.Count; k++)
            report.Line($"  {samples[k].Name}: znacznik {(samples[k].Visible ? "tak" : "nie")}, zmienione piksele w obszarze znacznika {changedIn[k]} " +
                        $"(obszar {corners[k].Width}×{corners[k].Height} px, element {samples[k].Element.ActualWidth * scale:0}×{samples[k].Element.ActualHeight * scale:0} px)");
        report.Line(string.Empty);
    }

    private static void Layout(FrameworkElement root, Size size)
    {
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
    }

    private static BitmapSource RenderCanvas(FrameworkElement root, Size size, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
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
        var switching = button is { IsChecked: not true };
        if (button is not null) button.IsChecked = true;
        await Waits.IdleAsync();
        if (switching) await Task.Delay(250);
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

    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, AnyInstance)?.SetValue(target, value);

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

internal static class PreviewHotkeys
{
    public const string Translate = "Ctrl+Alt+Shift+F21";
    public const string ToggleOverlay = "Ctrl+Alt+Shift+F22";
    public const string LiveToggle = "Ctrl+Alt+Shift+F23";
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
