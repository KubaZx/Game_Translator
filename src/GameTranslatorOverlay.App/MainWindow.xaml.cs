using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Hotkeys;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameTranslatorOverlay.App;

public partial class MainWindow : Window
{
    private const string AutoFontLabel = "Jak w grze (krój z profilu)";

    private const string NoProfileLabel = "— brak profilu (tryb uniwersalny) —";

    private readonly TranslationOrchestrator _orchestrator;
    private readonly AppSettings _settings;
    private readonly JsonSettingsStore _settingsStore;
    private readonly ISecretsStore _secrets;
    private readonly UsageTracker _usage;
    private readonly IOcrProvider _ocr;
    private readonly SqliteTranslationCache _persistentCache;
    private readonly HotkeyManager _hotkeys;
    private readonly UserGlossaryStore _userGlossaryStore;
    private readonly AppPaths _paths;
    private readonly ILogger<MainWindow> _logger;
    private LiveTranslationSession? _liveSession;
    private IntPtr _liveWindowHandle;

    // Teksty pokazanych komunikatów nakładki — pętla live odrzuca ich odczyty OCR
    // (filtr anty-sprzężeniowy, gdy wykluczenie nakładki z przechwytywania zawiedzie).
    private readonly OverlayNoticeEcho _noticeEcho = new();

    private readonly OverlayWindow _overlay = new();
    private readonly ResultPanelWindow _panel = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private IntPtr _trayIconHandle;

    private System.Drawing.Bitmap? _previewBitmap;
    private bool _previewBusy;
    private bool _loadingUi;
    private bool _selectingRegion;
    private int _statusTicks;

    private MenuItem? _trayLiveItem;
    private bool _liveHotkeyRegistered;
    private bool _liveHotkeyBusy;

    // Programowe zaznaczenie zapamiętanej gry po odświeżeniu listy NIE jest wyborem użytkownika:
    // nie może przełączać profilu (zapis settings.json + przebudowa pipeline'u bez kliknięcia).
    private bool _preselectingRememberedGame;

    // Gra zapamiętana w trybie prywatnym — tylko w pamięci, nigdy w settings.json.
    private string? _privateGameProcess;
    private string? _privateGameTitle;

    public MainWindow(
        TranslationOrchestrator orchestrator,
        AppSettings settings,
        JsonSettingsStore settingsStore,
        ISecretsStore secrets,
        UsageTracker usage,
        IOcrProvider ocr,
        SqliteTranslationCache persistentCache,
        HotkeyManager hotkeys,
        UserGlossaryStore userGlossaryStore,
        AppPaths paths,
        ILogger<MainWindow> logger)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _settingsStore = settingsStore;
        _secrets = secrets;
        _usage = usage;
        _ocr = ocr;
        _persistentCache = persistentCache;
        _hotkeys = hotkeys;
        _userGlossaryStore = userGlossaryStore;
        _paths = paths;
        _logger = logger;

        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosedHandler;
        _statusTimer.Tick += OnStatusTimerTick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Główne okno pokazuje ostatnio rozpoznany tekst i tłumaczenia — nie może
        // trafiać do przechwytywanego obrazu (fallback ekranowy czytałby własne wyniki).
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loadingUi = true;

        CmbSourceLang.ItemsSource = new[] { "en" };
        CmbSourceLang.SelectedItem = _settings.SourceLanguage;
        if (CmbSourceLang.SelectedItem is null) CmbSourceLang.SelectedIndex = 0;

        CmbTargetLang.ItemsSource = new[] { "pl" };
        CmbTargetLang.SelectedItem = _settings.TargetLanguage;
        if (CmbTargetLang.SelectedItem is null) CmbTargetLang.SelectedIndex = 0;

        CmbProvider.DisplayMemberPath = nameof(TranslationProviderInfo.DisplayName);
        CmbProvider.ItemsSource = TranslationProviderCatalog.All;
        CmbProvider.SelectedItem = TranslationProviderCatalog.Resolve(_settings.Provider);

        TxtAzureRegion.Text = _settings.AzureRegion ?? string.Empty;
        TxtLlmEndpoint.Text = _settings.LlmEndpoint;
        TxtLlmModel.Text = _settings.LlmModel ?? string.Empty;
        CmbClaudeModel.ItemsSource = TranslationProviderCatalog.SuggestedClaudeModels;
        CmbClaudeModel.Text = _settings.ClaudeModel;
        foreach (var llmPreset in TranslationProviderCatalog.LlmPresets)
        {
            var preset = new Button { Content = llmPreset.Name, Tag = llmPreset, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
            preset.Click += OnLlmPresetClick;
            PnlLlmPresets.Children.Add(preset);
        }

        var profileNames = new List<string> { NoProfileLabel };
        profileNames.AddRange(_orchestrator.Profiles.Select(static p => p.Name));
        CmbProfile.ItemsSource = profileNames;
        var activeProfileName = _orchestrator.ActiveProfile?.Name;
        CmbProfile.SelectedItem = activeProfileName ?? NoProfileLabel;

        CmbDisplayMode.ItemsSource = new[] { "Panel obok regionu", "Nakładka na ekranie" };
        CmbDisplayMode.SelectedIndex = _settings.ResultDisplayMode == "overlay" ? 1 : 0;

        CmbLiveStyle.ItemsSource = new[] { "Przy oryginale", "Napisy na dole" };
        CmbLiveStyle.SelectedIndex = _settings.LiveDisplayMode == "subtitle" ? 1 : 0;

        CmbPlacement.ItemsSource = new[] { "Pod oryginałem", "Na oryginale (zakrywa)" };
        CmbPlacement.SelectedIndex = _settings.OverlayPlacement == "cover" ? 1 : 0;

        CmbBackground.ItemsSource = new[] { "Ciemne", "Delikatne", "Brak (sam tekst)" };
        CmbBackground.SelectedIndex = _settings.OverlayBackgroundOpacity switch
        {
            < 0.05 => 2,
            < 0.7 => 1,
            _ => 0,
        };

        CmbFont.ItemsSource = new[] { AutoFontLabel, "Segoe UI", "Georgia", "Palatino Linotype", "Cambria", "Book Antiqua", "Times New Roman" };
        CmbFont.SelectedItem = Ui.OverlayFonts.IsAuto(_settings.OverlayFontFamily) ? AutoFontLabel : _settings.OverlayFontFamily;
        if (CmbFont.SelectedItem is null) CmbFont.SelectedIndex = 0;

        // Kolejność pozycji = PlayerGender (Unknown, Male, Female) — indeks to wartość wyliczenia.
        CmbPlayerGender.ItemsSource = new[] { "nieznana", "mężczyzna", "kobieta" };
        CmbPlayerGender.SelectedIndex = (int)PlayerGenders.Parse(_settings.PlayerGender);

        TxtFontSize.Text = _settings.OverlayFontSize.ToString(CultureInfo.InvariantCulture);
        ChkCacheOnly.IsChecked = _settings.CacheOnlyMode;
        ChkPrivate.IsChecked = _settings.PrivateMode;
        ChkOverlayNotices.IsChecked = _settings.ShowOverlayNotices;

        _loadingUi = false;

        UpdateOcrStatus();
        UpdateProviderPanel();
        _ = RefreshWindowsAsync();

        _hotkeys.Attach(this);
        if (!_hotkeys.TryRegister(_settings.TranslateHotkey, () => _ = TranslateRegionInteractiveAsync(), out var hotkeyError))
        {
            SetStatus(hotkeyError);
        }
        if (!_hotkeys.TryRegister(_settings.ToggleOverlayHotkey, () => _overlay.ToggleVisibility(), out var overlayHotkeyError))
        {
            SetStatus(overlayHotkeyError);
        }
        _liveHotkeyRegistered = _hotkeys.TryRegister(
            _settings.LiveToggleHotkey, () => _ = ToggleLiveFromHotkeyAsync(), out var liveHotkeyError);
        if (!_liveHotkeyRegistered)
        {
            SetStatus(liveHotkeyError);
        }

        foreach (var warning in _orchestrator.ContentWarnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        InitializeTrayIcon();
        UpdateLiveControls();
        _statusTimer.Start();

        // Obowiązkowe zastrzeżenie (SECURITY.md): raz przy pierwszym uruchomieniu,
        // a stale dostępne pod przyciskiem „Zastrzeżenie”.
        if (!_settings.DisclaimerAcknowledged)
        {
            ShowDisclaimer();
            _settings.DisclaimerAcknowledged = true;
            _settingsStore.Save(_settings);
        }
        MarkSettingsApplied();
    }

    /// <summary>
    /// Migawka ustawień ostatnio zapisanych i zastosowanych w pipeline. Porównanie z nią
    /// (a nie ze stanem w pamięci) sprawia, że nieudany zapis zostanie ponowiony przy
    /// kolejnej zmianie, zamiast zostać uznany za „bez zmian”.
    /// </summary>
    private string? _appliedSettingsJson;

    /// <summary>Część migawki istotna dla pipeline'u (bez wyglądu nakładki i skrótów).</summary>
    private string? _appliedPipelineJson;

    private string SettingsSnapshot() => System.Text.Json.JsonSerializer.Serialize(_settings);

    private void MarkSettingsApplied()
    {
        _appliedSettingsJson = SettingsSnapshot();
        _appliedPipelineJson = _settings.PipelineSnapshot();
    }

    private const string DisclaimerText =
        "Zastrzeżenie: GameTranslatorOverlay jest zewnętrzną nakładką tłumaczącą tekst widoczny " +
        "na ekranie. Program w żaden sposób nie modyfikuje gry — nie ingeruje w jej proces, pamięć, " +
        "pliki ani ruch sieciowy i nie automatyzuje rozgrywki. Mimo to nie gwarantujemy zgodności " +
        "z regulaminem każdej gry — zasady poszczególnych gier i ich systemów anty-cheat różnią się " +
        "i mogą się zmieniać. Przed użyciem sprawdź regulamin gry, w której chcesz korzystać " +
        "z nakładki. Używasz programu na własną odpowiedzialność. Projekt nie jest powiązany " +
        "z twórcami ani wydawcami żadnej gry.";

    private void ShowDisclaimer() =>
        MessageBox.Show(this, DisclaimerText, "GameTranslatorOverlay — zastrzeżenie",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnDisclaimerClick(object sender, RoutedEventArgs e) => ShowDisclaimer();

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateMenuItem("Pokaż okno", RestoreFromTray));
            _trayLiveItem = CreateMenuItem(LiveMenuHeader(), () => _ = ToggleLiveFromHotkeyAsync());
            menu.Items.Add(_trayLiveItem);
            menu.Items.Add(CreateMenuItem("Przetłumacz region  (Ctrl+Shift+T)", () => _ = TranslateRegionInteractiveAsync()));
            menu.Items.Add(CreateMenuItem("Ukryj / pokaż nakładkę  (Ctrl+Shift+H)", _overlay.ToggleVisibility));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Zakończ", Close));

            var trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "GameTranslatorOverlay",
                Icon = CreateTrayIconImage(),
                ContextMenu = menu,
            };
            trayIcon.TrayLeftMouseUp += (_, _) => RestoreFromTray();

            // H.NotifyIcon 2.x NIE rejestruje ikony automatycznie przy tworzeniu z kodu —
            // bez ForceCreate ikona nigdy nie pojawia się w zasobniku.
            trayIcon.ForceCreate();

            // Pole przypisujemy dopiero po udanej rejestracji: minimalizacja chowa okno
            // do zasobnika tylko wtedy, gdy ikona naprawdę istnieje.
            _trayIcon = trayIcon;
        }
        catch (Exception ex)
        {
            // Brak ikony w zasobniku nie może blokować aplikacji.
            _logger.LogWarning(ex, "Nie udało się utworzyć ikony zasobnika");
        }
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private System.Drawing.Icon CreateTrayIconImage()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 136, 229));
            graphics.FillEllipse(background, 1, 1, 30, 30);
            using var font = new System.Drawing.Font("Segoe UI", 14, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            graphics.DrawString("GT", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 1, 32, 30), format);
        }

        // Icon.FromHandle nie przejmuje uchwytu — trzymamy go i niszczymy przy zamknięciu.
        _trayIconHandle = bitmap.GetHicon();
        return System.Drawing.Icon.FromHandle(_trayIconHandle);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _trayIcon is not null)
        {
            // Minimalizacja chowa okno do zasobnika — tłumacz dalej działa w tle.
            Hide();
        }
    }

    private async Task RefreshWindowsAsync()
    {
        var windows = await Task.Run(WindowEnumerator.GetOpenWindows);
        WindowsList.ItemsSource = windows;
        var liveHotkey = _liveHotkeyRegistered ? _settings.LiveToggleHotkey : null;
        SetStatus(LiveHotkeyMessages.WindowListRefreshed(windows.Count, rememberedGameSelected: false, liveHotkey));

        // Zapamiętana gra jest tylko zaznaczana — start live zostaje decyzją użytkownika.
        // Bez autodetekcji profilu: świadomy wybór „brak profilu” ma przetrwać restart
        // i Odśwież; profil dobierze się dopiero przy kliknięciu okna albo tuż przed startem skrótem.
        if (FindRememberedGame(windows) is { } remembered)
        {
            _preselectingRememberedGame = true;
            try
            {
                WindowsList.SelectedItem = remembered;
            }
            finally
            {
                _preselectingRememberedGame = false;
            }
            WindowsList.ScrollIntoView(remembered);
            SetStatus(LiveHotkeyMessages.WindowListRefreshed(windows.Count, rememberedGameSelected: true, liveHotkey));
        }

        // Diagnostyka dev: GTO_AUTOLIVE="fragment tytułu" od razu startuje tryb live na
        // wskazanym oknie — bez klikania, żeby dało się zautomatyzować zrzuty nakładki.
        var autoLive = Environment.GetEnvironmentVariable("GTO_AUTOLIVE");
        if (!string.IsNullOrWhiteSpace(autoLive) && _liveSession is null)
        {
            var target = windows.FirstOrDefault(w => w.Title.Contains(autoLive, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                WindowsList.SelectedItem = target;
                OnStartLiveClick(this, new RoutedEventArgs());
            }
        }
    }

    /// <summary>Pokazuje komunikat w nakładce, o ile gracz ich nie wyłączył.</summary>
    private void ShowOverlayNotice(OverlayNotice? notice, RectPx anchor)
    {
        if (notice is null || !_settings.ShowOverlayNotices) return;
        // Najpierw filtr, potem ekran: następna klatka OCR może już widzieć komunikat.
        _noticeEcho.Remember(notice.Text);
        _overlay.ShowNotice(notice, anchor);
    }

    // Zminimalizowane okno ma granice w okolicy (-32000,-32000) — taki punkt zaczepienia
    // przyklejałby komunikat do rogu przypadkowego monitora. Pusty prostokąt = ostatni znany
    // punkt zaczepienia albo monitor z kursorem.
    private RectPx LiveWindowBounds() =>
        _liveWindowHandle != IntPtr.Zero && NativeMethods.IsWindow(_liveWindowHandle)
            && !NativeMethods.IsIconic(_liveWindowHandle)
            ? ScreenCapture.GetWindowBounds(_liveWindowHandle)
            : default;

    /// <summary>
    /// Włączenie/wyłączenie komunikatów nie zmienia tłumaczenia — zapis bez przebudowy
    /// pipeline'u, żeby nie anulować tłumaczeń w locie.
    /// </summary>
    private void OnOverlayNoticesChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.ShowOverlayNotices = ChkOverlayNotices.IsChecked == true;
        SaveSettingsWithoutRebuild();
        SetStatus(_settings.ShowOverlayNotices
            ? "Komunikaty w nakładce włączone."
            : "Komunikaty w nakładce wyłączone — błędy widać tylko w tym oknie.");
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshWindowsAsync();

    private void SetPreviewBusy(bool busy)
    {
        _previewBusy = busy;
        BtnCapture.IsEnabled = !busy;
        BtnOcrTest.IsEnabled = !busy;
    }

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_previewBusy) return;
        if (WindowsList.SelectedItem is not TargetWindow window)
        {
            SetStatus("Najpierw wybierz okno z listy po lewej.");
            return;
        }

        SetPreviewBusy(true);
        try
        {
            var bitmap = await Task.Run(() => ScreenCapture.CaptureWindow(window.Handle));
            if (bitmap is null)
            {
                SetStatus("Nie udało się przechwycić okna — sprawdź, czy nie jest zminimalizowane.");
                return;
            }

            _previewBitmap?.Dispose();
            _previewBitmap = bitmap;
            PreviewImage.Source = ScreenCapture.ToBitmapSource(bitmap);
            SetStatus($"Przechwycono „{window.Title}” ({bitmap.Width}×{bitmap.Height} px). Możesz teraz przetestować OCR.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd przechwytywania okna");
            SetStatus("Błąd przechwytywania okna — szczegóły w logu diagnostycznym.");
        }
        finally
        {
            SetPreviewBusy(false);
        }
    }

    private async void OnOcrTestClick(object sender, RoutedEventArgs e)
    {
        if (_previewBusy) return;

        // Lokalna migawka referencji — pole może zostać podmienione/zwolnione przez UI,
        // a bitmapa GDI+ nie jest bezpieczna wątkowo.
        var bitmap = _previewBitmap;
        if (bitmap is null)
        {
            SetStatus("Najpierw przechwyć podgląd okna (📷).");
            return;
        }

        SetPreviewBusy(true);
        try
        {
            var sourceLanguage = _settings.SourceLanguage;
            var result = await Task.Run(async () =>
            {
                var downscale = OcrScaling.ComputeDownscale(bitmap.Width, bitmap.Height, _ocr.MaxImageDimension);
                var working = downscale < 1.0 ? ScreenCapture.Rescale(bitmap, downscale) : bitmap;
                try
                {
                    return await _ocr.RecognizeAsync(ScreenCapture.ToOcrBitmap(working), sourceLanguage);
                }
                finally
                {
                    if (!ReferenceEquals(working, bitmap)) working.Dispose();
                }
            });

            TxtLastOcr.Text = string.Join('\n', result.Lines.Select(static l => l.Text));
            SetStatus(result.HasText
                ? $"OCR rozpoznał {result.Lines.Count} linii tekstu (język: {result.LanguageTag})."
                : "OCR nie znalazł tekstu w podglądzie.");
        }
        catch (OcrLanguageNotAvailableException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd testu OCR");
            SetStatus("Błąd OCR — szczegóły w logu diagnostycznym.");
        }
        finally
        {
            SetPreviewBusy(false);
        }
    }

    private void OnTranslateRegionClick(object sender, RoutedEventArgs e) => _ = TranslateRegionInteractiveAsync();

    private async Task TranslateRegionInteractiveAsync()
    {
        if (_selectingRegion)
        {
            if (RegionSelectWindow.IsOpen)
            {
                // Ponowny skrót przy otwartym zaznaczaniu działa jak Esc — zamyka selektor
                // (wcześniej był po cichu ignorowany). Kolejne naciśnięcie otwiera go od nowa.
                RegionSelectWindow.CloseActive();
                return;
            }

            // Ponowny skrót w trakcie wiszącego tłumaczenia = prawdziwe latest-wins:
            // anulujemy starą operację zamiast po cichu ignorować użytkownika.
            _orchestrator.CancelActiveOperation();
            return;
        }
        _selectingRegion = true;
        RectPx? manualRegion = null;
        try
        {
            // Połączenie z dostawcą zestawia się, gdy użytkownik zaznacza region — pierwsze
            // tłumaczenie nie czeka potem na DNS i TLS.
            _orchestrator.WarmUpActiveProvider();
            var region = await RegionSelectWindow.SelectAsync();
            if (region is not { } selected)
            {
                SetStatus("Zaznaczanie anulowane.");
                return;
            }
            manualRegion = selected;

            // Chowamy własne okna, żeby nie przechwycić starego tłumaczenia.
            _overlay.Hide();
            _panel.Hide();
            await Task.Delay(90);

            SetStatus("Tłumaczę zaznaczony region…");
            var result = await _orchestrator.TranslateRegionAsync(selected);
            DisplayResult(result);
        }
        catch (OperationCanceledException)
        {
            // Nowsze żądanie albo zmiana ustawień przerwały potok (latest-wins).
            SetStatus("Tłumaczenie przerwane (nowe żądanie albo zmiana ustawień) — spróbuj ponownie.");
        }
        catch (OcrLanguageNotAvailableException ex)
        {
            SetStatus(ex.Message);
            ShowManualFailureNotice(manualRegion);
        }
        catch (CacheStorageException ex)
        {
            SetStatus(ex.Message);
            ShowManualFailureNotice(manualRegion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd tłumaczenia regionu");
            SetStatus("Nieoczekiwany błąd tłumaczenia — szczegóły w logu diagnostycznym.");
            ShowManualFailureNotice(manualRegion);
        }
        finally
        {
            _selectingRegion = false;
        }
    }

    /// <summary>W trybie nakładki błąd ręcznego tłumaczenia nie może skończyć się ciszą.</summary>
    private void ShowManualFailureNotice(RectPx? region)
    {
        if (region is { } selected && _settings.ResultDisplayMode == "overlay")
            ShowOverlayNotice(OverlayNotices.ManualTranslationFailed(), selected);
    }

    private void DisplayResult(RegionTranslationResult result)
    {
        TxtLastOcr.Text = string.Join("\n\n", result.Blocks.Select(static b => b.Block.Text));
        TxtLastTranslation.Text = string.Join("\n\n", result.Blocks.Select(static b =>
            b.Outcome.TranslatedText ?? $"⚠ {b.Outcome.ErrorMessage}"));
        TxtTiming.Text = "Czasy: " + result.Timings;

        // Gracz patrzy na grę, nie na to okno: brak tekstu, błąd dostawcy albo pudła
        // Cache-only dostają komunikat w nakładce (przy zaznaczonym regionie). Pokazujemy go
        // po ShowBlocks (ono zdejmuje ukrycie skrótem). Gdy nic nie przetłumaczono, ShowBlocks
        // się nie wykonuje i nakładka zostaje schowana — ForManualResult oznacza więc komunikat
        // jako odpowiedź na żądanie gracza, żeby przeszedł mimo ukrycia (inaczej: cisza).
        var overlayMode = _settings.ResultDisplayMode == "overlay";
        var notice = overlayMode
            ? OverlayNotices.ForManualResult(result.Blocks.Select(static b => b.Outcome).ToList(), _orchestrator.ActiveProvider.Name)
            : null;

        if (result.Blocks.Count == 0)
        {
            ShowOverlayNotice(notice, result.Region);
            SetStatus(result.Warning ?? "OCR nie rozpoznał tekstu w zaznaczonym obszarze.");
            return;
        }

        if (overlayMode)
        {
            var translated = result.Blocks
                .Where(static b => b.Outcome.TranslatedText is not null)
                .Select(static b => (b.Block.Box, b.Outcome.TranslatedText!, Core.Text.TextBlockMetrics.MedianLineHeight(b.Block), b.TextColorRgb))
                .ToList();
            if (translated.Count > 0)
            {
                _overlay.ProfileFontFamily = _orchestrator.ActiveProfile?.Overlay?.FontFamily;
                _overlay.ShowBlocks(translated, _settings);
            }
            ShowOverlayNotice(notice, result.Region);
        }
        else
        {
            _panel.ShowResults(result, _settings, SaveCorrectionAsync, AddTermAsync);
        }

        SetStatus(result.Warning ?? $"Przetłumaczono {result.Blocks.Count} bloków tekstu ({result.Timings.TotalMs} ms).");
    }

    private Task SaveCorrectionAsync(ResultItem item, string corrected) =>
        _orchestrator.SaveManualCorrectionAsync(item.Block, corrected);

    private Task AddTermAsync(ResultItem item) =>
        _orchestrator.AddGlossaryTermAsync(item.SourceText, item.TranslatedText);

    private void OnToggleOverlayClick(object sender, RoutedEventArgs e) => _overlay.ToggleVisibility();

    private TranslationProviderInfo CurrentProviderInfo => TranslationProviderCatalog.Resolve(_settings.Provider);

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        var provider = CurrentProviderInfo;
        if (provider.SecretName is not { } secretName)
        {
            SetStatus($"Dostawca „{provider.DisplayName}” nie używa klucza API.");
            return;
        }

        var key = PwdApiKey.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Wpisz klucz API w polu obok, zanim go zapiszesz.");
            return;
        }

        // Klucz serwera LLM jest przypisany do adresu, dla którego go zapisano — po zmianie
        // adresu nie wyjdzie pod nowy serwer bez ponownego zapisania.
        Uri? llmEndpoint = null;
        if (provider.ExtraSettings.Contains(ProviderSetting.LlmEndpoint)
            && !LlmEndpoint.TryNormalize(_settings.LlmEndpoint, out llmEndpoint, out var endpointError))
        {
            SetStatus("⚠ Najpierw podaj poprawny adres serwera LLM: " + endpointError);
            return;
        }

        _secrets.Save(secretName, key.Trim());
        PwdApiKey.Clear();
        if (llmEndpoint is not null)
        {
            _settings.LlmKeyHost = llmEndpoint.Authority;
            SaveSettingsWithoutRebuild();
        }
        UpdateKeyStatus();
        SetStatus(llmEndpoint is not null
            ? $"Klucz zapisany bezpiecznie (Windows DPAPI) dla serwera {llmEndpoint.Authority}."
            : $"Klucz {provider.DisplayName} zapisany bezpiecznie (Windows DPAPI).");
    }

    /// <summary>Zapis zmian, które dostawcy czytają na bieżąco (np. serwer klucza LLM).</summary>
    private void SaveSettingsWithoutRebuild()
    {
        _settingsStore.Save(_settings);
        MarkSettingsApplied();
    }

    /// <summary>
    /// Model Claude zapisuje się po wyborze z listy albo po wyjściu z pola — nie w trakcie
    /// pisania, bo każda zmiana przebudowuje pipeline i anuluje tłumaczenia w locie.
    /// </summary>
    private void OnClaudeModelDropDownClosed(object? sender, EventArgs e) => OnSettingChanged(CmbClaudeModel, new RoutedEventArgs());

    private void OnLlmPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LlmServerPreset preset }) return;
        TxtLlmEndpoint.Text = preset.Endpoint;
        if (preset.Model is { } model) TxtLlmModel.Text = model;
        _settings.LlmServerOptions = preset.ServerOptionsForHost();
        OnSettingChanged(sender, e);
    }

    private async void OnTestKeyClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Testuję połączenie z dostawcą tłumaczeń…");
        try
        {
            var status = await _orchestrator.TestActiveProviderAsync();
            TxtKeyStatus.Text = status.Message;
            SetStatus(status.IsOk ? "Połączenie działa. ✔" : "Test połączenia nie powiódł się.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd testu połączenia");
            SetStatus("Błąd testu połączenia — szczegóły w logu diagnostycznym.");
        }
    }

    private void OnDeleteKeyClick(object sender, RoutedEventArgs e)
    {
        var provider = CurrentProviderInfo;
        if (provider.SecretName is not { } secretName) return;

        _secrets.Delete(secretName);
        if (provider.ExtraSettings.Contains(ProviderSetting.LlmEndpoint) && _settings.LlmKeyHost is not null)
        {
            _settings.LlmKeyHost = null;
            SaveSettingsWithoutRebuild();
        }
        UpdateKeyStatus();
        SetStatus($"Klucz {provider.DisplayName} usunięty.");
    }

    /// <summary>Pokazuje pola i podpowiedzi wybranego dostawcy (region Azure, serwer LLM, model Claude).</summary>
    private void UpdateProviderPanel()
    {
        var provider = CurrentProviderInfo;
        GrpProvider.Header = $"Dostawca: {provider.DisplayName}";
        PnlAzureSettings.Visibility = VisibleFor(provider, ProviderSetting.AzureRegion);
        PnlLlmSettings.Visibility = VisibleFor(provider, ProviderSetting.LlmEndpoint);
        PnlClaudeSettings.Visibility = VisibleFor(provider, ProviderSetting.ClaudeModel);

        PwdApiKey.IsEnabled = BtnSaveKey.IsEnabled = BtnDeleteKey.IsEnabled = provider.UsesApiKey;
        TxtProviderHint.Text = provider.UsesApiKey
            ? provider.KeyHint + " Klucz jest szyfrowany przez Windows (DPAPI) i nie opuszcza tego komputera."
            : provider.KeyHint;
        UpdateKeyStatus();
    }

    private static Visibility VisibleFor(TranslationProviderInfo provider, ProviderSetting setting) =>
        provider.ExtraSettings.Contains(setting) ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateKeyStatus()
    {
        var provider = CurrentProviderInfo;
        if (provider.SecretName is not { } secretName)
        {
            TxtKeyStatus.Text = "Ten dostawca nie potrzebuje klucza ani internetu.";
            return;
        }

        var hasKey = _secrets.Load(secretName) is not null;
        var status = (hasKey, provider.KeyOptional) switch
        {
            (true, _) => $"Klucz {provider.DisplayName}: zapisany ✔",
            (false, true) => "Bez klucza — wystarczy dla serwera na tym komputerze (Ollama, LM Studio). Usługi w chmurze wymagają klucza.",
            _ => $"Brak klucza {provider.DisplayName} — tłumaczenie online nie zadziała. Do testów bez klucza wybierz dostawcę „Mock”.",
        };

        // Dla serwera LLM mówimy wprost, dokąd trafia tekst z ekranu.
        if (provider.ExtraSettings.Contains(ProviderSetting.LlmEndpoint))
        {
            if (LlmEndpoint.TryNormalize(_settings.LlmEndpoint, out var endpoint, out var error))
            {
                status += LlmEndpoint.IsLoopback(endpoint!)
                    ? " Tekst zostaje na tym komputerze."
                    : $" Tekst z ekranu trafia do: {endpoint!.Host}.";
                if (hasKey && !OpenAiCompatibleTranslationProvider.KeyBelongsTo(_settings.LlmKeyHost, endpoint!))
                {
                    status += $" Klucz zapisano dla innego serwera ({_settings.LlmKeyHost ?? "nieznany"}) — " +
                              $"nie jest wysyłany do {endpoint!.Authority}.";
                }
                if (_settings.LlmServerOptions is { HasRequestFields: true } serverOptions && serverOptions.AppliesTo(endpoint!))
                {
                    status += $" Opcje serwera: {serverOptions.Describe()}.";
                }
            }
            else
            {
                status += " ⚠ " + error;
            }
        }
        TxtKeyStatus.Text = status;
    }

    private void UpdateOcrStatus()
    {
        var languages = string.Join(", ", _ocr.AvailableLanguages);
        TxtOcrStatus.Text = _ocr.IsLanguageAvailable(_settings.SourceLanguage)
            ? $"OCR Windows gotowy (języki: {languages})."
            : $"⚠ Brak pakietu OCR dla języka „{_settings.SourceLanguage}”. Zainstalowane: {languages}. " +
              "Dodaj język w: Ustawienia → Czas i język → Język i region.";
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;

        // Pola tekstowe zapisują się przy utracie fokusu — samo przejście między nimi
        // nie może przebudowywać pipeline'u (to anuluje tłumaczenia będące w locie).
        string? warning = null;

        _settings.SourceLanguage = CmbSourceLang.SelectedItem as string ?? "en";
        _settings.TargetLanguage = CmbTargetLang.SelectedItem as string ?? "pl";
        _settings.Provider = (CmbProvider.SelectedItem as TranslationProviderInfo)?.Id ?? TranslationProviderCatalog.DeepL.Id;

        var region = TxtAzureRegion.Text.Trim();
        if (AzureTranslatorProvider.IsValidRegion(region))
        {
            _settings.AzureRegion = region.Length == 0 ? null : region;
        }
        else
        {
            TxtAzureRegion.Text = _settings.AzureRegion ?? string.Empty;
            warning = "Region Azure może zawierać tylko litery, cyfry i myślniki (np. westeurope).";
        }

        _settings.LlmEndpoint = TxtLlmEndpoint.Text.Trim();
        if (_settings.Provider == TranslationProviderCatalog.Llm.Id
            && !LlmEndpoint.TryNormalize(_settings.LlmEndpoint, out _, out var endpointError))
        {
            warning = endpointError;
        }
        _settings.LlmModel = TxtLlmModel.Text.Trim() is { Length: > 0 } llmModel ? llmModel : null;
        _settings.ClaudeModel = CmbClaudeModel.Text.Trim() is { Length: > 0 } claudeModel
            ? claudeModel
            : ClaudeTranslationProvider.DefaultModel;
        _settings.ResultDisplayMode = CmbDisplayMode.SelectedIndex == 1 ? "overlay" : "panel";
        _settings.LiveDisplayMode = CmbLiveStyle.SelectedIndex == 1 ? "subtitle" : "at-source";
        _settings.OverlayPlacement = CmbPlacement.SelectedIndex == 1 ? "cover" : "below";
        _settings.OverlayBackgroundOpacity = CmbBackground.SelectedIndex switch
        {
            2 => 0.0,
            1 => 0.40,
            _ => 0.85,
        };
        _settings.CacheOnlyMode = ChkCacheOnly.IsChecked == true;
        _settings.PrivateMode = ChkPrivate.IsChecked == true;

        var selectedProfileName = CmbProfile.SelectedItem as string;
        _settings.ActiveProfileId = _orchestrator.Profiles
            .FirstOrDefault(p => p.Name == selectedProfileName)?.Id;

        if (double.TryParse(TxtFontSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
            && (fontSize == 0 || fontSize is >= 9 and <= 48))
        {
            _settings.OverlayFontSize = fontSize;
        }
        else
        {
            TxtFontSize.Text = _settings.OverlayFontSize.ToString(CultureInfo.InvariantCulture);
        }

        _settings.OverlayFontFamily = CmbFont.SelectedItem is string font && font != AutoFontLabel ? font : AppSettings.AutoFontFamily;
        _settings.PlayerGender = PlayerGenders.ToSetting(CmbPlayerGender.SelectedIndex switch
        {
            1 => PlayerGender.Male,
            2 => PlayerGender.Female,
            _ => PlayerGender.Unknown,
        });

        UpdateProviderPanel();
        var snapshot = SettingsSnapshot();
        if (snapshot == _appliedSettingsJson)
        {
            if (warning is not null) SetStatus("⚠ " + warning);
            return;
        }

        _settingsStore.Save(_settings);
        // Sam wygląd (czcionka, tło, styl live…) nakładka czyta na bieżąco — bez przebudowy,
        // która wyczyściłaby pamięć dialogu i anulowała tłumaczenia w locie.
        var pipelineSnapshot = _settings.PipelineSnapshot();
        if (pipelineSnapshot == _appliedPipelineJson)
        {
            _appliedSettingsJson = snapshot;
            SetStatus(warning is not null ? "⚠ " + warning : "Wygląd zapisany.");
            return;
        }
        _orchestrator.RebuildPipeline();
        _appliedSettingsJson = snapshot;
        _appliedPipelineJson = pipelineSnapshot;

        var profileInfo = _orchestrator.ActiveProfile is { } profile ? $", profil: {profile.Name}" : string.Empty;
        if (_orchestrator.ActiveCorpus.IsLoaded) profileInfo += $", korpus: {_orchestrator.ActiveCorpus.Texts:N0} tekstów";
        SetStatus(warning is not null
            ? "⚠ " + warning
            : $"Ustawienia zapisane (dostawca: {CurrentProviderInfo.DisplayName}{profileInfo}).");
    }

    private async void OnStatusTimerTick(object? sender, EventArgs e)
    {
        TxtCounters.Text =
            $"API: {_usage.ApiRequests} zapytań / {_usage.ApiCharacters:N0} znaków  •  " +
            $"Cache: {_usage.CacheHits}  •  Słownik: {_usage.GlossaryHits}  •  Błędy: {_usage.FailedRequests}";

        var latency = _usage.Latency.Describe(_orchestrator.ActiveProvider.Name);
        TxtLatency.Text = latency.Length > 0 ? latency : "Brak pomiarów — przetłumacz coś albo uruchom live.";

        if (++_statusTicks % 5 != 0 || _settings.PrivateMode) return;
        try
        {
            var stats = await _persistentCache.GetStatsAsync();
            TxtCacheStatus.Text =
                $"Cache SQLite: {stats.TotalEntries} wpisów ({stats.ManualEntries} ręcznych korekt), " +
                $"{stats.DatabaseSizeBytes / 1024.0:N0} KB.";
        }
        catch (CacheStorageException ex)
        {
            TxtCacheStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            // Zablokowany/usunięty plik bazy nie może zasypać użytkownika modalnymi błędami
            // z timera — pokazujemy status i logujemy techniczny szczegół.
            _logger.LogWarning(ex, "Nie udało się odczytać statystyk cache");
            TxtCacheStatus.Text = "Cache SQLite: statystyki chwilowo niedostępne (szczegóły w logu).";
        }
    }

    private void OnCopyLatencyReportClick(object sender, RoutedEventArgs e)
    {
        var report = _usage.Latency.Report(_orchestrator.ActiveProvider.Name);
        if (report.Length == 0)
        {
            SetStatus("Brak pomiarów czasu do skopiowania.");
            return;
        }
        try
        {
            Clipboard.SetText(report);
            SetStatus("Raport czasów skopiowany do schowka.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("Schowek jest zajęty przez inny program — spróbuj ponownie.");
        }
    }

    private void OnResetLatencyClick(object sender, RoutedEventArgs e)
    {
        _usage.Latency.Reset();
        TxtLatency.Text = "Brak pomiarów — przetłumacz coś albo uruchom live.";
        SetStatus("Pomiary czasu wyzerowane.");
    }

    private void SetStatus(string message)
    {
        // Wyłącznie pasek statusu w UI. Statusy zawierają treści z ekranu użytkownika
        // (tytuły okien, komunikaty o tłumaczeniach) — NIE wolno ich logować na dysk.
        TxtStatus.Text = message;
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || _preselectingRememberedGame || WindowsList.SelectedItem is not TargetWindow window) return;
        ApplyProfileAutoSelection(window);
    }

    /// <summary>
    /// Autodetekcja profilu po nazwie procesu — tylko gdy użytkownik nie wybrał żadnego
    /// profilu ręcznie (nie nadpisujemy jego decyzji). Zmiana profilu przebudowuje pipeline,
    /// więc przy działającej sesji live nic nie robimy: przebudowa anulowałaby jej tłumaczenia.
    /// Skrót live wywołuje to PRZED startem sesji.
    /// </summary>
    private void ApplyProfileAutoSelection(TargetWindow window)
    {
        if (_liveSession is not null) return;
        if (CmbProfile.SelectedItem as string != NoProfileLabel) return;

        var match = _orchestrator.Profiles.FirstOrDefault(p =>
            p.ProcessNames.Any(name => LiveTargetResolver.SameProcess(name, window.ProcessName)));
        if (match is not null)
        {
            CmbProfile.SelectedItem = match.Name;
            SetStatus($"Wykryto grę „{match.Name}” — profil włączony automatycznie.");
        }
    }

    private string? RememberedGameProcess => _privateGameProcess ?? _settings.LastGameProcess;

    private string? RememberedGameTitle => _privateGameProcess is not null ? _privateGameTitle : _settings.LastGameTitle;

    /// <summary>Okno zapamiętanej gry na liście (ten sam tytuł, inaczej największe okno procesu).</summary>
    private TargetWindow? FindRememberedGame(IReadOnlyList<TargetWindow> windows)
    {
        if (string.IsNullOrWhiteSpace(RememberedGameProcess)) return null;
        var resolution = LiveTargetResolver.Resolve(
            windows.Select(static w => w.ToCandidate()).ToList(), foreground: 0,
            RememberedGameProcess, RememberedGameTitle, profileProcessNames: null, OwnProcessName);
        return resolution.Reason == LiveTargetReason.RememberedProcess
            ? windows.FirstOrDefault(w => w.Handle == resolution.Window!.Handle)
            : null;
    }

    private static string? OwnProcessName => Path.GetFileName(Environment.ProcessPath);

    /// <summary>
    /// Skrót live (i pozycja w zasobniku): działająca sesja → stop jak przyciskiem Stop;
    /// inaczej start na aktywnej grze bez Alt+Tab do okna tłumacza.
    /// </summary>
    private async Task ToggleLiveFromHotkeyAsync()
    {
        if (_liveSession is not null)
        {
            // Zadanie ze skrótu/zasobnika jest odrzucane (`_ =`), więc wyjątek z zamykania sesji
            // przepadłby bez śladu — łapiemy go i logujemy jak przy starcie.
            try
            {
                OnStopLiveClick(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd zatrzymywania trybu live skrótem");
                SetStatus("Nie udało się zatrzymać trybu live — szczegóły w logu diagnostycznym.");
            }
            return;
        }
        if (_liveHotkeyBusy) return;

        // Okno pierwszoplanowe czytamy synchronicznie, ZANIM cokolwiek odda sterowanie —
        // po await fokus mógłby już przejść gdzie indziej (np. na okno tłumacza).
        var foreground = WindowEnumerator.GetForegroundRootWindow();
        _liveHotkeyBusy = true;
        try
        {
            var windows = await Task.Run(WindowEnumerator.GetOpenWindows);
            // W międzyczasie użytkownik mógł kliknąć Start — nie uruchamiamy drugiej sesji.
            if (_liveSession is not null) return;

            var resolution = LiveTargetResolver.Resolve(
                windows.Select(static w => w.ToCandidate()).ToList(),
                foreground,
                RememberedGameProcess,
                RememberedGameTitle,
                _orchestrator.Profiles.SelectMany(static p => p.ProcessNames),
                OwnProcessName);

            var target = resolution.Window is { } candidate
                ? windows.FirstOrDefault(w => w.Handle == candidate.Handle)
                : null;
            if (target is null)
            {
                var message = resolution.Message ?? LiveTargetResolver.NoTargetMessage;
                SetStatus(message);
                TxtLiveStatus.Text = message;
                ShowTrayNotification(message);
                return;
            }

            // Listę podmieniamy dopiero, gdy jest cel — inaczej skrót wciśnięty z pulpitu
            // skasowałby ręczne zaznaczenie gry na liście.
            WindowsList.ItemsSource = windows;

            // Zaznaczenie na liście uruchamia autodetekcję profilu; wywołanie wprost jest
            // idempotentne i gwarantuje, że ewentualna przebudowa pipeline'u nastąpi PRZED startem.
            WindowsList.SelectedItem = target;
            WindowsList.ScrollIntoView(target);
            ApplyProfileAutoSelection(target);
            StartLive(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd uruchamiania trybu live skrótem");
            SetStatus("Nie udało się uruchomić trybu live — szczegóły w logu diagnostycznym.");
        }
        finally
        {
            _liveHotkeyBusy = false;
        }
    }

    private void ShowTrayNotification(string message)
    {
        if (_trayIcon is null) return;
        try
        {
            // Stały tekst komunikatu — bez tytułów okien ani treści z ekranu.
            _trayIcon.ShowNotification("GameTranslatorOverlay", message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się pokazać powiadomienia w zasobniku");
        }
    }

    /// <summary>
    /// Zapamiętuje grę dla skrótu i odświeżania listy. Nie przebudowuje pipeline'u (nazwa gry nie
    /// wpływa na tłumaczenie). W trybie prywatnym nic nie trafia na dysk — tylko do pamięci.
    /// </summary>
    private void RememberGame(TargetWindow window)
    {
        if (_settings.PrivateMode)
        {
            _privateGameProcess = window.ProcessName;
            _privateGameTitle = window.Title;
            return;
        }

        _privateGameProcess = null;
        _privateGameTitle = null;
        if (_settings.LastGameProcess == window.ProcessName && _settings.LastGameTitle == window.Title) return;

        // Migawkę „zastosowanych” ustawień przesuwamy tylko wtedy, gdy była aktualna —
        // inaczej zgubilibyśmy oczekującą zmianę, która wymaga przebudowy pipeline'u.
        var wasApplied = SettingsSnapshot() == _appliedSettingsJson;
        _settings.LastGameProcess = window.ProcessName;
        _settings.LastGameTitle = window.Title;
        try
        {
            _settingsStore.Save(_settings);
            if (wasApplied) _appliedSettingsJson = SettingsSnapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Brak zapamiętanej gry nie może blokować trybu live.
            _logger.LogWarning(ex, "Nie udało się zapisać ostatniej gry w ustawieniach");
        }
    }

    private void OnStartLiveClick(object sender, RoutedEventArgs e)
    {
        if (_liveSession is not null) return;
        if (WindowsList.SelectedItem is not TargetWindow window)
        {
            SetStatus("Najpierw wybierz okno gry z listy po lewej.");
            return;
        }
        StartLive(window);
    }

    private void StartLive(TargetWindow window)
    {
        if (_liveSession is not null) return;

        var profile = _orchestrator.ActiveProfile;
        var upscale = OcrScaling.ResolvePreference(profile?.Ocr?.Upscale, _settings.OcrUpscale);
        var options = new LiveSessionOptions
        {
            Fps = profile?.ChangeDetection?.Fps ?? 6,
            // Domyślnie 0: każda mocna zmiana komórki budzi przetwarzanie — krótkie
            // linijki dialogów w grach ze statycznym obrazem zmieniają ledwie kilka komórek.
            ChangeThreshold = profile?.ChangeDetection?.Threshold ?? 0.0,
            OcrUpscale = upscale.Preferred,
            AllowAutoUpscale = upscale.AllowAuto,
            NoticeEcho = _noticeEcho,
            BuildGlyphCovers = () => _settings.OverlayPlacement == "cover" && _settings.LiveDisplayMode != "subtitle",
            HoldTypingPrefixes = () => _settings.OverlayPlacement == "cover" && _settings.LiveDisplayMode != "subtitle",
            IdentityEchoSafe = () => Ui.OverlayBlockRenderer.HidesIdenticalText(_settings) || _overlay.IsCaptureExclusionActive,
        };

        // Wczesne utworzenie HWND nakładki, żeby wiedzieć, czy wykluczenie z capture działa.
        _overlay.EnsureHandleCreated();
        _overlay.ProfileFontFamily = profile?.Overlay?.FontFamily;
        if (_settings.OverlayPlacement == "cover")
        {
            Ui.OverlayFonts.WarmUp(Ui.OverlayFonts.ResolveFamilyName(_settings, _overlay.ProfileFontFamily));
            _ = Task.Run(GameTranslatorOverlay.Core.Vision.GlyphCoverBuilder.WarmUp);
        }
        if (!_overlay.IsCaptureExclusionActive)
        {
            _logger.LogWarning("Wykluczenie nakładki z przechwytywania nie działa na tym systemie — aktywny filtr anty-sprzężeniowy");
        }

        _orchestrator.WarmUpActiveProvider();

        LiveTranslationSession? session = null;
        session = new LiveTranslationSession(
            _orchestrator, _ocr, window.Handle, options,
            update => Dispatcher.BeginInvoke(() =>
            {
                // Aktualizacje nieaktywnej (zatrzymanej/wymienionej) sesji nie mogą
                // malować po nakładce ani ubijać nowej sesji.
                if (!ReferenceEquals(session, _liveSession)) return;
                HandleLiveUpdate(update);
            }),
            _logger);
        _liveSession = session;
        _liveWindowHandle = window.Handle;
        session.Start();

        UpdateLiveControls();
        SetLiveIndicator(LiveIndicatorState.Active);
        SetStatus($"Tryb live uruchomiony dla „{window.Title}” ({options.Fps:0.#} analiz/s).");
        RememberGame(window);
    }

    private string LiveMenuHeader() => _liveSession is not null
        ? $"⏹ Stop live{HotkeySuffix()}"
        : $"▶ Start live na aktywnej grze{HotkeySuffix()}";

    private string HotkeySuffix() => _liveHotkeyRegistered ? $"  ({_settings.LiveToggleHotkey})" : string.Empty;

    /// <summary>Przyciski, pozycja w zasobniku i podpowiedź ikony zgodne ze stanem trybu live.</summary>
    private void UpdateLiveControls()
    {
        var running = _liveSession is not null;
        BtnStartLive.IsEnabled = !running;
        BtnStopLive.IsEnabled = running;
        BtnStartLive.Content = $"▶ Start live{HotkeySuffix()}";
        if (_trayLiveItem is not null) _trayLiveItem.Header = LiveMenuHeader();
        if (_trayIcon is not null)
        {
            // Bez tytułu okna gry — podpowiedź ikony widać też na zrzutach i nagraniach pulpitu.
            _trayIcon.ToolTipText = running ? "GameTranslatorOverlay — live: włączony" : "GameTranslatorOverlay — live: wyłączony";
        }
    }

    private enum LiveIndicatorState { Idle, Active, Paused }

    private void SetLiveIndicator(LiveIndicatorState state)
    {
        LiveIndicator.Fill = new System.Windows.Media.SolidColorBrush(state switch
        {
            LiveIndicatorState.Active => System.Windows.Media.Color.FromRgb(0x43, 0xA0, 0x47),
            LiveIndicatorState.Paused => System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x00),
            _ => System.Windows.Media.Color.FromRgb(0x54, 0x6E, 0x7A),
        });
    }

    private void HandleLiveUpdate(LiveUpdate update)
    {
        TxtLiveStatus.Text = update.StatusLine;

        if (update.ClearOverlay)
        {
            // Automatic scene cleanup must not undo the user's Ctrl+Shift+H choice.
            _overlay.ClearBlocks(preserveUserHidden: true);
        }
        if (update.ClearSubtitle) _overlay.ClearSubtitle();
        if (update.HideOverlay)
        {
            // Ruch sceny: bloki są chwilowo nieaktualne — chowamy, wrócą po najbliższym OCR.
            _overlay.Hide();
            SetLiveIndicator(LiveIndicatorState.Paused);
        }
        else if (update.Blocks is not null && !update.Stopped)
        {
            SetLiveIndicator(LiveIndicatorState.Active);
        }
        // Komunikat (błąd dostawcy, Cache-only, start/stop) przy górnej krawędzi okna gry.
        // Warstwa komunikatu przetrwa czyszczenie nakładki powyżej.
        if (update.Notice is { } notice)
        {
            ShowOverlayNotice(notice, update.WindowBounds.IsEmpty ? LiveWindowBounds() : update.WindowBounds);
        }
        if (update.Stopped)
        {
            StopLiveSession();
            return;
        }

        if (update.Blocks is { } blocks)
        {
            if (_settings.LiveDisplayMode == "subtitle")
            {
                if (update.SubtitleText is { Length: > 0 } subtitle)
                {
                    _overlay.ShowSubtitle(subtitle, update.WindowBounds, _settings, update.PreserveSubtitleLifetime);
                }
                else
                {
                    // Okno gry przesunęło się bez nowego tekstu — dosuwamy pasek napisów.
                    _overlay.RepositionSubtitle(update.WindowBounds);
                }
            }
            else
            {
                _overlay.ProfileFontFamily = _orchestrator.ActiveProfile?.Overlay?.FontFamily;
                _overlay.UpdateLiveBlocks(blocks, _settings);
            }
        }
    }

    private void StopLiveSession()
    {
        _liveSession?.Dispose();
        _liveSession = null;
        _liveWindowHandle = IntPtr.Zero;
        UpdateLiveControls();
        SetLiveIndicator(LiveIndicatorState.Idle);
    }

    private void OnStopLiveClick(object sender, RoutedEventArgs e)
    {
        var wasRunning = _liveSession is not null;
        var bounds = LiveWindowBounds();
        StopLiveSession();
        _overlay.ClearBlocks();
        if (wasRunning) ShowOverlayNotice(OverlayNotices.LiveStopped(), bounds);
        TxtLiveStatus.Text = "Tryb live zatrzymany.";
        SetStatus("Tryb live zatrzymany.");
    }

    private void OnOpenGlossaryEditorClick(object sender, RoutedEventArgs e)
    {
        var editor = new GlossaryEditorWindow(
            _userGlossaryStore, _orchestrator, _settings.SourceLanguage, _settings.TargetLanguage)
        {
            Owner = this,
        };
        editor.ShowDialog();
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            "Usunąć wszystkie automatyczne wpisy z cache tłumaczeń?\n\nRęczne korekty zostaną zachowane.",
            "GameTranslatorOverlay", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) return;

        try
        {
            var removed = await _persistentCache.ClearAsync(keepManualCorrections: true);
            SetStatus($"Wyczyszczono cache: usunięto {removed} wpisów (ręczne korekty zachowane).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd czyszczenia cache");
            SetStatus("Nie udało się wyczyścić cache — szczegóły w logu diagnostycznym.");
        }
    }

    private async void OnExportCacheClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Eksport cache JSON|*.json",
            FileName = "gametranslator-cache.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var json = await _persistentCache.ExportJsonAsync();
            await File.WriteAllTextAsync(dialog.FileName, json);
            SetStatus($"Wyeksportowano cache do: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd eksportu cache");
            SetStatus("Nie udało się wyeksportować cache — szczegóły w logu diagnostycznym.");
        }
    }

    private async void OnImportCacheClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Eksport cache JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var imported = await _persistentCache.ImportJsonAsync(json);
            SetStatus($"Zaimportowano {imported} wpisów do cache.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            SetStatus("Ten plik nie wygląda na eksport cache GameTranslatorOverlay.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd importu cache");
            SetStatus("Nie udało się zaimportować cache — szczegóły w logu diagnostycznym.");
        }
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _paths.RootDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udało się otworzyć folderu danych");
            SetStatus($"Folder danych: {_paths.RootDirectory}");
        }
    }

    private void OnClosedHandler(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _trayIcon?.Dispose();
        // Zatrzymanie sesji niżej odświeża stan live — nie może dotykać zwolnionej ikony.
        _trayIcon = null;
        _trayLiveItem = null;
        if (_trayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }
        StopLiveSession();
        _orchestrator.CancelActiveOperation();
        _hotkeys.Dispose();
        RegionSelectWindow.CloseActive();
        _overlay.Close();
        _panel.ForceClose();
        _settingsStore.Save(_settings);
        // _previewBitmap celowo bez Dispose: operacja OCR w tle mogłaby jeszcze z niej
        // korzystać (use-after-dispose = twardy crash GDI+), a proces i tak się kończy.
    }
}
