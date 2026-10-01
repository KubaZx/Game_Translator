using System.Diagnostics;
using System.IO;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Vision;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App.Services;

public sealed record LiveDisplayBlock(
    string Key, RectPx ScreenBox, string TranslatedText, int LineHeight = 0, int ColorRgb = -1,
    int BackgroundRgb = -1, int OutlineRgb = -1, BackgroundTexture? Texture = null);

/// <summary>
/// Lokalne metryki jednego zakonczonego OCR, bez tekstu i pikseli. CaptureToUpdateMs
/// mierzy wiek klatki do przygotowania aktualizacji, NIE czas od pojawienia sie
/// tekstu w grze ani opoznienie prezentacji nakladki przez WPF.
/// </summary>
public sealed record LiveFrameDiagnostics(
    double CaptureToUpdateMs,
    long CaptureMs, long OcrMs, long TranslateMs,
    int OcrWidth, int OcrHeight, int RawLines, int RecognizedBlocks,
    int ReusedBlocks, int RetainedBlocks, int DisplayedBlocks,
    bool PartialOcr, bool SceneCut, bool WhiffSuspected, bool UsedScreenFallback)
{
    // Task completion is observed separately from synchronous scene checks.
    // It includes OCR setup/continuation scheduling, not only the native engine.
    public double? OcrOperationMs { get; init; }
    public int OcrSceneChecks { get; init; }
    public double OcrSceneCheckMs { get; init; }
    public int TranslationSceneChecks { get; init; }
    public double TranslationSceneCheckMs { get; init; }
}

public sealed record LiveUpdate(
    string StatusLine,
    IReadOnlyList<LiveDisplayBlock>? Blocks = null,
    string? SubtitleText = null,
    RectPx WindowBounds = default,
    bool ClearOverlay = false,
    bool Stopped = false,
    bool HideOverlay = false,
    LiveFrameDiagnostics? Diagnostics = null)
{
    public bool ClearSubtitle { get; init; }
    public bool PreserveSubtitleLifetime { get; init; }

    /// <summary>
    /// Komunikat dla gracza w nakładce (błąd dostawcy, Cache-only, start/stop live) —
    /// już przepuszczony przez <see cref="OverlayNoticePolicy"/> sesji; null = brak.
    /// </summary>
    public OverlayNotice? Notice { get; init; }
}

public sealed class LiveSessionOptions
{
    public double Fps { get; init; } = 6;

    /// <summary>
    /// Ułamek zmienionych komórek, od którego klatka liczy się jako „zmieniona”.
    /// Domyślnie 0 — KAŻDA komórka ze zmianą ponad próg jasności budzi przetwarzanie:
    /// w grze ze statycznym obrazem krótka linijka dialogu zmienia ledwie 2–5 komórek
    /// i przy dawnym progu 0.02 (26 komórek) w ogóle nie była zauważana.
    /// Filtrem szumu jest próg jasności per komórka (cellDelta), nie ułamek komórek.
    /// </summary>
    public double ChangeThreshold { get; init; }

    public TimeSpan StabilityDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Przy ciągłych zmianach (animowane tło) przetwarzaj mimo braku stabilizacji.
    /// W żywej grze 3D szum sceny stale przekracza <see cref="ChangeThreshold"/>,
    /// więc to ten interwał wyznacza faktyczne tempo tłumaczenia (zmierzono na PoE2).
    /// </summary>
    public TimeSpan ForcedProcessInterval { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Powyżej tego ułamka MOCNO zmienionych komórek scena jest „w ruchu” (gracz
    /// biegnie, kamera płynie) — tłumaczenie czeka, aż obraz się uspokoi.
    /// Pomiar na żywym PoE2: normalna gra (łącznie z biegiem po izometrycznej mapie)
    /// nie przekracza ~9% — próg łapie tylko prawdziwe cięcia i przejścia scen.
    /// </summary>
    public double MotionThreshold { get; init; } = 0.12;

    /// <summary>
    /// Limit odraczania OCR od wykrycia ruchu albo rozpoczęcia odczytu podczas ruchu.
    /// Krótka spokojna próbka nie zeruje oczekiwania: sekwencja ruch/ruch/spokój
    /// resetowała dawny licznik i potrafiła blokować OCR przez cały test 10 s.
    /// Przetwarzamy przy pierwszej próbce po terminie; capture, OCR i tłumaczenie
    /// mają własny koszt, więc nie jest to limit opóźnienia widocznej nakładki.
    /// </summary>
    public TimeSpan MaxMotionPause { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Ile kolejnych przebiegów OCR może nie widzieć bloku, zanim blok zniknie
    /// z nakładki. Chroni przed czknięciami Windows OCR (pusty wynik na
    /// niezmienionej scenie), które bez łaski migają całą nakładką.
    /// </summary>
    public int BlockMissGrace { get; init; } = 2;

    /// <summary>
    /// Ułamek zmienionych komórek, od którego klatkę traktujemy jak cięcie sceny —
    /// wtedy okres łaski nie obowiązuje i nieobecne bloki znikają od razu.
    /// </summary>
    public double SceneCutThreshold { get; init; } = 0.55;

    /// <summary>
    /// Górna granica ponownych przebiegów po podejrzeniu czknięcia OCR (pusty/uszczuplony
    /// wynik na scenie, która wg detektora się nie zmieniła). W grze ze statycznym obrazem
    /// nic innego nie obudziłoby pętli — bez powtórki przegapiona kwestia przepada na zawsze.
    /// </summary>
    public int MaxWhiffRetries { get; init; } = 2;

    /// <summary>
    /// Bezpiecznik ostateczny dla scen bez ruchu: pełny przebieg OCR co ten interwał,
    /// nawet gdy detektor zmian milczy (łapie zmiany zbyt subtelne dla siatki jasności
    /// oraz czknięcia OCR, które przetrwały powtórki). Zero = wyłączony.
    /// </summary>
    public TimeSpan StaticRescanInterval { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Diagnostyka (tylko narzędzia dev): katalog, do którego trafia PNG klatki,
    /// gdy pełny przebieg OCR zwróci zero bloków mimo wyświetlanej nakładki.
    /// W aplikacji zawsze null — nic nie ląduje na dysku.
    /// </summary>
    public string? DebugFrameDumpDir { get; init; }

    /// <summary>Metryki tylko dla narzedzi dev; aplikacja nie zbiera ani nie zapisuje raportu.</summary>
    public bool EnableDiagnostics { get; init; }

    public double OcrUpscale { get; init; }

    /// <summary>False, gdy profil jawnie wyłączył automatyczne powiększanie małych wycinków.</summary>
    public bool AllowAutoUpscale { get; init; } = true;

    /// <summary>
    /// Wspólny z oknem aplikacji rejestr pokazanych komunikatów nakładki. Sesja dopisuje
    /// do niego własne komunikaty i odrzuca ich odczyty OCR (filtr anty-sprzężeniowy).
    /// </summary>
    public OverlayNoticeEcho? NoticeEcho { get; init; }
}

/// <summary>
/// Tryb live: cykliczne przechwytywanie wybranego okna, tanie wykrywanie zmian
/// (siatka jasności), OCR dopiero po ustabilizowaniu obrazu, tłumaczenie przez
/// aktualny pipeline. OCR i stan sceny obsluguje jedna petla. Do dwoch tlumaczen
/// moze trwac jednoczesnie, gdy zmieni sie widok; probki obrazu pilnuja aktualnosci.
/// Nieaktualny wynik nie wraca
/// na nakladke, nawet jesli odpowiedz dostawcy dociera po zmianie widoku.
/// Pozycje bloków trzymane są względem okna gry — przesunięcie okna bez zmiany
/// treści aktualizuje nakładkę bez ponownego OCR. Zero ingerencji w okno gry.
/// </summary>
public sealed class LiveTranslationSession(
    TranslationOrchestrator orchestrator,
    IOcrProvider ocrProvider,
    IntPtr gameWindowHandle,
    LiveSessionOptions options,
    Action<LiveUpdate> onUpdate,
    ILogger logger) : IDisposable
{
    private const int MaxConsecutiveFailures = 5;

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lifetimeGate = new();
    private bool _disposed;
    private readonly BoundedTranslationWork<IReadOnlyList<TranslationOutcome>> _translationWork = new(2);
    private sealed class SceneSupersededException : Exception;
    private readonly Dictionary<string, LiveOverlayBlock> _displayed = [];
    private sealed record BlockFingerprint(RectPx SourceBox, RectPx FrameRect, TextRegionFingerprint Image);
    private readonly Dictionary<string, BlockFingerprint> _blockFingerprints = [];
    private readonly NoiseAwareChangeDetector _changeDetector = new();
    private byte[]? _gridBuffer;
    private LuminanceGrid? _previousGrid;
    private RectPx? _pendingDirtyRegion;
    private double _peakChangedFraction;
    private TimeSpan _lastProcessedAt;
    private int _whiffRetries;
    private bool _whiffRetryRequested;

    private readonly LiveReadingStabilizer _readings = new();
    private readonly LiveSubtitleContent _subtitleContent = new();
    private bool _readingRetryRequested;

    private const double JitterSimilarityThreshold = 0.5;
    private const double JitterOverlapFraction = 0.5;
    private const double QualityTolerance = 0.1;
    private const double TextureChangeThreshold = 12.0;

    /// <summary>
    /// Duchy: bloki zdjęte z nakładki (OCR gubił je kilka przebiegów z rzędu nad grafiką).
    /// Nowy odczyt w ich miejscu wskrzesza ducha zamiast tworzyć blok od zera z pierwszym
    /// lepszym śmieciowym odczytem i innym rozmiarem — koniec skaczących dymków.
    /// </summary>
    private readonly Dictionary<string, (LiveOverlayBlock Block, TimeSpan DroppedAt)> _ghosts = new(StringComparer.Ordinal);
    private static readonly TimeSpan GhostLifetime = TimeSpan.FromSeconds(10);
    private TimeSpan _cycleTime;
    private RectPx _lastEmittedBounds;
    private bool _warnedAboutScreenFallback;
    private int _consecutiveFailures;
    private readonly LiveSceneValidity _sceneValidity = new(options.SceneCutThreshold, options.MotionThreshold);
    private readonly HashSet<string> _invalidatedTranslations = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshAfterBusyChanges;
    private readonly MotionProcessDeadline _motionDeadline = new(options.MaxMotionPause);
    private Task? _loop;
    private int _diagnosticSceneChecks;
    private double _diagnosticSceneCheckMs;
    private sealed record PendingTextArea(string Key, RectPx Box, int TextRgb, int BackgroundRgb);
    private readonly List<PendingTextArea> _pendingTextAreas = [];
    private bool _pendingTextGone;
    private byte[]? _presenceRowBuffer;

    // Chwila ostatniej zmiany obrazu zauważonej od startu bieżącego przetworzenia (także
    // przez kontrole sceny w trakcie OCR/tłumaczenia). Odświeżenie po zajętym przebiegu
    // liczy okno stabilności od niej, a nie od końca oczekiwania — ForceDirty kazałby
    // napisowi, który pojawił się w trakcie długiego tłumaczenia, czekać drugi raz.
    private TimeSpan? _lastObservedChangeAt;

    // Pomiar „zmiana → napis”: początek zmiany objętej bieżącym przetworzeniem.
    private readonly ChangeToTextTracker _changeToText = new();
    private TimeSpan? _processingChangeOrigin;

    // Klatka porzucona przed pokazaniem (nowa scena, zniknięty tekst): jej zmiana wciąż
    // czeka na napis, więc pomiar „zmiana → napis” biegnie od pierwotnej zmiany.
    private bool _frameDiscarded;

    // Lokalne sprawdzenia porzucone przez zmianę sceny. Nie należą do BoundedTranslationWork,
    // więc pętla czeka na nie osobno — Completion (na które czekają narzędzia dev przed
    // usunięciem danych) nie może się skończyć przy otwartym zapytaniu do bazy.
    private readonly List<Task> _abandonedLocalLookups = [];

    // Komunikaty dla gracza w nakładce: polityka (powtórki, waga, zbiorcze Cache-only)
    // należy do pętli sesji i dostaje jej zegar — bez zegara systemowego.
    private readonly OverlayNoticePolicy _notices = new();

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Pozwala narzedziom dev zaczekac na zakonczenie przed usunieciem ich danych.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    public void Start()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null) return;
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_lifetimeGate)
        {
            if (!_disposed) _cts.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _ = DisposeAfterCompletionAsync(_loop ?? Task.CompletedTask);
        }
    }

    private async Task DisposeAfterCompletionAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception) { /* Completion is observed; the loop reports its own errors. */ }
        _cts.Dispose();
    }

    /// <summary>
    /// Przepuszcza komunikat przez politykę sesji. Przyjęty trafia od razu do filtra
    /// anty-sprzężeniowego — następny OCR nie może wysłać go do dostawcy jako tekstu gry.
    /// </summary>
    private OverlayNotice? AcceptNotice(OverlayNotice? candidate, TimeSpan now)
    {
        if (candidate is null || !_notices.Offer(candidate, now)) return null;
        RememberNotice(candidate);
        return candidate;
    }

    private void RememberNotice(OverlayNotice? notice)
    {
        if (notice is not null) options.NoticeEcho?.Remember(notice.Text);
    }

    private void EmitStopped(string statusLine, Stopwatch clock) =>
        onUpdate(new LiveUpdate(statusLine, ClearOverlay: true, Stopped: true)
        {
            Notice = AcceptNotice(OverlayNotices.LiveStopped(), clock.Elapsed),
        });

    private void Emit(LiveUpdate update, CancellationToken cancellationToken)
    {
        // Po zatrzymaniu sesji żadna aktualizacja nie może już malować po nakładce.
        if (cancellationToken.IsCancellationRequested && !update.Stopped) return;
        onUpdate(update);
    }

    private IReadOnlyList<LiveDisplayBlock> BuildDisplayList(RectPx bounds) =>
        _displayed
            .Select(kv => new LiveDisplayBlock(
                kv.Key,
                kv.Value.WindowRelativeBox.Offset(bounds.X, bounds.Y),
                kv.Value.TranslatedText,
                kv.Value.LineHeight,
                kv.Value.ColorRgb,
                kv.Value.BackgroundRgb,
                kv.Value.OutlineRgb,
                kv.Value.Texture))
            .ToList();

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var stabilizer = new ChangeStabilizer(options.StabilityDelay, options.ForcedProcessInterval);
        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(options.Fps, 0.5, 30.0));
        var wasMinimized = false;

        try
        {
            if (!ocrProvider.IsLanguageAvailable(orchestrator.SourceLanguage))
            {
                EmitStopped(
                    $"Brak pakietu OCR dla języka „{orchestrator.SourceLanguage}” — tryb live zatrzymany.", clock);
                return;
            }

            Emit(new LiveUpdate("Live: start — czekam na pierwszą klatkę.")
            {
                Notice = AcceptNotice(OverlayNotices.LiveStarted(), clock.Elapsed),
            }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var cycleStart = clock.Elapsed;

                if (!NativeMethods.IsWindow(gameWindowHandle))
                {
                    EmitStopped("Okno gry zostało zamknięte — tryb live zatrzymany.", clock);
                    return;
                }

                if (NativeMethods.IsIconic(gameWindowHandle))
                {
                    if (!wasMinimized)
                    {
                        wasMinimized = true;
                        _previousGrid = null;
                        _changeDetector.Reset();
                        _motionDeadline.Reset();
                        _sceneValidity.Reset();
                        _ghosts.Clear();
                        _readings.Clear();
                        _subtitleContent.Clear();
                        _readingRetryRequested = false;
                        _invalidatedTranslations.Clear();
                        stabilizer.Reset();
                        _changeToText.Reset();
                        _lastObservedChangeAt = null;
                        _displayed.Clear();
                        _blockFingerprints.Clear();
                        Emit(new LiveUpdate("Gra zminimalizowana — nakładka ukryta, czekam na powrót.",
                            ClearOverlay: true), cancellationToken);
                    }
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (wasMinimized)
                {
                    wasMinimized = false;
                    stabilizer.ForceDirty(clock.Elapsed);
                }

                try
                {
                    var (changedFraction, strongFraction, significantFraction, processed) = await RunCycleAsync(
                        stabilizer, clock, cancellationToken).ConfigureAwait(false);

                    _consecutiveFailures = 0;
                    if (!processed)
                    {
                        Emit(new LiveUpdate(
                            $"Live: obserwuję (zmiana {changedFraction:P0}, istotne {significantFraction:P0}, mocne {strongFraction:P0}, bloki {_displayed.Count})."),
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Przebudowa pipeline'u (zmiana ustawień w trakcie tłumaczenia) anulowała
                    // epokę — to NIE jest stop sesji. Pomijamy klatkę; następny cykl pójdzie
                    // już przez nowy pipeline. Bez tego rozróżnienia każda zmiana comboboxa
                    // podczas live po cichu zabijała całą pętlę.
                    Emit(new LiveUpdate("Live: ustawienia zmienione w trakcie klatki — wznawiam z nowym pipeline'em."),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    // Pojedyncza czkawka (SQLITE_BUSY, przejściowy błąd WinRT/GDI) nie może
                    // ubić wielogodzinnej sesji — pomijamy klatkę i jedziemy dalej.
                    _consecutiveFailures++;
                    logger.LogWarning(ex, "Błąd cyklu live ({Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        EmitStopped("Tryb live zatrzymany po serii błędów — szczegóły w logu diagnostycznym.", clock);
                        return;
                    }
                    Emit(new LiveUpdate("Live: pominięto klatkę z powodu błędu (szczegóły w logu)."), cancellationToken);
                }

                var now = clock.Elapsed;
                // Preserve low FPS settings; at faster rates, avoid adding a full
                // capture tick after the unchanged stability deadline is reached.
                var remaining = stabilizer.GetPollingDelay(now, interval - (now - cycleStart), interval);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Zatrzymanie przez użytkownika — bez komunikatu o błędzie.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Błąd pętli trybu live");
            EmitStopped("Tryb live zatrzymany przez błąd — szczegóły w logu diagnostycznym.", clock);
        }
        finally
        {
            // Completion includes obsolete provider work. Keep its token alive until
            // all tasks finish, including when the window closes or the loop fails.
            _cts.Cancel();
            await _translationWork.DrainAsync().ConfigureAwait(false);
            await DrainAbandonedLocalLookupsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Jeden cykl: capture → detekcja zmian → (opcjonalnie) OCR + tłumaczenie.</summary>
    private async Task<(double ChangedFraction, double StrongFraction, double SignificantFraction, bool Processed)> RunCycleAsync(
        ChangeStabilizer stabilizer,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        RectPx bounds;
        OcrBitmap? frameForOcr = null;
        var ocrScaleBack = 1.0;
        var ocrRegion = default(RectPx);
        var capturedFrameRect = default(RectPx);
        var partialOcr = false;
        double changedFraction;
        double strongFraction;
        double significantFraction;
        long captureMs;

        var captureStartedTimestamp = Stopwatch.GetTimestamp();
        var sampledAt = clock.Elapsed;
        var captureWatch = Stopwatch.StartNew();
        var (bitmap, usedScreenFallback) = ScreenCapture.CaptureWindowEx(gameWindowHandle);
        using (bitmap)
        {
            captureMs = captureWatch.ElapsedMilliseconds;
            if (bitmap is null)
            {
                return (0, 0, 0, false);
            }
            orchestrator.Latency.Record(LatencyStage.Capture, captureWatch.Elapsed.TotalMilliseconds);

            if (usedScreenFallback && !_warnedAboutScreenFallback)
            {
                _warnedAboutScreenFallback = true;
                logger.LogWarning("Okno gry nie wspiera PrintWindow — tryb live używa zrzutu ekranu (możliwe obce okna w kadrze)");
                Emit(new LiveUpdate(
                    "⚠ To okno wymaga przechwytywania ekranu: fragmenty innych okien nachodzących na grę mogą być tłumaczone. " +
                    "Zamknij poufne okna znad gry albo zatrzymaj tryb live."), cancellationToken);
            }

            bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
            var frameRect = new RectPx(0, 0, bitmap.Width, bitmap.Height);
            capturedFrameRect = frameRect;
            var analysis = ObserveCapturedFrame(bitmap, frameRect, usedScreenFallback, sampledAt, cancellationToken);
            changedFraction = analysis.ChangedFraction;
            strongFraction = analysis.StrongChangedFraction;
            significantFraction = analysis.SignificantFraction;

            // Zmieniona klatka = zmiana ISTOTNA (stałe migotanie tła nie liczy się) —
            // dzięki temu region do OCR obejmuje tylko nowy tekst, a nie cały ekran.
            var frameChanged = analysis.SignificantFraction > options.ChangeThreshold;
            var isMoving = analysis.StrongChangedFraction >= options.MotionThreshold;
            var motionNow = clock.Elapsed;
            var forceProcess = _motionDeadline.Observe(isMoving, motionNow);

            // Scena w ruchu (bieg, przesuw kamery): każdy wynik OCR wylądowałby w miejscu,
            // z którego tekst już odpłynął. Ruch poznajemy po MOCNYCH zmianach pikseli —
            // falująca mgła/pogoda zmienia komórki subtelnie i ruchem nie jest.
            if (isMoving)
            {
                _pendingDirtyRegion = frameRect;

                if (!forceProcess)
                {
                    stabilizer.ForceDirty(motionNow);
                    return (changedFraction, strongFraction, significantFraction, _sceneValidity.MotionSamples >= 2);
                }

                // Deadline zostanie odnowiony dopiero, gdy klatka rzeczywiscie
                // trafi do OCR; blad przygotowania obrazu nie kupuje nowej pauzy.
            }

            if (frameChanged && analysis.SignificantRegion is { } changedNow)
            {
                // Zmiany kumulują się między klatkami (animacja pojawiania tooltipa) —
                // do OCR pójdzie unia wszystkiego, co się zmieniło od ostatniego przetworzenia.
                _pendingDirtyRegion = _pendingDirtyRegion?.Union(changedNow) ?? changedNow;
            }

            // Bezpiecznik scen statycznych: bez niego zmiana zbyt subtelna dla siatki
            // (albo czknięcie OCR bez kolejnych zmian obrazu) nigdy nie doczekałaby się
            // ponownego przebiegu — w grze bez szumu tła pętla potrafi milczeć minutami.
            var heartbeatDue = options.StaticRescanInterval > TimeSpan.Zero
                && clock.Elapsed - _lastProcessedAt >= options.StaticRescanInterval;

            var shouldProcess = stabilizer.Update(frameChanged, clock.Elapsed) || forceProcess || heartbeatDue;
            if (forceProcess)
            {
                stabilizer.Reset();
            }
            if (shouldProcess)
            {
                ocrRegion = frameRect;
                var dirty = _pendingDirtyRegion;
                _pendingDirtyRegion = null;

                if (dirty is { } dirtyRegion)
                {
                    // Region rozszerzamy o zapas i o wyświetlane bloki, które na niego
                    // nachodzą — do PUNKTU STAŁEGO: unia z blokiem może dosunąć region do
                    // kolejnego bloku (łańcuch), a blok objęty tylko częściowo zostałby
                    // ucięty w OCR i zdublowany na nakładce.
                    var expanded = dirtyRegion.Inflate(24).Intersect(frameRect);
                    bool grew;
                    do
                    {
                        grew = false;
                        foreach (var displayed in _displayed.Values)
                        {
                            if (displayed.WindowRelativeBox.IntersectsWith(expanded))
                            {
                                var union = expanded.Union(displayed.WindowRelativeBox);
                                if (union != expanded)
                                {
                                    expanded = union;
                                    grew = true;
                                }
                            }
                        }
                    } while (grew);
                    expanded = expanded.Intersect(frameRect);

                    // Częściowy OCR opłaca się tylko dla wyraźnie mniejszego wycinka.
                    if (!expanded.IsEmpty
                        && (long)expanded.Width * expanded.Height * 2 <= (long)bitmap.Width * bitmap.Height)
                    {
                        ocrRegion = expanded;
                        partialOcr = true;
                    }
                }

                using var crop = partialOcr
                    ? bitmap.Clone(
                        new System.Drawing.Rectangle(ocrRegion.X, ocrRegion.Y, ocrRegion.Width, ocrRegion.Height),
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                    : null;
                var source = crop ?? bitmap;

                // Powiększanie z profilu służy małym wycinkom — skalowanie całej klatki
                // 1080p do 4K kosztowałoby sekundę+ na każde przetworzenie.
                var preferredUpscale = source.Width >= 1000 || source.Height >= 700 ? 0.0 : options.OcrUpscale;
                var downscale = OcrScaling.ComputeDownscale(source.Width, source.Height, ocrProvider.MaxImageDimension);
                var factor = downscale < 1.0
                    ? downscale
                    : OcrScaling.ComputeUpscale(
                        source.Width, source.Height, ocrProvider.MaxImageDimension, preferredUpscale, options.AllowAutoUpscale);

                if (Math.Abs(factor - 1.0) > 0.001)
                {
                    using var scaled = ScreenCapture.Rescale(source, factor);
                    frameForOcr = ScreenCapture.ToOcrBitmap(scaled);
                    ocrScaleBack = 1.0 / factor;
                }
                else
                {
                    frameForOcr = ScreenCapture.ToOcrBitmap(source);
                }
            }
        }

        if (frameForOcr is not null)
        {
            var peakChanged = _peakChangedFraction;
            _peakChangedFraction = 0;
            _lastProcessedAt = clock.Elapsed;
            _motionDeadline.OnProcessingStarted(_lastProcessedAt);
            _cycleTime = clock.Elapsed;
            // Ta klatka obejmuje wszystkie zmiany zauważone do teraz; liczą się już
            // tylko te, które przyjdą w trakcie jej OCR i tłumaczenia.
            _lastObservedChangeAt = null;
            _processingChangeOrigin = _changeToText.BeginProcessing();
            _frameDiscarded = false;
            var frameCompleted = false;
            try
            {
                await ProcessFrameAsync(frameForOcr, ocrScaleBack, ocrRegion, partialOcr, capturedFrameRect, captureMs, peakChanged,
                        captureStartedTimestamp, usedScreenFallback, clock, cancellationToken)
                    .ConfigureAwait(false);
                frameCompleted = !_frameDiscarded;
            }
            finally
            {
                _pendingTextAreas.Clear();
                _pendingTextGone = false;
                var changeOrigin = _processingChangeOrigin;
                _processingChangeOrigin = null;
                // Busy samples also advance the detector when OCR/provider fails
                // or settings cancel its epoch. Do not lose their pending change.
                if (_whiffRetryRequested || _refreshAfterBusyChanges || _readingRetryRequested)
                {
                    var retryNow = clock.Elapsed;
                    var rereadRequested = _whiffRetryRequested || _readingRetryRequested;
                    if (rereadRequested)
                    {
                        // Celowe drugie czytanie: pełne okno stabilności PO tym przebiegu
                        // (patrz ChangeStabilizerPollingTests) — tego nie skracamy.
                        stabilizer.ForceDirty(retryNow);
                    }
                    else
                    {
                        // Samo odświeżenie po zmianach widzianych w trakcie pracy: okno
                        // stabilności liczy się od zauważonej zmiany, nie od końca czekania.
                        stabilizer.MarkDirty(_lastObservedChangeAt ?? retryNow, retryNow);
                    }
                    _readingRetryRequested = false;
                    _whiffRetryRequested = false;
                    _refreshAfterBusyChanges = false;
                    // Ponowny odczyt tej samej zmiany (powtórka, porzucona klatka): pomiar
                    // „zmiana → napis” biegnie dalej od pierwotnej zmiany. Klatka ukończona bez
                    // napisu swoją zmianę obsłużyła — nowa linia liczy się od własnej próbki.
                    _changeToText.FinishProcessing(changeOrigin, frameCompleted, rereadRequested);
                }
            }
            return (changedFraction, strongFraction, significantFraction, true);
        }

        // Okno przesunęło się bez zmiany treści — przeliczamy pozycje bloków bez OCR.
        if (_displayed.Count > 0 && bounds != _lastEmittedBounds)
        {
            _lastEmittedBounds = bounds;
            Emit(new LiveUpdate(
                $"Live: okno gry przesunięte — aktualizuję pozycje ({_displayed.Count} bloków).",
                BuildDisplayList(bounds),
                SubtitleText: null,
                WindowBounds: bounds), cancellationToken);
            return (changedFraction, strongFraction, significantFraction, true);
        }

        return (changedFraction, strongFraction, significantFraction, false);
    }


    /// <summary>
    /// Shared by normal capture and checks made while OCR/translation is pending.
    /// All calls run on the same session loop: no concurrent access to scene state.
    /// </summary>
    private NoiseAwareAnalysis ObserveCapturedFrame(
        System.Drawing.Bitmap bitmap, RectPx frameRect, bool usedScreenFallback, TimeSpan sampledAt,
        CancellationToken cancellationToken)
    {
        var grid = ScreenCapture.ComputeLuminanceGrid(bitmap, ref _gridBuffer);
        var hasPrevious = _previousGrid is not null;
        var analysis = _previousGrid is { } previous
            ? _changeDetector.Analyze(previous, grid, bitmap.Width, bitmap.Height)
            : new NoiseAwareAnalysis(1.0, 0.0, 1.0, frameRect);
        _previousGrid = grid;
        _peakChangedFraction = Math.Max(_peakChangedFraction, analysis.ChangedFraction);

        var sceneCut = _sceneValidity.Observe(analysis, hasPrevious);
        var significant = analysis.SignificantFraction > options.ChangeThreshold;
        if (sceneCut || significant)
        {
            // Czas próbki, nie jej przetworzenia: od niej gracz widzi nowy obraz.
            _lastObservedChangeAt = sampledAt;
            _changeToText.ObserveChange(sampledAt);
        }
        if (sceneCut)
        {
            // The generation still changes, so pending old OCR/provider work is
            // discarded. Only already displayed regions with exact native RGB
            // evidence may stay. Screen fallback can contain our own overlay.
            InvalidateScene(cancellationToken, usedScreenFallback ? null : bitmap);
            _pendingDirtyRegion = frameRect;
        }
        if (significant && analysis.SignificantRegion is { } changed)
            _pendingDirtyRegion = _pendingDirtyRegion?.Union(changed) ?? changed;
        return analysis;
    }

    private void InvalidateScene(CancellationToken cancellationToken, System.Drawing.Bitmap? current = null)
    {
        _ghosts.Clear();
        _readings.Clear();
        _readingRetryRequested = false;
        _whiffRetries = 0;
        _whiffRetryRequested = false;

        if (current is not null && _displayed.Count > 0)
        {
            var frameRect = new RectPx(0, 0, current.Width, current.Height);
            var removedKeys = _displayed.Keys.Where(key =>
                !_blockFingerprints.TryGetValue(key, out var reference)
                || reference.FrameRect != frameRect
                || !reference.Image.Matches(ScreenCapture.ComputeTextFingerprint(
                    current, reference.SourceBox, ref _presenceRowBuffer))).ToArray();
            if (removedKeys.Length < _displayed.Count)
            {
                RemoveLocalBlocks(removedKeys, cancellationToken);
                var bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
                if (bounds != _lastEmittedBounds)
                {
                    _lastEmittedBounds = bounds;
                    Emit(new LiveUpdate("Live: aktualizuję pozycje zachowanych napisów.",
                        BuildDisplayList(bounds), WindowBounds: bounds), cancellationToken);
                }
                return;
            }
        }

        // A fallback bitmap can contain our overlay from before this clear.
        // Preserve the anti-feedback filter through the next fresh read.
        _invalidatedTranslations.UnionWith(_displayed.Values.Select(static b => b.NormalizedTranslation));
        _displayed.Clear();
        _blockFingerprints.Clear();
        _subtitleContent.Clear();
        // Subtitle presentation can outlive an empty block list. Always clear both
        // layers; MainWindow preserves the user's explicit hidden-state choice.
        Emit(new LiveUpdate("Live: zmiana widoku — usuwam nieaktualne napisy.",
            ClearOverlay: true), cancellationToken);
    }

    private async Task<T> AwaitWithSceneChecksAsync<T>(
        Task<T> operation, Stopwatch clock, CancellationToken cancellationToken,
        long? abandonGeneration = null, long? ocrStartedTimestamp = null)
    {
        void EnsureCurrent()
        {
            if (abandonGeneration is { } generation && (!_sceneValidity.IsCurrent(generation) || _pendingTextGone))
                throw new SceneSupersededException();
        }

        EnsureCurrent();
        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(options.Fps, 0.5, 30.0));
        // A short OCR often finishes just after the first capture tick. Give it
        // half a tick more before a periodic check, then still check AFTER completion
        // whenever it took at least one normal interval. This avoids capture both
        // before and after that short operation without publishing an unchecked result.
        var nextCheckDelay = ocrStartedTimestamp is { } started
            ? OcrSceneCheckTiming.FirstCheckDelay(interval) - Stopwatch.GetElapsedTime(started)
            : interval;
        if (nextCheckDelay < TimeSpan.Zero) nextCheckDelay = TimeSpan.Zero;
        bool NeedsCompletionCheck(bool periodicCheck) => ocrStartedTimestamp is { } ocrStarted
            ? OcrSceneCheckTiming.RequiresCompletionCheck(Stopwatch.GetElapsedTime(ocrStarted), interval, periodicCheck)
            : periodicCheck;
        // Cache hits and fast OCR retain the path with no additional capture.
        if (operation.IsCompleted && !NeedsCompletionCheck(false))
            return await operation.ConfigureAwait(false);
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var checkedScene = false;
        try
        {
            while (!operation.IsCompleted)
            {
                var delay = Task.Delay(nextCheckDelay, polling.Token);
                nextCheckDelay = interval;
                if (await Task.WhenAny(operation, delay).ConfigureAwait(false) == operation) break;
                await delay.ConfigureAwait(false);
                if (operation.IsCompleted) break;
                try { CheckSceneWhileBusy(clock, cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    InvalidateUnavailableScene(cancellationToken);
                    throw;
                }
                checkedScene = true;
                EnsureCurrent();
            }
            var result = await operation.ConfigureAwait(false);
            // Close the gap between the last periodic check and a slow response.
            if (NeedsCompletionCheck(checkedScene))
            {
                try { CheckSceneWhileBusy(clock, cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    InvalidateUnavailableScene(cancellationToken);
                    throw;
                }
            }
            EnsureCurrent();
            return result;
        }
        catch (SceneSupersededException)
        {
            // The bounded owner observes/drains translation tasks. Only this frame's
            // wait ends: the paid request can still fill cache and serve a later view.
            throw;
        }
        catch
        {
            // Do not leave a faulting provider/OCR task unobserved if capture fails.
            // Cancellation is propagated through the same token passed to operation.
            try { await operation.ConfigureAwait(false); }
            catch (Exception) { /* The original failure is rethrown below. */ }
            throw;
        }
        finally
        {
            polling.Cancel();
        }
    }

    /// <summary>
    /// Lokalne sprawdzenie klatki (bez dostawcy) z tymi samymi kontrolami sceny co
    /// tłumaczenie: przy wolnej bazie nieaktualna klatka też jest porzucana.
    /// </summary>
    private async Task<IReadOnlyList<TranslationOutcome?>> TranslateLocalWithSceneChecksAsync(
        IReadOnlyList<string> texts, long generation, Stopwatch clock, CancellationToken cancellationToken)
    {
        var lookup = orchestrator.TranslateLocalAsync(texts, cancellationToken);
        try
        {
            return await AwaitWithSceneChecksAsync(lookup, clock, cancellationToken, generation).ConfigureAwait(false);
        }
        catch (SceneSupersededException)
        {
            // W odróżnieniu od tłumaczeń nie ma tu właściciela (BoundedTranslationWork),
            // który by je obserwował — porzucone sprawdzenie nie może zostawić
            // nieobserwowanego wyjątku (np. anulowanej epoki pipeline'u).
            _ = lookup.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _abandonedLocalLookups.RemoveAll(static t => t.IsCompleted);
            _abandonedLocalLookups.Add(lookup);
            throw;
        }
    }

    private async Task DrainAbandonedLocalLookupsAsync()
    {
        foreach (var lookup in _abandonedLocalLookups)
        {
            try
            {
                await lookup.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Wynik porzuconego sprawdzenia nikogo nie interesuje — liczy się tylko,
                // że skończyło pracę na bazie przed Completion.
            }
        }
        _abandonedLocalLookups.Clear();
    }

    private void InvalidateUnavailableScene(CancellationToken cancellationToken)
    {
        _sceneValidity.Reset();
        InvalidateScene(cancellationToken);
        _previousGrid = null;
        _changeDetector.Reset();
        _motionDeadline.Reset();
        _refreshAfterBusyChanges = true;
    }


    private void CheckSceneWhileBusy(Stopwatch clock, CancellationToken cancellationToken)
    {
        var started = options.EnableDiagnostics ? Stopwatch.GetTimestamp() : 0;
        try { CheckSceneWhileBusyCore(clock, cancellationToken); }
        finally
        {
            if (options.EnableDiagnostics)
            {
                _diagnosticSceneChecks++;
                _diagnosticSceneCheckMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
    }

    private static async Task<T> ObserveOperationTimeAsync<T>(Task<T> operation, long started, Action<double> completed)
    {
        try { return await operation.ConfigureAwait(false); }
        finally { completed(Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private void CheckSceneWhileBusyCore(Stopwatch clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.IsWindow(gameWindowHandle) || NativeMethods.IsIconic(gameWindowHandle))
        {
            InvalidateUnavailableScene(cancellationToken);
            return;
        }
        var sampledAt = clock.Elapsed;
        var (bitmap, usedScreenFallback) = ScreenCapture.CaptureWindowEx(gameWindowHandle);
        using (bitmap)
        {
            if (bitmap is null)
            {
                InvalidateUnavailableScene(cancellationToken);
                return;
            }
            if (usedScreenFallback && !_warnedAboutScreenFallback)
            {
                _warnedAboutScreenFallback = true;
                logger.LogWarning("Kontrola sceny wymaga przechwytywania ekranu");
                Emit(new LiveUpdate(
                    "⚠ To okno wymaga przechwytywania ekranu: inne okna nad grą mogą znaleźć się w kadrze."),
                    cancellationToken);
            }
            var frameRect = new RectPx(0, 0, bitmap.Width, bitmap.Height);
            var analysis = ObserveCapturedFrame(bitmap, frameRect, usedScreenFallback, sampledAt, cancellationToken);
            // These samples advance _previousGrid. Tell the normal stabilizer about
            // changes seen only while busy, or a small new dialog could wait for the
            // four-second rescan even though we already observed its appearance.
            if (analysis.SignificantFraction > options.ChangeThreshold)
                _refreshAfterBusyChanges = true;
            _motionDeadline.Observe(analysis.StrongChangedFraction >= options.MotionThreshold, clock.Elapsed);
            if (_pendingTextAreas.Count > 0)
            {
                var covered = _pendingTextAreas.Where(area => ScreenCapture.IsTextAreaClearlyEmpty(
                    bitmap, area.Box, area.TextRgb, area.BackgroundRgb, ref _presenceRowBuffer)).ToList();
                if (covered.Count > 0)
                {
                    _pendingTextGone = true;
                    _refreshAfterBusyChanges = true;
                    // Zniknięcie tekstu to zmiana obrazu z chwili tej próbki.
                    _lastObservedChangeAt = sampledAt;
                    // The whole unpublished frame is discarded. Retry all its candidate
                    // areas, including unchanged menu text that has not appeared yet.
                    foreach (var area in _pendingTextAreas)
                        _pendingDirtyRegion = _pendingDirtyRegion?.Union(area.Box) ?? area.Box;
                    RemoveLocalBlocks(covered.Select(static area => area.Key), cancellationToken);
                }
            }
        }
    }

    private void RemoveLocalBlocks(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var keyList = keys.ToArray();
        var updatedSubtitle = _subtitleContent.Remove(keyList);
        var removed = false;
        foreach (var key in keyList)
        {
            if (_displayed.Remove(key, out var old))
            {
                _invalidatedTranslations.Add(old.NormalizedTranslation);
                removed = true;
            }
            _ghosts.Remove(key);
            _blockFingerprints.Remove(key);
            _readings.Reset(key);
        }
        if (!removed && updatedSubtitle is null) return;
        var bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
        _lastEmittedBounds = bounds;
        Emit(new LiveUpdate("Live: usuwam zastąpiony napis.", BuildDisplayList(bounds),
                SubtitleText: updatedSubtitle, WindowBounds: bounds)
            {
                ClearSubtitle = updatedSubtitle is { Length: 0 },
                PreserveSubtitleLifetime = updatedSubtitle is not null,
            }, cancellationToken);
    }

    private static RectPx ToSampleBox(RectPx windowBox, RectPx region, double scaleBack)
    {
        // Include all edge pixels. An inward-rounded crop could miss a thin glyph
        // and incorrectly identify its box as a uniform covering panel.
        var x = (int)Math.Floor((windowBox.X - region.X) / scaleBack);
        var y = (int)Math.Floor((windowBox.Y - region.Y) / scaleBack);
        var right = (int)Math.Ceiling((windowBox.Right - region.X) / scaleBack);
        var bottom = (int)Math.Ceiling((windowBox.Bottom - region.Y) / scaleBack);
        return new RectPx(x, y, right - x, bottom - y);
    }

    private static double Luminance(int rgb) =>
        0.299 * ((rgb >> 16) & 0xFF) + 0.587 * ((rgb >> 8) & 0xFF) + 0.114 * (rgb & 0xFF);

    /// <summary>
    /// Blok z puli zajmujący to samo miejsce co nowy odczyt (nachodzenie co najmniej
    /// w połowie mniejszego z boxów). Blok już przejęty w tym przebiegu nie liczy się.
    /// </summary>
    private static (string Key, LiveOverlayBlock Block)? FindOverlapping(
        KeyedTextBlock candidate,
        IReadOnlyDictionary<string, LiveOverlayBlock> pool,
        IReadOnlyDictionary<string, LiveOverlayBlock> alreadyReused)
    {
        var box = candidate.Block.Box;
        long area = (long)box.Width * box.Height;
        if (area <= 0) return null;

        (string Key, LiveOverlayBlock Block)? best = null;
        long bestOverlap = 0;
        foreach (var (key, displayed) in pool)
        {
            if (alreadyReused.ContainsKey(key) || displayed.SourceText.Length == 0) continue;
            var overlap = box.Intersect(displayed.WindowRelativeBox);
            if (overlap.IsEmpty) continue;
            long displayedArea = (long)displayed.WindowRelativeBox.Width * displayed.WindowRelativeBox.Height;
            var overlapArea = (long)overlap.Width * overlap.Height;
            if (overlapArea < Math.Min(area, displayedArea) * JitterOverlapFraction) continue;
            if (overlapArea > bestOverlap)
            {
                bestOverlap = overlapArea;
                best = (key, displayed);
            }
        }
        return best;
    }

    private async Task ProcessFrameAsync(
        OcrBitmap frame, double scaleBack, RectPx ocrRegion, bool partialOcr, RectPx capturedFrameRect,
        long captureMs, double peakChangedFraction, long captureStartedTimestamp,
        bool usedScreenFallback, Stopwatch clock, CancellationToken cancellationToken)
    {
        var generation = _sceneValidity.Generation;
        var ocrWatch = Stopwatch.StartNew();
        var ocrChecksBefore = _diagnosticSceneChecks;
        var ocrCheckMsBefore = _diagnosticSceneCheckMs;
        double? ocrOperationMs = null;
        var ocrStarted = Stopwatch.GetTimestamp();
        var ocrTask = ocrProvider.RecognizeAsync(frame, orchestrator.SourceLanguage, cancellationToken);
        if (options.EnableDiagnostics)
            ocrTask = ObserveOperationTimeAsync(ocrTask, ocrStarted, elapsed => ocrOperationMs = elapsed);
        var ocrResult = await AwaitWithSceneChecksAsync(ocrTask, clock, cancellationToken, ocrStartedTimestamp: ocrStarted).ConfigureAwait(false);
        var ocrMs = ocrWatch.ElapsedMilliseconds;
        orchestrator.Latency.Record(LatencyStage.Ocr, ocrWatch.Elapsed.TotalMilliseconds);
        var ocrSceneChecks = _diagnosticSceneChecks - ocrChecksBefore;
        var ocrSceneCheckMs = _diagnosticSceneCheckMs - ocrCheckMsBefore;
        if (!_sceneValidity.IsCurrent(generation))
        {
            _refreshAfterBusyChanges = true;
            _frameDiscarded = true;
            return;
        }
        var rawLineCount = ocrResult.Lines.Count;

        var lines = ocrResult.Lines;
        if (Math.Abs(scaleBack - 1.0) > 0.001)
        {
            lines = lines
                .Select(line => new OcrLine(
                    line.Text,
                    line.Box.Scale(scaleBack),
                    line.Words.Select(w => new OcrWord(w.Text, w.Box.Scale(scaleBack))).ToList()))
                .ToList();
        }

        // Współrzędne OCR są względem wycinka — przenosimy je na układ okna gry.
        if (partialOcr && (ocrRegion.X != 0 || ocrRegion.Y != 0))
        {
            lines = lines
                .Select(line => new OcrLine(
                    line.Text,
                    line.Box.Offset(ocrRegion.X, ocrRegion.Y),
                    line.Words.Select(w => new OcrWord(w.Text, w.Box.Offset(ocrRegion.X, ocrRegion.Y))).ToList()))
                .ToList();
        }

        // Komunikat nakładki odfiltrowujemy już na poziomie linii: zgrupowany z tekstem gry
        // („⚠ Brak klucza DeepL” nad „Chapter 3”) przestałby przypominać komunikat i poszedłby
        // do dostawcy. Sprawdzenie na blokach niżej zostaje jako druga ochrona.
        if (options.NoticeEcho is { } lineEcho) lines = lineEcho.RemoveEchoLines(lines);

        var blocks = TextBlockGrouper.Group(lines)
            .Where(static block => JunkFilter.IsMeaningful(block.Text))
            .ToList();
        var keyed = LiveBlockKeyer.AssignKeys(blocks);

        // Diagnostyka whiffów OCR (tylko narzędzia dev): pełny przebieg nie widzi NIC,
        // choć nakładka ma bloki — zapisujemy klatkę, żeby odróżnić zepsuty capture
        // od czknięcia silnika OCR.
        if (options.DebugFrameDumpDir is { Length: > 0 } dumpDir
            && !partialOcr && keyed.Count == 0 && _displayed.Count >= 3)
        {
            try
            {
                Directory.CreateDirectory(dumpDir);
                var dumpPath = Path.Combine(dumpDir, $"whiff-{DateTime.Now:HHmmss-fff}.png");
                ScreenCapture.SavePng(frame, dumpPath);
                Emit(new LiveUpdate(
                    $"Live: diagnostyka — pusty wynik OCR ({rawLineCount} linii surowych), zrzut: {dumpPath}"),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Runtime.InteropServices.ExternalException or ArgumentException)
            {
                // GDI+ zgłasza błędy zapisu (pełny dysk, enkoder) jako ExternalException —
                // diagnostyka nie może ubić diagnozowanej sesji licznikiem awarii.
                logger.LogWarning(ex, "Nie udało się zapisać diagnostycznego zrzutu klatki");
            }
        }

        // Ochrona przed pętlą sprzężenia (gdy wykluczenie nakładki z przechwytywania
        // zawiedzie): blok, którego tekst jest naszym własnym wyświetlanym tłumaczeniem
        // albo komunikatem nakładki („⚠ Brak klucza DeepL”), nie wraca do tłumaczenia —
        // komunikat wysłany do dostawcy kosztowałby znaki i wrócił jako „napis z gry”.
        var displayedTranslations = _displayed.Values
            .Select(static d => d.NormalizedTranslation)
            .Concat(_invalidatedTranslations)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Consume only entries available to this fresh OCR. Local removals later
        // in this frame must survive to protect the next capture from overlay feedback.
        _invalidatedTranslations.Clear();
        var noticeEcho = options.NoticeEcho;
        keyed = keyed
            .Where(k => !displayedTranslations.Contains(k.NormalizedText)
                && noticeEcho?.IsEcho(k.NormalizedText) != true)
            .ToList();

        // Stabilizacja odczytów: nad ruchomą/zajętą grafiką OCR czyta ten sam napis za
        // każdym razem trochę inaczej („Last Played: 06.08.2026” / „Lasi Played: 06.0840261”).
        // Każdy wariant to nowy klucz, nowe zapytanie do API i nowy dymek — miganie.
        // Blok podobny do już wyświetlanego w tym samym miejscu przejmuje jego tłumaczenie;
        // nowy odczyt zastępuje stary dopiero, gdy powtórzy się (prawdziwa zmiana treści
        // jest stabilna, drżenie OCR — nie).
        var reused = new Dictionary<string, LiveOverlayBlock>(StringComparer.Ordinal);
        // Nowy odczyt zastępujący stary dziedziczy jego styl (rozmiar, box, kolory) —
        // podmiana ma zmieniać tekst, nigdy wygląd dymka.
        var inheritFrom = new Dictionary<string, LiveOverlayBlock>(StringComparer.Ordinal);
        var accepted = new List<KeyedTextBlock>(keyed.Count);
        var observedReadingKeys = new HashSet<string>(StringComparer.Ordinal);
        var replacedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in keyed)
        {
            if (_displayed.ContainsKey(candidate.Key))
            {
                observedReadingKeys.Add(candidate.Key);
                _readings.Reset(candidate.Key);
                accepted.Add(candidate);
                continue;
            }

            var match = FindOverlapping(candidate, _displayed, reused);
            if (match is null)
            {
                // Miejsce po duchu: podobny albo brudniejszy odczyt wskrzesza ducha
                // (jego tłumaczenie i styl), czysta nowa treść dziedziczy tylko styl.
                var ghost = FindOverlapping(candidate, _ghosts.ToDictionary(g => g.Key, g => g.Value.Block), reused);
                if (ghost is { } g)
                {
                    _ghosts.Remove(g.Key);
                    var ghostSimilarity = TextSimilarity.Ratio(candidate.NormalizedText, g.Block.SourceText);
                    var quality = ReadingQuality.Score(candidate.NormalizedText);
                    var ghostQuality = ReadingQuality.Score(g.Block.SourceText);
                    if (ghostSimilarity >= JitterSimilarityThreshold || quality < ghostQuality - QualityTolerance)
                    {
                        reused[g.Key] = g.Block with { Misses = 0 };
                        continue;
                    }
                    inheritFrom[candidate.Key] = g.Block;
                }
                accepted.Add(candidate);
                continue;
            }

            var (oldKey, old) = match.Value;
            observedReadingKeys.Add(oldKey);
            var decision = _readings.Observe(oldKey, old.SourceText, candidate.NormalizedText);
            if (decision == LiveReadingDecision.Replace)
            {
                replacedKeys.Add(oldKey);
                inheritFrom[candidate.Key] = old;
                accepted.Add(candidate);
                continue;
            }
            if (decision == LiveReadingDecision.Confirm)
            {
                // One prompt OCR confirmation of a plausible change. Do not wait
                // for the four-second static scan, and do not send this candidate yet.
                _readingRetryRequested = true;
                var confirmationRegion = candidate.Block.Box.Union(old.WindowRelativeBox);
                _pendingDirtyRegion = _pendingDirtyRegion?.Union(confirmationRegion) ?? confirmationRegion;
            }
            reused[oldKey] = old with { Misses = 0 };
        }
        // A missing observation breaks the streak only inside the scanned area;
        // a partial OCR elsewhere is not evidence about an untouched block.
        foreach (var (key, displayed) in _displayed)
        {
            if ((!partialOcr || displayed.WindowRelativeBox.IntersectsWith(ocrRegion))
                && !observedReadingKeys.Contains(key))
                _readings.Reset(key);
        }
        keyed = accepted;

        // A confirmed replacement, or missing OCR over a now-uniform former text
        // box, is evidence that the old label has ended. Remove it before waiting
        // for translation, without clearing unrelated menu entries. Texture or an
        // incomplete crop remains uncertain and keeps the normal OCR miss grace.
        var acceptedKeys = keyed.Select(static k => k.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, old) in _displayed)
        {
            if (observedReadingKeys.Contains(key) || acceptedKeys.Contains(key) || reused.ContainsKey(key)) continue;
            if (old.WindowRelativeBox.Intersect(ocrRegion) != old.WindowRelativeBox) continue;
            var oldSample = ToSampleBox(old.WindowRelativeBox, ocrRegion, scaleBack);
            if (TextPresenceProbe.IsClearlyEmpty(frame, oldSample, old.ColorRgb, old.BackgroundRgb))
                replacedKeys.Add(key);
        }
        replacedKeys.ExceptWith(acceptedKeys);
        foreach (var key in replacedKeys) reused.Remove(key);
        RemoveLocalBlocks(replacedKeys, cancellationToken);

        // These are the same samples used for rendering below. Preparing them now
        // also lets pending work verify that its source text has not been covered.
        var sampledStyles = keyed.Select(k =>
        {
            var box = k.Block.Box;
            // Keep the existing rendering sample geometry; absence evidence below
            // uses the separately outward-rounded full box to protect edge glyphs.
            var sampleBox = new RectPx(
                (int)((box.X - ocrRegion.X) / scaleBack),
                (int)((box.Y - ocrRegion.Y) / scaleBack),
                Math.Max(1, (int)(box.Width / scaleBack)),
                Math.Max(1, (int)(box.Height / scaleBack)));
            return BlockColorSampler.SampleColors(frame.PixelsBgra32, frame.Width, frame.Height, frame.Stride, sampleBox);
        }).ToList();
        for (var i = 0; i < keyed.Count; i++)
        {
            var colors = sampledStyles[i];
            _pendingTextAreas.Add(new PendingTextArea(keyed[i].Key, keyed[i].Block.Box,
                colors.TextRgb, colors.BackgroundRgb));
        }

        var translateWatch = Stopwatch.StartNew();
        var translationChecksBefore = _diagnosticSceneChecks;
        var translationCheckMsBefore = _diagnosticSceneCheckMs;
        IReadOnlyList<TranslationOutcome> outcomes;
        try
        {
            if (keyed.Count == 0)
            {
                // Retained readings need no translation slot. Finish this frame so
                // a confirmation OCR cannot be held up by obsolete provider work.
                outcomes = [];
            }
            else
            {
                // A scene check during OCR may already have seen a local change.
                // Verify its accepted areas before sending that now-obsolete text.
                if (_refreshAfterBusyChanges && _pendingTextAreas.Count > 0)
                {
                    CheckSceneWhileBusy(clock, cancellationToken);
                    if (_pendingTextGone || !_sceneValidity.IsCurrent(generation))
                        throw new SceneSupersededException();
                }
                var texts = keyed.Select(static k => k.Block.Text).ToList();
                var local = await TranslateLocalWithSceneChecksAsync(texts, generation, clock, cancellationToken)
                    .ConfigureAwait(false);
                if (local.All(static o => o is not null))
                {
                    // Cała klatka jest znana (korekty, słownik, cache): bez miejsca w kolejce
                    // tłumaczeń, więc wolne zapytanie do dostawcy ze starszej klatki nie
                    // wstrzymuje napisów, które mamy już lokalnie. Częściowo znanej klatki
                    // nie publikujemy dwuetapowo — idzie zwykłą ścieżką poniżej.
                    outcomes = local.Select(static o => o!).ToList();
                }
                else
                {
                    // Wynik próby trafia do tłumaczenia: znane teksty nie są szukane w bazie
                    // drugi raz (podwójny licznik użyć i zbędne zapytanie na gorącej ścieżce).
                    Task<IReadOnlyList<TranslationOutcome>>? translation;
                    while (!_translationWork.TryStart(
                               () => orchestrator.TranslateTextsAsync(texts, local, cancellationToken),
                               out translation))
                    {
                        // No FIFO of old frames. If both slots are occupied, watch the scene
                        // while waiting; a superseded frame is discarded before sending text.
                        await AwaitWithSceneChecksAsync(_translationWork.WaitForCapacityAsync(),
                            clock, cancellationToken, generation).ConfigureAwait(false);
                    }
                    outcomes = await AwaitWithSceneChecksAsync(translation,
                        clock, cancellationToken, generation).ConfigureAwait(false);
                }
            }
        }
        catch (SceneSupersededException)
        {
            _refreshAfterBusyChanges = true;
            _frameDiscarded = true;
            return;
        }
        var translateMs = translateWatch.ElapsedMilliseconds;

        if (cancellationToken.IsCancellationRequested) return;
        if (!_sceneValidity.IsCurrent(generation))
        {
            // The provider may fill its cache, but no block/style/survivor from the
            // obsolete frame is allowed back onto the overlay.
            _refreshAfterBusyChanges = true;
            _frameDiscarded = true;
            return;
        }

        var freshKeys = new List<string>();
        var claimedBoxes = new List<RectPx>();
        var next = new Dictionary<string, LiveOverlayBlock>();
        var nextFingerprints = new Dictionary<string, BlockFingerprint>(_blockFingerprints);

        // Przy częściowym OCR bloki spoza przetworzonego regionu zostają bez zmian.
        if (partialOcr)
        {
            foreach (var (key, displayed) in _displayed)
            {
                if (!displayed.WindowRelativeBox.IntersectsWith(ocrRegion))
                {
                    next[key] = displayed;
                }
            }
        }

        for (var i = 0; i < keyed.Count; i++)
        {
            var outcome = outcomes[i];
            if (outcome.TranslatedText is not { } translated) continue;

            // Kolor tekstu próbkujemy z oryginalnych pikseli (np. kolor rzadkości przedmiotu).
            var box = keyed[i].Block.Box;
            var sampled = sampledStyles[i];
            var (colorRgb, backgroundRgb, outlineRgb) = sampled;
            var texture = sampled.Texture;
            var pendingBackgroundRgb = -1;

            var key = keyed[i].Key;
            while (next.ContainsKey(key))
            {
                key += "'";
            }

            var lineHeight = TextBlockMetrics.MedianLineHeight(keyed[i].Block);
            if (_displayed.TryGetValue(key, out var previous) || inheritFrom.TryGetValue(keyed[i].Key, out previous))
            {
                // Histereza stylu: kolejne przebiegi OCR pływają o piksele (wycinek ×2
                // vs pełna klatka, animacje pod tekstem) — nie przebudowujemy wyglądu
                // bloku, dopóki zmiana nie jest znacząca. Koniec z „oddychającą” czcionką.
                // Tolerancje szerokie: box OCR faluje (raz z obwódką, raz bez), a każda
                // zmiana rozmiaru odtwarza dymek od nowa — widoczne jako skok czcionki.
                // Tolerancje liczone od MNIEJSZEJ wartości (symetrycznie): najechany element
                // menu rośnie 68→97 px i musi po zjechaniu wrócić do 68 — przy tolerancji od
                // większej wartości 68 „mieściło się” w 97 i napis zostawał powiększony.
                var minLine = Math.Min(previous.LineHeight, lineHeight);
                if (Math.Abs(previous.LineHeight - lineHeight) <= Math.Max(2, minLine * 0.35))
                {
                    lineHeight = previous.LineHeight;
                }
                // Pozycja toleruje tylko drobny szum OCR (2 fizyczne piksele).
                // Szeroki próg zależny od rozmiaru tekstu przyklejał napis,
                // a następnie przeskakiwał; rozmiar stabilizujemy osobno.
                box = LiveBlockGeometry.Stabilize(previous.WindowRelativeBox, box);
                // Kolory trzymają się poprzednich, CHYBA ŻE tło pod napisem realnie się zmieniło
                // (najechany rząd: ciemny → żółty) i nowe oświetlenie UTRZYMAŁO SIĘ przez dwa
                // kolejne przebiegi — przewijana grafika faluje jasnością wokół progu i bez
                // tego warunku kolor tekstu skakał co 600 ms.
                var backgroundShifted = previous.BackgroundRgb >= 0 && backgroundRgb >= 0
                    && Math.Abs(Luminance(previous.BackgroundRgb) - Luminance(backgroundRgb)) >= 60;
                var shiftConfirmed = backgroundShifted && previous.PendingBackgroundRgb >= 0
                    && Math.Abs(Luminance(previous.PendingBackgroundRgb) - Luminance(backgroundRgb)) < 30;
                pendingBackgroundRgb = backgroundShifted && !shiftConfirmed ? backgroundRgb : -1;
                if (!shiftConfirmed)
                {
                    if (previous.ColorRgb >= 0) colorRgb = previous.ColorRgb;
                    if (previous.BackgroundRgb >= 0) backgroundRgb = previous.BackgroundRgb;
                    if (previous.OutlineRgb >= 0) outlineRgb = previous.OutlineRgb;
                }

                // Tekstura tła: podmieniana tylko przy realnej zmianie (przewijana grafika),
                // nie przy szumie próbkowania — inaczej rozmyta łatka „oddychała” co przebieg.
                if (previous.Texture is not null && texture is not null
                    && BackgroundTexture.MeanDifference(previous.Texture, texture) < TextureChangeThreshold)
                {
                    texture = previous.Texture;
                }
            }

            next[key] = new LiveOverlayBlock(
                box,
                translated,
                TextNormalizer.Normalize(translated),
                lineHeight,
                colorRgb,
                backgroundRgb,
                outlineRgb,
                Texture: texture,
                SourceText: keyed[i].NormalizedText,
                PendingBackgroundRgb: pendingBackgroundRgb);
            // Reference the source box, not the presentation box stabilized above.
            // A rescaled OCR image cannot prove equality with future native pixels.
            // A small complete margin also notices adjacent glyphs added to a label.
            var sourceBox = keyed[i].Block.Box.Inflate(3);
            var fingerprint = !usedScreenFallback && scaleBack == 1.0
                && TextPresenceProbe.CanCheckKnownText(sampled.TextRgb, sampled.BackgroundRgb)
                ? TextRegionFingerprint.FromBitmap(frame, sourceBox.Offset(-ocrRegion.X, -ocrRegion.Y)) : null;
            if (fingerprint is not null)
                nextFingerprints[key] = new BlockFingerprint(sourceBox, capturedFrameRect, fingerprint);
            else
                nextFingerprints.Remove(key);
            claimedBoxes.Add(box);
            if (!_displayed.ContainsKey(key))
            {
                freshKeys.Add(key);
            }
        }

        // Okres łaski: bloki, których ten przebieg nie widział, nie znikają od razu —
        // Windows OCR miewa puste przebiegi na niezmienionej scenie, a bez łaski każde
        // takie czknięcie zdejmowało i przywracało całą nakładkę (miganie).
        // Bloki przejęte przez drżące odczyty zostają jak rozpoznane (bez nieobecności).
        foreach (var (key, block) in reused)
        {
            next.TryAdd(key, block);
        }

        var sceneCut = peakChangedFraction >= options.SceneCutThreshold;
        if (!usedScreenFallback && scaleBack == 1.0)
        {
            // A forced OCR during world motion may miss a still-identical HUD.
            // Exact current pixels also protect it through repeated empty reads
            // when motion is below the scene-cut threshold. Never extend the miss
            // grace without this proof or across an overlapping replacement box.
            foreach (var (key, old) in _displayed)
            {
                if (next.ContainsKey(key) || claimedBoxes.Any(b => b.IntersectsWith(old.WindowRelativeBox))
                    || !_blockFingerprints.TryGetValue(key, out var reference)
                    || reference.FrameRect != capturedFrameRect) continue;
                if (reference.Image.Matches(TextRegionFingerprint.FromBitmap(
                    frame, reference.SourceBox.Offset(-ocrRegion.X, -ocrRegion.Y))))
                    next[key] = old with { Misses = 0 };
            }
        }
        var survivors = LiveBlockSurvival.Survivors(
            _displayed,
            next.Keys.ToHashSet(StringComparer.Ordinal),
            claimedBoxes,
            sceneCut,
            options.BlockMissGrace);
        foreach (var (key, block) in survivors)
        {
            next[key] = block;
        }

        // Bloki, które właśnie wygasły, zostają duchami (o ile to nie cięcie sceny).
        if (!sceneCut)
        {
            foreach (var (key, block) in _displayed)
            {
                if (!next.ContainsKey(key) && block.SourceText.Length > 0)
                {
                    _ghosts[key] = (block with { Misses = 0 }, _cycleTime);
                }
            }
        }
        else
        {
            _ghosts.Clear();
        }
        foreach (var expired in _ghosts.Where(g => _cycleTime - g.Value.DroppedAt > GhostLifetime || next.ContainsKey(g.Key)).Select(g => g.Key).ToList())
        {
            _ghosts.Remove(expired);
        }

        // Podejrzenie czknięcia OCR: pełny przebieg nic nie widzi mimo bloków na ekranie
        // albo łaska musiała podtrzymywać zgubione bloki. Na scenie statycznej żadna
        // kolejna zmiana obrazu nie nadejdzie — pętla sama prosi o powtórkę.
        var whiffSuspected = survivors.Count > 0
            || (!partialOcr && keyed.Count == 0 && rawLineCount == 0 && !sceneCut);
        if (whiffSuspected && _whiffRetries < options.MaxWhiffRetries)
        {
            _whiffRetries++;
            _whiffRetryRequested = true;
        }
        else if (!whiffSuspected)
        {
            _whiffRetries = 0;
        }

        var subtitle = freshKeys.Count > 0
            ? _subtitleContent.Replace(freshKeys.Select(key => new KeyValuePair<string, string>(key, next[key].TranslatedText)))
            : null;

        _displayed.Clear();
        foreach (var (key, block) in next)
        {
            _displayed[key] = block;
        }
        _blockFingerprints.Clear();
        foreach (var (key, reference) in nextFingerprints)
            if (next.ContainsKey(key)) _blockFingerprints[key] = reference;
        _readings.Prune(next.Keys.ToHashSet(StringComparer.Ordinal));

        // Pozycje liczymy względem ŚWIEŻYCH granic okna — mogło się przesunąć
        // w czasie oczekiwania na OCR i tłumaczenie.
        var bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
        _lastEmittedBounds = bounds;

        var firstError = outcomes.FirstOrDefault(static o => o.ErrorMessage is not null)?.ErrorMessage;
        var scope = partialOcr ? " • wycinek" : string.Empty;
        var retained = survivors.Count > 0 ? $" • podtrzymane {survivors.Count}" : string.Empty;
        var status = firstError
            ?? $"Live: {next.Count} bloków ({freshKeys.Count} nowych{retained}) • klatka {captureMs} ms • OCR {ocrMs} ms/{rawLineCount} linii • tłum. {translateMs} ms{scope}";

        // Od przechwycenia tej klatki do gotowych napisów — osobno dla tekstu znanego
        // (cache, słownik) i nowego (zapytanie do dostawcy). Bez czasu rysowania przez WPF.
        if (outcomes.Any(static o => o.IsTranslated))
        {
            orchestrator.Latency.Record(
                outcomes.Any(static o => o.Origin == TranslationOrigin.Provider) ? LatencyStage.NewText : LatencyStage.KnownText,
                Stopwatch.GetElapsedTime(captureStartedTimestamp).TotalMilliseconds);
        }

        var displayList = BuildDisplayList(bounds);
        var diagnostics = options.EnableDiagnostics
            ? new LiveFrameDiagnostics(
                Stopwatch.GetElapsedTime(captureStartedTimestamp).TotalMilliseconds,
                captureMs, ocrMs, translateMs,
                frame.Width, frame.Height, rawLineCount, blocks.Count,
                reused.Count, survivors.Count, next.Count,
                partialOcr, sceneCut, whiffSuspected, usedScreenFallback)
            {
                OcrOperationMs = ocrOperationMs,
                OcrSceneChecks = ocrSceneChecks,
                OcrSceneCheckMs = ocrSceneCheckMs,
                TranslationSceneChecks = _diagnosticSceneChecks - translationChecksBefore,
                TranslationSceneCheckMs = _diagnosticSceneCheckMs - translationCheckMsBefore,
            }
            : null;
        // Zmiana → napis: tylko gdy pokazujemy przetłumaczone bloki. Odczyt czekający
        // na potwierdzenie nie jest jeszcze napisem dla tej zmiany — mierzymy po powtórce.
        if (outcomes.Any(static o => o.IsTranslated) && !_readingRetryRequested
            && ChangeToTextTracker.Measure(_processingChangeOrigin, clock.Elapsed) is { } changeToTextMs)
        {
            orchestrator.Latency.Record(LatencyStage.ChangeToText, changeToTextMs);
            _processingChangeOrigin = null;
        }
        // Gracz nie widzi okna aplikacji — błąd dostawcy, pudła Cache-only i niedziałający
        // cache trafiają do nakładki jako krótki komunikat (polityka pilnuje powtórek).
        var notice = _notices.OfferFrame(outcomes, orchestrator.ActiveProvider.Name, orchestrator.IsCacheDegraded, clock.Elapsed);
        RememberNotice(notice);
        Emit(new LiveUpdate(status, displayList, subtitle, bounds, Diagnostics: diagnostics) { Notice = notice },
            cancellationToken);
    }
}
