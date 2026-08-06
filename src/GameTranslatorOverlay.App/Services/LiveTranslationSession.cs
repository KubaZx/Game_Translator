using System.Diagnostics;
using System.IO;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Vision;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App.Services;

public sealed record LiveDisplayBlock(string Key, RectPx ScreenBox, string TranslatedText, int LineHeight = 0, int ColorRgb = -1);

public sealed record LiveUpdate(
    string StatusLine,
    IReadOnlyList<LiveDisplayBlock>? Blocks = null,
    string? SubtitleText = null,
    RectPx WindowBounds = default,
    bool ClearOverlay = false,
    bool Stopped = false,
    bool HideOverlay = false);

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
    /// Bezpiecznik: nawet przy nieprzerwanym „ruchu” (np. powolna panorama z napisami)
    /// po tym czasie klatka i tak zostaje przetworzona.
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

    public double OcrUpscale { get; init; }
}

/// <summary>
/// Tryb live: cykliczne przechwytywanie wybranego okna, tanie wykrywanie zmian
/// (siatka jasności), OCR dopiero po ustabilizowaniu obrazu, tłumaczenie przez
/// aktualny pipeline. Latest-frame-wins: pętla zawsze pracuje na najnowszej klatce.
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
    private readonly Dictionary<string, LiveOverlayBlock> _displayed = [];
    private byte[]? _gridBuffer;
    private LuminanceGrid? _previousGrid;
    private RectPx? _pendingDirtyRegion;
    private double _peakChangedFraction;
    private TimeSpan _lastProcessedAt;
    private int _whiffRetries;
    private bool _whiffRetryRequested;
    private RectPx _lastEmittedBounds;
    private bool _warnedAboutScreenFallback;
    private int _consecutiveFailures;
    private int _motionFrames;
    private TimeSpan _motionSince;
    private Task? _loop;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

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
                kv.Value.ColorRgb))
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
                onUpdate(new LiveUpdate(
                    $"Brak pakietu OCR dla języka „{orchestrator.SourceLanguage}” — tryb live zatrzymany.",
                    ClearOverlay: true, Stopped: true));
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var cycleStart = clock.Elapsed;

                if (!NativeMethods.IsWindow(gameWindowHandle))
                {
                    onUpdate(new LiveUpdate("Okno gry zostało zamknięte — tryb live zatrzymany.",
                        ClearOverlay: true, Stopped: true));
                    return;
                }

                if (NativeMethods.IsIconic(gameWindowHandle))
                {
                    if (!wasMinimized)
                    {
                        wasMinimized = true;
                        _previousGrid = null;
                        stabilizer.Reset();
                        _displayed.Clear();
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
                    var (changedFraction, strongFraction, processed) = await RunCycleAsync(
                        stabilizer, clock, cancellationToken).ConfigureAwait(false);

                    _consecutiveFailures = 0;
                    if (!processed)
                    {
                        Emit(new LiveUpdate(
                            $"Live: obserwuję (zmiana {changedFraction:P0}, mocne {strongFraction:P0}, bloki {_displayed.Count})."),
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
                        onUpdate(new LiveUpdate(
                            "Tryb live zatrzymany po serii błędów — szczegóły w logu diagnostycznym.",
                            ClearOverlay: true, Stopped: true));
                        return;
                    }
                    Emit(new LiveUpdate("Live: pominięto klatkę z powodu błędu (szczegóły w logu)."), cancellationToken);
                }

                var remaining = interval - (clock.Elapsed - cycleStart);
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
            onUpdate(new LiveUpdate("Tryb live zatrzymany przez błąd — szczegóły w logu diagnostycznym.",
                ClearOverlay: true, Stopped: true));
        }
    }

    /// <summary>Jeden cykl: capture → detekcja zmian → (opcjonalnie) OCR + tłumaczenie.</summary>
    private async Task<(double ChangedFraction, double StrongFraction, bool Processed)> RunCycleAsync(
        ChangeStabilizer stabilizer,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        RectPx bounds;
        OcrBitmap? frameForOcr = null;
        var ocrScaleBack = 1.0;
        var ocrRegion = default(RectPx);
        var partialOcr = false;
        double changedFraction;
        double strongFraction;
        long captureMs;

        var captureWatch = Stopwatch.StartNew();
        var (bitmap, usedScreenFallback) = ScreenCapture.CaptureWindowEx(gameWindowHandle);
        using (bitmap)
        {
            captureMs = captureWatch.ElapsedMilliseconds;
            if (bitmap is null)
            {
                return (0, 0, false);
            }

            if (usedScreenFallback && !_warnedAboutScreenFallback)
            {
                _warnedAboutScreenFallback = true;
                logger.LogWarning("Okno gry nie wspiera PrintWindow — tryb live używa zrzutu ekranu (możliwe obce okna w kadrze)");
                Emit(new LiveUpdate(
                    "⚠ To okno wymaga przechwytywania ekranu: fragmenty innych okien nachodzących na grę mogą być tłumaczone. " +
                    "Zamknij poufne okna znad gry albo zatrzymaj tryb live."), cancellationToken);
            }

            bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
            var grid = ScreenCapture.ComputeLuminanceGrid(bitmap, ref _gridBuffer);
            var frameRect = new RectPx(0, 0, bitmap.Width, bitmap.Height);
            var analysis = _previousGrid is null
                ? new ChangeAnalysis(1.0, frameRect)
                : FrameChangeDetector.Analyze(_previousGrid, grid, bitmap.Width, bitmap.Height);
            changedFraction = analysis.ChangedFraction;
            strongFraction = analysis.StrongChangedFraction;
            _previousGrid = grid;

            // Cięcie sceny ocenia się po SZCZYCIE zmian od ostatniego przetworzenia,
            // nie po klatce, która akurat trafiła do OCR — po twardym cięciu przetwarzana
            // jest już spokojna klatka nowej sceny (zmiana ~0%), a wysoka zmiana samego
            // cięcia przepadała i duchy starych bloków przeżywały okres łaski.
            _peakChangedFraction = Math.Max(_peakChangedFraction, changedFraction);

            var frameChanged = changedFraction > options.ChangeThreshold;
            var forceProcess = false;

            // Scena w ruchu (bieg, przesuw kamery): każdy wynik OCR wylądowałby w miejscu,
            // z którego tekst już odpłynął. Ruch poznajemy po MOCNYCH zmianach pikseli —
            // falująca mgła/pogoda zmienia komórki subtelnie i ruchem nie jest.
            if (analysis.StrongChangedFraction >= options.MotionThreshold)
            {
                if (_motionFrames == 0)
                {
                    _motionSince = clock.Elapsed;
                }
                _motionFrames++;
                _pendingDirtyRegion = frameRect;

                if (clock.Elapsed - _motionSince < options.MaxMotionPause)
                {
                    stabilizer.ForceDirty(clock.Elapsed);
                    if (_motionFrames == 2)
                    {
                        // Scena odpłynęła — stare bloki są nieaktualne, po ruchu budujemy od zera.
                        _displayed.Clear();
                        Emit(new LiveUpdate(
                            $"Live: ruch na ekranie (mocne {strongFraction:P0}) — wstrzymuję tłumaczenie do ustania ruchu.",
                            HideOverlay: true), cancellationToken);
                    }
                    return (changedFraction, strongFraction, _motionFrames >= 2);
                }

                // Bezpiecznik: „ruch” trwa podejrzanie długo (panorama z napisami,
                // czuły detektor) — przetwarzamy mimo wszystko i liczymy pauzę od nowa.
                _motionSince = clock.Elapsed;
                forceProcess = true;
            }
            else
            {
                _motionFrames = 0;
            }

            if (frameChanged && analysis.ChangedRegion is { } changedNow)
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
                    : OcrScaling.ComputeUpscale(source.Width, source.Height, ocrProvider.MaxImageDimension, preferredUpscale);

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
            await ProcessFrameAsync(frameForOcr, ocrScaleBack, ocrRegion, partialOcr, captureMs, peakChanged, cancellationToken)
                .ConfigureAwait(false);

            // Podejrzenie czknięcia OCR: na statycznej scenie tylko wymuszona powtórka
            // może odzyskać przegapiony tekst (ograniczona licznikiem, żeby pusty ekran
            // nie kręcił OCR w kółko).
            if (_whiffRetryRequested)
            {
                _whiffRetryRequested = false;
                stabilizer.ForceDirty(clock.Elapsed);
            }
            return (changedFraction, strongFraction, true);
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
            return (changedFraction, strongFraction, true);
        }

        return (changedFraction, strongFraction, false);
    }

    private async Task ProcessFrameAsync(
        OcrBitmap frame, double scaleBack, RectPx ocrRegion, bool partialOcr,
        long captureMs, double peakChangedFraction, CancellationToken cancellationToken)
    {
        var ocrWatch = Stopwatch.StartNew();
        var ocrResult = await ocrProvider.RecognizeAsync(frame, orchestrator.SourceLanguage, cancellationToken)
            .ConfigureAwait(false);
        var ocrMs = ocrWatch.ElapsedMilliseconds;
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
        // zawiedzie): blok, którego tekst jest naszym własnym wyświetlanym tłumaczeniem,
        // nie wraca do tłumaczenia.
        var displayedTranslations = _displayed.Values
            .Select(static d => d.NormalizedTranslation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        keyed = keyed
            .Where(k => !displayedTranslations.Contains(k.NormalizedText))
            .ToList();

        var translateWatch = Stopwatch.StartNew();
        var outcomes = await orchestrator.TranslateTextsAsync(
            keyed.Select(static k => k.Block.Text).ToList(), cancellationToken).ConfigureAwait(false);
        var translateMs = translateWatch.ElapsedMilliseconds;

        if (cancellationToken.IsCancellationRequested) return;

        var freshKeys = new List<string>();
        var claimedBoxes = new List<RectPx>();
        var next = new Dictionary<string, LiveOverlayBlock>();

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
            var sampleBox = new RectPx(
                (int)((box.X - ocrRegion.X) / scaleBack),
                (int)((box.Y - ocrRegion.Y) / scaleBack),
                Math.Max(1, (int)(box.Width / scaleBack)),
                Math.Max(1, (int)(box.Height / scaleBack)));
            var colorRgb = TextColorSampler.SampleTextColorRgb(
                frame.PixelsBgra32, frame.Width, frame.Height, frame.Stride, sampleBox);

            var key = keyed[i].Key;
            while (next.ContainsKey(key))
            {
                key += "'";
            }

            var lineHeight = TextBlockMetrics.MedianLineHeight(keyed[i].Block);
            if (_displayed.TryGetValue(key, out var previous))
            {
                // Histereza stylu: kolejne przebiegi OCR pływają o piksele (wycinek ×2
                // vs pełna klatka, animacje pod tekstem) — nie przebudowujemy wyglądu
                // bloku, dopóki zmiana nie jest znacząca. Koniec z „oddychającą” czcionką.
                if (Math.Abs(previous.LineHeight - lineHeight) <= Math.Max(2, previous.LineHeight / 5))
                {
                    lineHeight = previous.LineHeight;
                }
                if (Math.Abs(previous.WindowRelativeBox.X - box.X) <= 6
                    && Math.Abs(previous.WindowRelativeBox.Y - box.Y) <= 6
                    && Math.Abs(previous.WindowRelativeBox.Width - box.Width) <= 12
                    && Math.Abs(previous.WindowRelativeBox.Height - box.Height) <= 12)
                {
                    box = previous.WindowRelativeBox;
                }
                if (previous.ColorRgb >= 0)
                {
                    colorRgb = previous.ColorRgb;
                }
            }

            next[key] = new LiveOverlayBlock(
                box,
                translated,
                TextNormalizer.Normalize(translated),
                lineHeight,
                colorRgb);
            claimedBoxes.Add(box);
            if (!_displayed.ContainsKey(key))
            {
                freshKeys.Add(key);
            }
        }

        // Okres łaski: bloki, których ten przebieg nie widział, nie znikają od razu —
        // Windows OCR miewa puste przebiegi na niezmienionej scenie, a bez łaski każde
        // takie czknięcie zdejmowało i przywracało całą nakładkę (miganie).
        var sceneCut = peakChangedFraction >= options.SceneCutThreshold;
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
            ? string.Join('\n', freshKeys.Select(key => next[key].TranslatedText)).Trim()
            : null;
        if (subtitle is { Length: > 400 })
        {
            subtitle = subtitle[..400] + "…";
        }

        _displayed.Clear();
        foreach (var (key, block) in next)
        {
            _displayed[key] = block;
        }

        // Pozycje liczymy względem ŚWIEŻYCH granic okna — mogło się przesunąć
        // w czasie oczekiwania na OCR i tłumaczenie.
        var bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
        _lastEmittedBounds = bounds;

        var firstError = outcomes.FirstOrDefault(static o => o.ErrorMessage is not null)?.ErrorMessage;
        var scope = partialOcr ? " • wycinek" : string.Empty;
        var retained = survivors.Count > 0 ? $" • podtrzymane {survivors.Count}" : string.Empty;
        var status = firstError
            ?? $"Live: {next.Count} bloków ({freshKeys.Count} nowych{retained}) • klatka {captureMs} ms • OCR {ocrMs} ms/{rawLineCount} linii • tłum. {translateMs} ms{scope}";

        Emit(new LiveUpdate(status, BuildDisplayList(bounds), subtitle, bounds), cancellationToken);
    }
}
