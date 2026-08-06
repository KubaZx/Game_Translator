using System.Diagnostics;
using System.Drawing;
using GameTranslatorOverlay.App.Capture;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.App.Services;

public sealed record TranslatedBlock(Core.Text.TextBlock Block, TranslationOutcome Outcome);

public sealed record PipelineTimings(long CaptureMs, long OcrMs, long TranslateMs, long TotalMs)
{
    public override string ToString() =>
        $"przechwycenie {CaptureMs} ms • OCR {OcrMs} ms • tłumaczenie {TranslateMs} ms • razem {TotalMs} ms";
}

public sealed record RegionTranslationResult(
    RectPx Region,
    IReadOnlyList<TranslatedBlock> Blocks,
    PipelineTimings Timings,
    string? Warning);

/// <summary>
/// Spina pionowy przepływ: przechwycenie regionu → OCR → grupowanie/normalizacja →
/// pipeline tłumaczenia (słownik → cache → API). Kolejne żądanie anuluje poprzednie
/// (latest-wins). Nie dotyka UI — okna obsługuje MainWindow.
/// </summary>
public sealed class TranslationOrchestrator(
    AppSettings settings,
    SqliteTranslationCache persistentCache,
    IGlossaryService glossaryService,
    GlossaryCatalog glossaryCatalog,
    ProfileCatalog profileCatalog,
    UserGlossaryStore userGlossaryStore,
    MockTranslationProvider mockProvider,
    DeepLTranslationProvider deepLProvider,
    IOcrProvider ocrProvider,
    UsageTracker usage,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<TranslationOrchestrator>();

    private TranslationPipeline? _pipeline;
    private InMemoryTranslationCache? _privateCache;
    private CancellationTokenSource? _activeCts;

    public IReadOnlyList<GameProfile> Profiles { get; private set; } = [];
    public IReadOnlyList<CatalogIssue> ProfileIssues { get; private set; } = [];
    public IReadOnlyList<string> ContentWarnings { get; private set; } = [];
    public GameProfile? ActiveProfile { get; private set; }

    public ITranslationProvider ActiveProvider =>
        settings.Provider.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            ? mockProvider
            : deepLProvider;

    public ITranslationCache CurrentCache => _privateCache is { } inMemory ? inMemory : persistentCache;

    public void Initialize()
    {
        var (profiles, issues) = profileCatalog.LoadAll();
        Profiles = profiles;
        ProfileIssues = issues;
        foreach (var issue in issues)
        {
            _logger.LogWarning("Problem z profilem {File}: {Message}", issue.FilePath, issue.Message);
        }
        RebuildPipeline();
    }

    /// <summary>Przebudowuje pipeline po każdej zmianie ustawień (dostawca, profil, tryby, limity).</summary>
    public void RebuildPipeline()
    {
        // Operacja w locie działa na starych regułach (stary cache, stary dostawca) —
        // po zmianie np. trybu prywatnego nie może dokończyć zapisu na dysk.
        CancelActiveOperation();

        ActiveProfile = Profiles.FirstOrDefault(p => p.Id.Equals(settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase));

        var warnings = new List<string>();
        glossaryService.Clear();
        LoadGlossary("global", warnings);
        if (ActiveProfile?.Glossary is { Length: > 0 } profileGlossary)
        {
            LoadGlossary(profileGlossary, warnings);
        }
        glossaryService.LoadDocument(userGlossaryStore.Load(settings.SourceLanguage, settings.TargetLanguage));

        if (settings.PrivateMode)
        {
            _privateCache ??= new InMemoryTranslationCache();
        }
        else
        {
            _privateCache = null;
        }

        usage.SessionCharacterLimit = settings.SessionCharacterLimit;

        _pipeline = new TranslationPipeline(
            glossaryService,
            CurrentCache,
            ActiveProvider,
            usage,
            new TranslationPipelineOptions
            {
                CacheOnlyMode = settings.CacheOnlyMode,
                GameProfile = ActiveProfile?.Id ?? string.Empty,
            },
            loggerFactory.CreateLogger<TranslationPipeline>());

        ContentWarnings = warnings;
        _logger.LogInformation(
            "Pipeline: dostawca={Provider}, profil={Profile}, słownik={Terms} terminów, cacheOnly={CacheOnly}, prywatny={Private}",
            ActiveProvider.Name, ActiveProfile?.Id ?? "(brak)", glossaryService.TermCount, settings.CacheOnlyMode, settings.PrivateMode);
    }

    private void LoadGlossary(string glossaryId, List<string> warnings)
    {
        var (document, issue) = glossaryCatalog.TryLoad(glossaryId, settings.SourceLanguage, settings.TargetLanguage);
        if (document is not null)
        {
            glossaryService.LoadDocument(document);
        }
        else if (issue is not null && glossaryId != "global")
        {
            warnings.Add($"Słownik „{glossaryId}”: {issue.Message}");
            _logger.LogWarning("Problem ze słownikiem {Glossary}: {Message}", glossaryId, issue.Message);
        }
    }

    public void CancelActiveOperation() => _activeCts?.Cancel();

    public async Task<RegionTranslationResult> TranslateRegionAsync(RectPx region, CancellationToken externalToken = default)
    {
        var pipeline = _pipeline ?? throw new InvalidOperationException("Pipeline tłumaczenia nie został zbudowany.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var previous = Interlocked.Exchange(ref _activeCts, cts);
        previous?.Cancel();
        var cancellationToken = cts.Token;

        var totalWatch = Stopwatch.StartNew();

        var (ocrResult, captureMs, ocrMs) = await Task.Run(async () =>
        {
            var captureWatch = Stopwatch.StartNew();
            using var bitmap = ScreenCapture.CaptureScreenRegion(region);
            var captureElapsed = captureWatch.ElapsedMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();

            var preferredUpscale = ActiveProfile?.Ocr?.Upscale ?? settings.OcrUpscale;
            var upscale = OcrScaling.ComputeUpscale(bitmap.Width, bitmap.Height, ocrProvider.MaxImageDimension, preferredUpscale);
            var downscale = OcrScaling.ComputeDownscale(bitmap.Width, bitmap.Height, ocrProvider.MaxImageDimension);
            var factor = downscale < 1.0 ? downscale : upscale;

            var working = Math.Abs(factor - 1.0) > 0.001 ? ScreenCapture.Rescale(bitmap, factor) : bitmap;
            try
            {
                var ocrInput = ScreenCapture.ToOcrBitmap(working);
                var ocrWatch = Stopwatch.StartNew();
                var result = await ocrProvider.RecognizeAsync(ocrInput, settings.SourceLanguage, cancellationToken)
                    .ConfigureAwait(false);

                if (Math.Abs(factor - 1.0) > 0.001)
                {
                    var inverse = 1.0 / factor;
                    result = result with
                    {
                        Lines = result.Lines
                            .Select(line => new OcrLine(
                                line.Text,
                                line.Box.Scale(inverse),
                                line.Words.Select(w => new OcrWord(w.Text, w.Box.Scale(inverse))).ToList()))
                            .ToList(),
                    };
                }

                return (result, captureElapsed, ocrWatch.ElapsedMilliseconds);
            }
            finally
            {
                if (!ReferenceEquals(working, bitmap))
                {
                    working.Dispose();
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        // Współrzędne z OCR są względem regionu — przenosimy je na ekran.
        var screenLines = ocrResult.Lines
            .Select(line => new OcrLine(
                line.Text,
                line.Box.Offset(region.X, region.Y),
                line.Words.Select(w => new OcrWord(w.Text, w.Box.Offset(region.X, region.Y))).ToList()))
            .ToList();

        var blocks = TextBlockGrouper.Group(screenLines)
            .Where(static block => JunkFilter.IsMeaningful(block.Text))
            .ToList();

        if (blocks.Count == 0)
        {
            return new RegionTranslationResult(
                region, [],
                new PipelineTimings(captureMs, ocrMs, 0, totalWatch.ElapsedMilliseconds),
                "OCR nie rozpoznał tekstu w zaznaczonym obszarze. Spróbuj zaznaczyć większy fragment albo powiększyć tekst w grze.");
        }

        var translateWatch = Stopwatch.StartNew();
        var outcomes = await pipeline.TranslateAsync(
            blocks.Select(static b => b.Text).ToList(),
            settings.SourceLanguage,
            settings.TargetLanguage,
            cancellationToken).ConfigureAwait(false);
        var translateMs = translateWatch.ElapsedMilliseconds;

        var translated = blocks.Zip(outcomes, static (block, outcome) => new TranslatedBlock(block, outcome)).ToList();
        var firstError = translated.FirstOrDefault(static t => t.Outcome.ErrorMessage is not null)?.Outcome.ErrorMessage;

        return new RegionTranslationResult(
            region,
            translated,
            new PipelineTimings(captureMs, ocrMs, translateMs, totalWatch.ElapsedMilliseconds),
            firstError);
    }

    public Task SaveManualCorrectionAsync(TranslatedBlock block, string correctedText, CancellationToken cancellationToken = default)
    {
        var entry = new NewCacheEntry(
            block.Block.Text,
            block.Outcome.NormalizedText,
            settings.SourceLanguage,
            settings.TargetLanguage,
            correctedText.Trim(),
            "manual",
            ActiveProfile?.Id ?? string.Empty);
        return CurrentCache.SaveManualCorrectionAsync(entry, cancellationToken);
    }

    public Task AddGlossaryTermAsync(string source, string target)
    {
        var term = new GlossaryTerm(
            source.Trim().Replace('\n', ' '),
            target.Trim().Replace('\n', ' '),
            Priority: 100);

        glossaryService.AddTerm(term);

        // Tryb prywatny obiecuje brak zapisu treści z ekranu na dysk — termin działa
        // tylko w pamięci do końca sesji.
        if (settings.PrivateMode) return Task.CompletedTask;

        return Task.Run(() => userGlossaryStore.AddTerm(term, settings.SourceLanguage, settings.TargetLanguage));
    }

    public Task<ProviderStatus> TestActiveProviderAsync(CancellationToken cancellationToken = default) =>
        ActiveProvider.TestConnectionAsync(cancellationToken);
}
