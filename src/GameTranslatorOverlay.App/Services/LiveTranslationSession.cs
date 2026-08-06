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
/// aktualny pipeline. Latest-frame-wins: pętla zawsze pracuje na najnowszej klatce,
/// starych nie kolejkuje. Zero ingerencji w okno gry — wyłącznie odczyt obrazu.
/// </summary>
public sealed class LiveTranslationSession(
    TranslationOrchestrator orchestrator,
    IOcrProvider ocrProvider,
    IntPtr gameWindowHandle,
    LiveSessionOptions options,
    Action<LiveUpdate> onUpdate,
    ILogger logger) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, LiveDisplayBlock> _displayed = [];
    private byte[]? _gridBuffer;
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

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var stabilizer = new ChangeStabilizer(options.StabilityDelay);
        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(options.Fps, 0.5, 30.0));
        LuminanceGrid? previousGrid = null;
        var wasMinimized = false;
        var lastChangedFraction = 0.0;

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
                        previousGrid = null;
                        stabilizer.Reset();
                        _displayed.Clear();
                        onUpdate(new LiveUpdate("Gra zminimalizowana — nakładka ukryta, czekam na powrót.",
                            ClearOverlay: true));
                    }
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (wasMinimized)
                {
                    wasMinimized = false;
                    stabilizer.ForceDirty(clock.Elapsed);
                }

                var captureWatch = Stopwatch.StartNew();
                RectPx bounds;
                OcrBitmap? frameForOcr = null;
                var ocrScaleBack = 1.0;

                using (var bitmap = ScreenCapture.CaptureWindow(gameWindowHandle))
                {
                    if (bitmap is null)
                    {
                        await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    bounds = ScreenCapture.GetWindowBounds(gameWindowHandle);
                    var grid = ScreenCapture.ComputeLuminanceGrid(bitmap, ref _gridBuffer);
                    lastChangedFraction = previousGrid is null
                        ? 1.0
                        : FrameChangeDetector.ChangedFraction(previousGrid, grid);
                    previousGrid = grid;

                    var frameChanged = lastChangedFraction > options.ChangeThreshold;
                    if (stabilizer.Update(frameChanged, clock.Elapsed))
                    {
                        // Pełną kopię klatki robimy wyłącznie, gdy naprawdę idzie do OCR.
                        var downscale = OcrScaling.ComputeDownscale(bitmap.Width, bitmap.Height, ocrProvider.MaxImageDimension);
                        if (downscale < 1.0)
                        {
                            using var scaled = ScreenCapture.Rescale(bitmap, downscale);
                            frameForOcr = ScreenCapture.ToOcrBitmap(scaled);
                            ocrScaleBack = 1.0 / downscale;
                        }
                        else
                        {
                            frameForOcr = ScreenCapture.ToOcrBitmap(bitmap);
                        }
                    }
                }
                var captureMs = captureWatch.ElapsedMilliseconds;

                if (frameForOcr is not null)
                {
                    await ProcessFrameAsync(frameForOcr, ocrScaleBack, bounds, captureMs, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    onUpdate(new LiveUpdate(
                        $"Live: obserwuję (zmiana {lastChangedFraction:P0}, bloki {_displayed.Count}, klatka {captureMs} ms)."));
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

    private async Task ProcessFrameAsync(
        OcrBitmap frame, double scaleBack, RectPx windowBounds, long captureMs, CancellationToken cancellationToken)
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

        var translateWatch = Stopwatch.StartNew();
        var outcomes = await orchestrator.TranslateTextsAsync(
            keyed.Select(static k => k.Block.Text).ToList(), cancellationToken).ConfigureAwait(false);
        var translateMs = translateWatch.ElapsedMilliseconds;

        var freshKeys = new List<string>();
        var next = new Dictionary<string, LiveDisplayBlock>();
        for (var i = 0; i < keyed.Count; i++)
        {
            var outcome = outcomes[i];
            if (outcome.TranslatedText is not { } translated) continue;

            var key = keyed[i].Key;
            var screenBox = keyed[i].Block.Box.Offset(windowBounds.X, windowBounds.Y);
            next[key] = new LiveDisplayBlock(key, screenBox, translated);
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

        var firstError = outcomes.FirstOrDefault(static o => o.ErrorMessage is not null)?.ErrorMessage;
        var status = firstError
            ?? $"Live: {next.Count} bloków ({freshKeys.Count} nowych) • klatka {captureMs} ms • OCR {ocrMs} ms • tłum. {translateMs} ms";

        onUpdate(new LiveUpdate(status, next.Values.ToList(), subtitle, windowBounds));
    }
}
