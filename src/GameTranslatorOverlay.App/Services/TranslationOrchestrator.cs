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

public sealed record TranslatedBlock(Core.Text.TextBlock Block, TranslationOutcome Outcome, int TextColorRgb = -1);

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
    ILoggerFactory loggerFactory,
    IEnumerable<ITranslationProvider>? additionalProviders = null,
    CorpusCatalog? corpusCatalog = null)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<TranslationOrchestrator>();

    /// <summary>Mock i DeepL zawsze; pozostali dostawcy (Azure, Google, LLM, Claude) z DI.</summary>
    private readonly IReadOnlyList<ITranslationProvider> _providers = [mockProvider, deepLProvider, .. additionalProviders ?? []];

    /// <summary>
    /// Niemutowalna para pipeline + token jego epoki, publikowana JEDNYM zapisem —
    /// wołający z wątku tła nie może skleić starego pipeline'u z nowym tokenem
    /// (wyścig groził np. zapisem na dysk już po włączeniu trybu prywatnego).
    /// </summary>
    private sealed record PipelineState(TranslationPipeline Pipeline, CancellationToken EpochToken);

    private PipelineState? _pipelineState;
    private InMemoryTranslationCache? _privateCache;
    private CancellationTokenSource? _activeCts;
    private CancellationTokenSource _pipelineEpoch = new();
    private readonly List<GlossaryTerm> _privateSessionTerms = [];

    public IReadOnlyList<GameProfile> Profiles { get; private set; } = [];
    public IReadOnlyList<CatalogIssue> ProfileIssues { get; private set; } = [];
    public IReadOnlyList<string> ContentWarnings { get; private set; } = [];
    public GameProfile? ActiveProfile { get; private set; }

    public CorpusLoadResult ActiveCorpus { get; private set; } = CorpusLoadResult.None;

    /// <summary>Dostawca wybrany w ustawieniach; nieznana nazwa oznacza DeepL (domyślny).</summary>
    public ITranslationProvider ActiveProvider =>
        _providers.FirstOrDefault(p => p.Name.Equals(settings.Provider?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? deepLProvider;

    /// <summary>Czasy etapów tej sesji aplikacji (live i ręczne tłumaczenie).</summary>
    public LatencyMonitor Latency => usage.Latency;

    /// <summary>
    /// Czy bieżący pipeline zgłosił błąd trwałego cache (wyniki tylko w pamięci) — UI
    /// pokazuje wtedy ostrzeżenie w nakładce. Nowy pipeline zaczyna bez tego stanu.
    /// </summary>
    public bool IsCacheDegraded => Volatile.Read(ref _pipelineState)?.Pipeline.IsCacheDegraded == true;

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
        // Epoka pipeline'u obejmuje też wywołania z trybu live (TranslateTextsAsync).
        CancelActiveOperation();
        var previousEpoch = Interlocked.Exchange(ref _pipelineEpoch, new CancellationTokenSource());
        previousEpoch.Cancel();

        ActiveProfile = Profiles.FirstOrDefault(p => p.Id.Equals(settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase));

        var warnings = new List<string>();
        glossaryService.Clear();
        LoadGlossary("global", warnings);
        if (ActiveProfile?.Glossary is { Length: > 0 } profileGlossary)
        {
            LoadGlossary(profileGlossary, warnings);
        }
        glossaryService.LoadDocument(userGlossaryStore.Load(settings.SourceLanguage, settings.TargetLanguage));

        // Terminy dodane w trybie prywatnym żyją tylko w pamięci — muszą przetrwać
        // każdą przebudowę pipeline'u do końca sesji aplikacji. Oznaczone jako sesyjne,
        // żeby także po wyłączeniu trybu prywatnego nie trafiły do glosariusza DeepL
        // (trwałego, na koncie użytkownika).
        foreach (var term in _privateSessionTerms)
        {
            glossaryService.AddTerm(term, sessionOnly: true);
        }

        if (settings.PrivateMode)
        {
            _privateCache ??= new InMemoryTranslationCache();
        }
        else
        {
            _privateCache = null;
        }

        usage.SessionCharacterLimit = settings.SessionCharacterLimit;

        ActiveCorpus = ActiveProfile is { } profileWithCorpus && corpusCatalog is not null
            ? corpusCatalog.Load(profileWithCorpus.Id)
            : CorpusLoadResult.None;
        if (ActiveCorpus.Issue is { } corpusIssue) warnings.Add($"Korpus profilu „{ActiveProfile?.Id}”: {corpusIssue}");

        // Glosariusz DeepL jest przechowywany na koncie DeepL — tryb prywatny go nie tworzy.
        deepLProvider.Options.UseGlossary = !settings.PrivateMode;

        var pipeline = new TranslationPipeline(
            glossaryService,
            CurrentCache,
            ActiveProvider,
            usage,
            new TranslationPipelineOptions
            {
                CacheOnlyMode = settings.CacheOnlyMode,
                GameProfile = ActiveProfile?.Id ?? string.Empty,
                GameName = ActiveProfile?.Name,
                PlayerGender = PlayerGenders.Parse(settings.PlayerGender),
                Corpus = ActiveCorpus.Snapper,
                SplitParagraphs = ActiveCorpus.IsLoaded || settings.ParagraphCacheKeys,
            },
            loggerFactory.CreateLogger<TranslationPipeline>(),
            cacheWriteCancellationToken: _pipelineEpoch.Token);
        Volatile.Write(ref _pipelineState, new PipelineState(pipeline, _pipelineEpoch.Token));

        ContentWarnings = warnings;
        _logger.LogInformation(
            "Pipeline: dostawca={Provider}, profil={Profile}, słownik={Terms} terminów, cacheOnly={CacheOnly}, prywatny={Private}, korpus={CorpusTexts} tekstów, akapity={Paragraphs}",
            ActiveProvider.Name, ActiveProfile?.Id ?? "(brak)", glossaryService.TermCount, settings.CacheOnlyMode, settings.PrivateMode,
            ActiveCorpus.Texts, ActiveCorpus.IsLoaded || settings.ParagraphCacheKeys);
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
        var pipeline = (Volatile.Read(ref _pipelineState)
            ?? throw new InvalidOperationException("Pipeline tłumaczenia nie został zbudowany.")).Pipeline;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var previous = Interlocked.Exchange(ref _activeCts, cts);
        previous?.Cancel();
        var cancellationToken = cts.Token;

        var totalWatch = Stopwatch.StartNew();

        var (ocrResult, sampleFrame, captureMs, ocrMs) = await Task.Run(async () =>
        {
            var captureWatch = Stopwatch.StartNew();
            using var bitmap = ScreenCapture.CaptureScreenRegion(region);
            var captureElapsed = captureWatch.ElapsedMilliseconds;
            usage.Latency.Record(LatencyStage.Capture, captureWatch.Elapsed.TotalMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            // Kopia pikseli oryginału do próbkowania koloru tekstu (przed skalowaniem).
            var pixelsForColor = ScreenCapture.ToOcrBitmap(bitmap);

            var preference = OcrScaling.ResolvePreference(ActiveProfile?.Ocr?.Upscale, settings.OcrUpscale);
            var upscale = OcrScaling.ComputeUpscale(
                bitmap.Width, bitmap.Height, ocrProvider.MaxImageDimension, preference.Preferred, preference.AllowAuto);
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

                usage.Latency.Record(LatencyStage.Ocr, ocrWatch.Elapsed.TotalMilliseconds);
                return (result, pixelsForColor, captureElapsed, ocrWatch.ElapsedMilliseconds);
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
            .Where(block => JunkFilter.IsMeaningful(block.Text) || pipeline.IsExactCorpusText(block.Text))
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
        if (outcomes.Any(static o => o.IsTranslated))
        {
            usage.Latency.Record(
                outcomes.Any(static o => o.Origin == TranslationOrigin.Provider) ? LatencyStage.NewText : LatencyStage.KnownText,
                totalWatch.Elapsed.TotalMilliseconds);
        }

        var translated = new List<TranslatedBlock>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            // Kolor tekstu próbkowany z oryginału (np. kolor rzadkości przedmiotu w PoE2).
            var colorRgb = Core.Vision.TextColorSampler.SampleTextColorRgb(
                sampleFrame.PixelsBgra32, sampleFrame.Width, sampleFrame.Height, sampleFrame.Stride,
                blocks[i].Box.Offset(-region.X, -region.Y));
            translated.Add(new TranslatedBlock(blocks[i], outcomes[i], colorRgb));
        }
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
            block.Outcome.CacheKey ?? block.Block.Text,
            block.Outcome.CacheKey ?? block.Outcome.NormalizedText,
            settings.SourceLanguage,
            settings.TargetLanguage,
            correctedText.Trim(),
            "manual",
            ActiveProfile?.Id ?? string.Empty);
        // Pamięć dialogu (tylko w RAM) ma się trzymać poprawionej formy w kolejnych liniach.
        Volatile.Read(ref _pipelineState)?.Pipeline.RememberManualCorrection(entry.NormalizedText, entry.TranslatedText);
        return CurrentCache.SaveManualCorrectionAsync(entry, cancellationToken);
    }

    public Task AddGlossaryTermAsync(string source, string target)
    {
        var term = new GlossaryTerm(
            source.Trim().Replace('\n', ' '),
            target.Trim().Replace('\n', ' '),
            Priority: 100);

        // Tryb prywatny obiecuje brak zapisu treści z ekranu na dysk i niczego trwałego
        // w chmurze — termin działa tylko w pamięci (bez glosariusza DeepL), ale musi
        // przetrwać przebudowy pipeline'u do końca sesji.
        if (settings.PrivateMode)
        {
            glossaryService.AddTerm(term, sessionOnly: true);
            _privateSessionTerms.Add(term);
            return Task.CompletedTask;
        }

        glossaryService.AddTerm(term);
        return Task.Run(() => userGlossaryStore.AddTerm(term, settings.SourceLanguage, settings.TargetLanguage));
    }

    private static readonly TimeSpan WarmUpInterval = TimeSpan.FromSeconds(30);
    private long _lastWarmUpTicks = long.MinValue / 2;

    /// <summary>
    /// Nawiązuje połączenie z aktywnym dostawcą, zanim pojawi się tekst — np. gdy użytkownik
    /// zaczyna zaznaczać region albo uruchamia live. Bez klucza i treści; pomijane
    /// w Cache-only (tryb obiecuje brak ruchu sieciowego) i częściej niż co 30 s.
    /// </summary>
    public void WarmUpActiveProvider()
    {
        if (settings.CacheOnlyMode || ActiveProvider is not IWarmableTranslationProvider warmable) return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarmUpTicks);
        if (now - last < WarmUpInterval.TotalMilliseconds
            || Interlocked.CompareExchange(ref _lastWarmUpTicks, now, last) != last)
        {
            return;
        }

        var epoch = (Volatile.Read(ref _pipelineState)?.EpochToken) ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await warmable.WarmUpAsync(epoch).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Zmiana ustawień w trakcie rozgrzewki — bez znaczenia.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Rozgrzewka połączenia z dostawcą nie powiodła się");
            }
        });
    }

    public Task<ProviderStatus> TestActiveProviderAsync(CancellationToken cancellationToken = default) =>
        ActiveProvider.TestConnectionAsync(cancellationToken);

    /// <summary>
    /// Tłumaczy gotowe teksty przez AKTUALNY pipeline (słownik → cache → API).
    /// Używane przez tryb live — każdy cykl bierze świeży pipeline, więc zmiana
    /// ustawień (dostawca, tryb prywatny) obowiązuje od następnej klatki.
    /// </summary>
    public Task<IReadOnlyList<TranslationOutcome>> TranslateTextsAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        TranslateTextsAsync(texts, knownLocal: null, cancellationToken);

    /// <summary>
    /// Jak <see cref="TranslateTextsAsync(IReadOnlyList{string}, CancellationToken)"/>, ale z wynikiem
    /// <see cref="TranslateLocalAsync"/> dla tych samych tekstów — znane teksty nie idą drugi raz
    /// do bazy (podwójny licznik użyć). Wynik próby innego pipeline'u (zmiana ustawień między
    /// próbą a tłumaczeniem) jest pomijany: mógłby pochodzić z innego cache albo reguł.
    /// </summary>
    public async Task<IReadOnlyList<TranslationOutcome>> TranslateTextsAsync(
        IReadOnlyList<string> texts, IReadOnlyList<TranslationOutcome?>? knownLocal,
        CancellationToken cancellationToken = default)
    {
        // Token epoki: zmiana ustawień (np. włączenie trybu prywatnego/Cache-only)
        // przerywa także tłumaczenia live będące w locie — stary pipeline nie może
        // dokończyć zapisu na dysk ani wysyłki do API na starych regułach.
        // Pipeline i token bierzemy z JEDNEJ migawki, a po zlinkowaniu sprawdzamy,
        // czy przebudowa nie weszła między odczyt a linkowanie — inaczej stary
        // pipeline pojechałby pod nowym, nieanulowanym tokenem.
        while (true)
        {
            var state = Volatile.Read(ref _pipelineState)
                ?? throw new InvalidOperationException("Pipeline tłumaczenia nie został zbudowany.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.EpochToken);
            if (!ReferenceEquals(state, Volatile.Read(ref _pipelineState)))
            {
                continue;
            }

            var hint = knownLocal is LocalProbe probe && ReferenceEquals(probe.State, state) ? probe.Outcomes : null;
            var outcomes = await state.Pipeline
                .TranslateAsync(texts, settings.SourceLanguage, settings.TargetLanguage, hint, linked.Token)
                .ConfigureAwait(false);
            // A provider may finish despite cancellation. Never publish a result
            // belonging to settings that have already been replaced.
            linked.Token.ThrowIfCancellationRequested();
            return outcomes;
        }
    }

    /// <summary>
    /// Tylko lokalna część <see cref="TranslateTextsAsync"/> (korekty, słownik, cache) —
    /// bez dostawcy i bez rezerwacji budżetu. Null = tekstu nie ma lokalnie. Ta sama
    /// obsługa epoki co w <see cref="TranslateTextsAsync"/>: wynik starego pipeline'u
    /// (np. sprzed włączenia trybu prywatnego) nigdy nie wraca do wołającego.
    /// </summary>
    public async Task<IReadOnlyList<TranslationOutcome?>> TranslateLocalAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var state = Volatile.Read(ref _pipelineState)
                ?? throw new InvalidOperationException("Pipeline tłumaczenia nie został zbudowany.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.EpochToken);
            if (!ReferenceEquals(state, Volatile.Read(ref _pipelineState)))
            {
                continue;
            }

            var outcomes = await state.Pipeline
                .TranslateLocalAsync(texts, settings.SourceLanguage, settings.TargetLanguage, linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new LocalProbe(outcomes, state);
        }
    }

    // Wynik próby lokalnej z migawką pipeline'u, który go dał — tylko ten sam pipeline
    // może go ponownie użyć w TranslateTextsAsync.
    private sealed class LocalProbe(IReadOnlyList<TranslationOutcome?> outcomes, PipelineState state)
        : IReadOnlyList<TranslationOutcome?>
    {
        public PipelineState State { get; } = state;
        public IReadOnlyList<TranslationOutcome?> Outcomes { get; } = outcomes;
        public TranslationOutcome? this[int index] => Outcomes[index];
        public int Count => Outcomes.Count;
        public IEnumerator<TranslationOutcome?> GetEnumerator() => Outcomes.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public string SourceLanguage => settings.SourceLanguage;

    public bool HasCorpus => Volatile.Read(ref _pipelineState)?.Pipeline.HasCorpus == true;

    public bool IsExactCorpusText(string text) =>
        Volatile.Read(ref _pipelineState)?.Pipeline.IsExactCorpusText(text) == true;

    public bool ShouldTranslateLive(string text) =>
        Volatile.Read(ref _pipelineState)?.Pipeline.ShouldTranslateLive(text) ?? JunkFilter.IsMeaningful(text);

    public string? CorpusIdentity(string text) =>
        Volatile.Read(ref _pipelineState)?.Pipeline.CorpusIdentity(text);

    public bool IsCorpusPrefix(string text) =>
        Volatile.Read(ref _pipelineState)?.Pipeline.IsCorpusPrefix(text) == true;
}
