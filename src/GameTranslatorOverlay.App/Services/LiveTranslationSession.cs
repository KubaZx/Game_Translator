using System.Diagnostics;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.App.Interop;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Vision;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App.Services;

public sealed record LiveDisplayBlock(string Key, RectPx ScreenBox, string TranslatedText);

public sealed record LiveUpdate(
    string StatusLine,
    IReadOnlyList<LiveDisplayBlock>? Blocks = null,
    string? SubtitleText = null,
    RectPx WindowBounds = default,
    bool ClearOverlay = false,
    bool Stopped = false);

public sealed class LiveSessionOptions
{
    public double Fps { get; init; } = 4;
    public double ChangeThreshold { get; init; } = 0.02;
    public TimeSpan StabilityDelay { get; init; } = TimeSpan.FromMilliseconds(300);
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

    private sealed record DisplayedBlock(RectPx WindowRelativeBox, string TranslatedText, string NormalizedTranslation);

    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DisplayedBlock> _displayed = [];
    private byte[]? _gridBuffer;
    private LuminanceGrid? _previousGrid;
    private RectPx _lastEmittedBounds;
    private bool _warnedAboutScreenFallback;
    private int _consecutiveFailures;
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
                kv.Value.TranslatedText))
            .ToList();

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var stabilizer = new ChangeStabilizer(options.StabilityDelay);
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
                    var (changedFraction, processed) = await RunCycleAsync(
                        stabilizer, clock, cancellationToken).ConfigureAwait(false);

                    _consecutiveFailures = 0;
                    if (!processed)
                    {
                        Emit(new LiveUpdate(
                            $"Live: obserwuję (zmiana {changedFraction:P0}, bloki {_displayed.Count})."),
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
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
    private async Task<(double ChangedFraction, bool Processed)> RunCycleAsync(
        ChangeStabilizer stabilizer,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        RectPx bounds;
        OcrBitmap? frameForOcr = null;
        var ocrScaleBack = 1.0;
        double changedFraction;
        long captureMs;

        var captureWatch = Stopwatch.StartNew();
        var (bitmap, usedScreenFallback) = ScreenCapture.CaptureWindowEx(gameWindowHandle);
        using (bitmap)
        {
            captureMs = captureWatch.ElapsedMilliseconds;
            if (bitmap is null)
            {
                return (0, false);
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
            changedFraction = _previousGrid is null
                ? 1.0
                : FrameChangeDetector.ChangedFraction(_previousGrid, grid);
            _previousGrid = grid;

            var frameChanged = changedFraction > options.ChangeThreshold;
            if (stabilizer.Update(frameChanged, clock.Elapsed))
            {
                // Pełną kopię klatki robimy wyłącznie, gdy naprawdę idzie do OCR.
                var downscale = OcrScaling.ComputeDownscale(bitmap.Width, bitmap.Height, ocrProvider.MaxImageDimension);
                var factor = downscale < 1.0
                    ? downscale
                    : OcrScaling.ComputeUpscale(bitmap.Width, bitmap.Height, ocrProvider.MaxImageDimension, options.OcrUpscale);

                if (Math.Abs(factor - 1.0) > 0.001)
                {
                    using var scaled = ScreenCapture.Rescale(bitmap, factor);
                    frameForOcr = ScreenCapture.ToOcrBitmap(scaled);
                    ocrScaleBack = 1.0 / factor;
                }
                else
                {
                    frameForOcr = ScreenCapture.ToOcrBitmap(bitmap);
                }
            }
        }

        if (frameForOcr is not null)
        {
            await ProcessFrameAsync(frameForOcr, ocrScaleBack, captureMs, cancellationToken).ConfigureAwait(false);
            return (changedFraction, true);
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
            return (changedFraction, true);
        }

        return (changedFraction, false);
    }

    private async Task ProcessFrameAsync(
        OcrBitmap frame, double scaleBack, long captureMs, CancellationToken cancellationToken)
    {
        var ocrWatch = Stopwatch.StartNew();
        var ocrResult = await ocrProvider.RecognizeAsync(frame, orchestrator.SourceLanguage, cancellationToken)
            .ConfigureAwait(false);
        var ocrMs = ocrWatch.ElapsedMilliseconds;

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

        var blocks = TextBlockGrouper.Group(lines)
            .Where(static block => JunkFilter.IsMeaningful(block.Text))
            .ToList();
        var keyed = LiveBlockKeyer.AssignKeys(blocks);

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
        var next = new Dictionary<string, DisplayedBlock>();
        for (var i = 0; i < keyed.Count; i++)
        {
            var outcome = outcomes[i];
            if (outcome.TranslatedText is not { } translated) continue;

            var key = keyed[i].Key;
            next[key] = new DisplayedBlock(
                keyed[i].Block.Box,
                translated,
                TextNormalizer.Normalize(translated));
            if (!_displayed.ContainsKey(key))
            {
                freshKeys.Add(key);
            }
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
        var status = firstError
            ?? $"Live: {next.Count} bloków ({freshKeys.Count} nowych) • klatka {captureMs} ms • OCR {ocrMs} ms • tłum. {translateMs} ms";

        Emit(new LiveUpdate(status, BuildDisplayList(bounds), subtitle, bounds), cancellationToken);
    }
}
