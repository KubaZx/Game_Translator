using System.Globalization;
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
using GameTranslatorOverlay.Infrastructure.Secrets;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Core.Usage;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<MainWindow> _logger;

    private readonly OverlayWindow _overlay = new();
    private readonly ResultPanelWindow _panel = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private System.Drawing.Bitmap? _previewBitmap;
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
        _logger = logger;

        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosedHandler;
        _statusTimer.Tick += OnStatusTimerTick;
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

        _statusTimer.Start();
    }

    private async Task RefreshWindowsAsync()
    {
        var windows = await Task.Run(WindowEnumerator.GetOpenWindows);
        WindowsList.ItemsSource = windows;
        SetStatus($"Znaleziono {windows.Count} okien. Wybierz okno gry albo od razu użyj Ctrl+Shift+T.");
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshWindowsAsync();

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (WindowsList.SelectedItem is not TargetWindow window)
        {
            SetStatus("Najpierw wybierz okno z listy po lewej.");
            return;
        }

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
    }

    private async void OnOcrTestClick(object sender, RoutedEventArgs e)
    {
        if (_previewBitmap is null)
        {
            SetStatus("Najpierw przechwyć podgląd okna (📷).");
            return;
        }

        try
        {
            var sourceLanguage = _settings.SourceLanguage;
            var result = await Task.Run(async () =>
            {
                var downscale = OcrScaling.ComputeDownscale(_previewBitmap.Width, _previewBitmap.Height, _ocr.MaxImageDimension);
                var working = downscale < 1.0 ? ScreenCapture.Rescale(_previewBitmap, downscale) : _previewBitmap;
                try
                {
                    return await _ocr.RecognizeAsync(ScreenCapture.ToOcrBitmap(working), sourceLanguage);
                }
                finally
                {
                    if (!ReferenceEquals(working, _previewBitmap)) working.Dispose();
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
    }

    private void OnTranslateRegionClick(object sender, RoutedEventArgs e) => _ = TranslateRegionInteractiveAsync();

    private async Task TranslateRegionInteractiveAsync()
    {
        if (_selectingRegion) return;
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
            // Nowsze żądanie przejęło potok (latest-wins) — nic do pokazania.
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
    }

    private void SetStatus(string message)
    {
        TxtStatus.Text = message;
        _logger.LogInformation("{Status}", message);
    }

    private void OnClosedHandler(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _hotkeys.Dispose();
        _overlay.Close();
        _panel.ForceClose();
        _previewBitmap?.Dispose();
        _settingsStore.Save(_settings);
    }
}
