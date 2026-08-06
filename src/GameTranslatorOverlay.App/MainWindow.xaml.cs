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
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using GameTranslatorOverlay.Core.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameTranslatorOverlay.App;

public partial class MainWindow : Window
{
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

        CmbProvider.ItemsSource = new[] { "DeepL", "Mock" };
        CmbProvider.SelectedItem = _settings.Provider;
        if (CmbProvider.SelectedItem is null) CmbProvider.SelectedIndex = 0;

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

        TxtFontSize.Text = _settings.OverlayFontSize.ToString(CultureInfo.InvariantCulture);
        ChkCacheOnly.IsChecked = _settings.CacheOnlyMode;
        ChkPrivate.IsChecked = _settings.PrivateMode;

        _loadingUi = false;

        UpdateOcrStatus();
        UpdateKeyStatus();
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

        foreach (var warning in _orchestrator.ContentWarnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        InitializeTrayIcon();
        _statusTimer.Start();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateMenuItem("Pokaż okno", RestoreFromTray));
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
        SetStatus($"Znaleziono {windows.Count} okien. Wybierz okno gry albo od razu użyj Ctrl+Shift+T.");
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
            // Ponowny skrót w trakcie wiszącego tłumaczenia = prawdziwe latest-wins:
            // anulujemy starą operację zamiast po cichu ignorować użytkownika.
            _orchestrator.CancelActiveOperation();
            return;
        }
        _selectingRegion = true;
        try
        {
            var region = await RegionSelectWindow.SelectAsync();
            if (region is not { } selected)
            {
                SetStatus("Zaznaczanie anulowane.");
                return;
            }

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
        }
        catch (CacheStorageException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd tłumaczenia regionu");
            SetStatus("Nieoczekiwany błąd tłumaczenia — szczegóły w logu diagnostycznym.");
        }
        finally
        {
            _selectingRegion = false;
        }
    }

    private void DisplayResult(RegionTranslationResult result)
    {
        TxtLastOcr.Text = string.Join("\n\n", result.Blocks.Select(static b => b.Block.Text));
        TxtLastTranslation.Text = string.Join("\n\n", result.Blocks.Select(static b =>
            b.Outcome.TranslatedText ?? $"⚠ {b.Outcome.ErrorMessage}"));
        TxtTiming.Text = "Czasy: " + result.Timings;

        if (result.Blocks.Count == 0)
        {
            SetStatus(result.Warning ?? "OCR nie rozpoznał tekstu w zaznaczonym obszarze.");
            return;
        }

        if (_settings.ResultDisplayMode == "overlay")
        {
            var translated = result.Blocks
                .Where(static b => b.Outcome.TranslatedText is not null)
                .Select(static b => (b.Block.Box, b.Outcome.TranslatedText!))
                .ToList();
            if (translated.Count > 0)
            {
                _overlay.ShowBlocks(translated, _settings);
            }
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

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        var key = PwdApiKey.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Wpisz klucz API w polu obok, zanim go zapiszesz.");
            return;
        }

        _secrets.Save(SecretNames.DeepLApiKey, key.Trim());
        PwdApiKey.Clear();
        UpdateKeyStatus();
        SetStatus("Klucz API zapisany bezpiecznie (Windows DPAPI).");
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
        _secrets.Delete(SecretNames.DeepLApiKey);
        UpdateKeyStatus();
        SetStatus("Klucz API usunięty.");
    }

    private void UpdateKeyStatus()
    {
        var hasKey = _secrets.Load(SecretNames.DeepLApiKey) is not null;
        TxtKeyStatus.Text = hasKey
            ? "Klucz DeepL: zapisany ✔"
            : "Brak klucza DeepL — tłumaczenie online nie zadziała. Do testów bez klucza wybierz dostawcę „Mock”.";
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

        _settings.SourceLanguage = CmbSourceLang.SelectedItem as string ?? "en";
        _settings.TargetLanguage = CmbTargetLang.SelectedItem as string ?? "pl";
        _settings.Provider = CmbProvider.SelectedItem as string ?? "DeepL";
        _settings.ResultDisplayMode = CmbDisplayMode.SelectedIndex == 1 ? "overlay" : "panel";
        _settings.LiveDisplayMode = CmbLiveStyle.SelectedIndex == 1 ? "subtitle" : "at-source";
        _settings.OverlayPlacement = CmbPlacement.SelectedIndex == 1 ? "cover" : "below";
        _settings.CacheOnlyMode = ChkCacheOnly.IsChecked == true;
        _settings.PrivateMode = ChkPrivate.IsChecked == true;

        var selectedProfileName = CmbProfile.SelectedItem as string;
        _settings.ActiveProfileId = _orchestrator.Profiles
            .FirstOrDefault(p => p.Name == selectedProfileName)?.Id;

        if (double.TryParse(TxtFontSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
            && fontSize is >= 9 and <= 48)
        {
            _settings.OverlayFontSize = fontSize;
        }
        else
        {
            TxtFontSize.Text = _settings.OverlayFontSize.ToString(CultureInfo.InvariantCulture);
        }

        _settingsStore.Save(_settings);
        _orchestrator.RebuildPipeline();
        UpdateKeyStatus();

        var profileInfo = _orchestrator.ActiveProfile is { } profile ? $", profil: {profile.Name}" : string.Empty;
        SetStatus($"Ustawienia zapisane (dostawca: {_settings.Provider}{profileInfo}).");
    }

    private async void OnStatusTimerTick(object? sender, EventArgs e)
    {
        TxtCounters.Text =
            $"API: {_usage.ApiRequests} zapytań / {_usage.ApiCharacters:N0} znaków  •  " +
            $"Cache: {_usage.CacheHits}  •  Słownik: {_usage.GlossaryHits}  •  Błędy: {_usage.FailedRequests}";

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

    private void SetStatus(string message)
    {
        // Wyłącznie pasek statusu w UI. Statusy zawierają treści z ekranu użytkownika
        // (tytuły okien, komunikaty o tłumaczeniach) — NIE wolno ich logować na dysk.
        TxtStatus.Text = message;
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || WindowsList.SelectedItem is not TargetWindow window) return;

        // Autodetekcja profilu po nazwie procesu — tylko gdy użytkownik nie wybrał
        // żadnego profilu ręcznie (nie nadpisujemy jego decyzji).
        if (CmbProfile.SelectedItem as string != NoProfileLabel) return;

        var match = _orchestrator.Profiles.FirstOrDefault(p =>
            p.ProcessNames.Any(name => name.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase)));
        if (match is not null)
        {
            CmbProfile.SelectedItem = match.Name;
            SetStatus($"Wykryto grę „{match.Name}” — profil włączony automatycznie.");
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

        var profile = _orchestrator.ActiveProfile;
        var options = new LiveSessionOptions
        {
            Fps = profile?.ChangeDetection?.Fps ?? 4,
            ChangeThreshold = profile?.ChangeDetection?.Threshold ?? 0.02,
            OcrUpscale = profile?.Ocr?.Upscale ?? _settings.OcrUpscale,
        };

        // Wczesne utworzenie HWND nakładki, żeby wiedzieć, czy wykluczenie z capture działa.
        _overlay.EnsureHandleCreated();
        if (!_overlay.IsCaptureExclusionActive)
        {
            _logger.LogWarning("Wykluczenie nakładki z przechwytywania nie działa na tym systemie — aktywny filtr anty-sprzężeniowy");
        }

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
        session.Start();

        BtnStartLive.IsEnabled = false;
        BtnStopLive.IsEnabled = true;
        SetStatus($"Tryb live uruchomiony dla „{window.Title}” ({options.Fps:0.#} analiz/s).");
    }

    private void HandleLiveUpdate(LiveUpdate update)
    {
        TxtLiveStatus.Text = update.StatusLine;

        if (update.ClearOverlay)
        {
            _overlay.ClearBlocks();
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
                    _overlay.ShowSubtitle(subtitle, update.WindowBounds, _settings);
                }
                else
                {
                    // Okno gry przesunęło się bez nowego tekstu — dosuwamy pasek napisów.
                    _overlay.RepositionSubtitle(update.WindowBounds);
                }
            }
            else
            {
                _overlay.UpdateLiveBlocks(blocks, _settings);
            }
        }
    }

    private void StopLiveSession()
    {
        _liveSession?.Dispose();
        _liveSession = null;
        BtnStartLive.IsEnabled = true;
        BtnStopLive.IsEnabled = false;
    }

    private void OnStopLiveClick(object sender, RoutedEventArgs e)
    {
        StopLiveSession();
        _overlay.ClearBlocks();
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
