using System.Net.Http;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.OverlayPreview;

internal sealed class PreviewTranslator : IDisposable
{
    public const string MockPrefix = "[PL] ";

    private readonly HttpClient _http;
    private readonly string _workDirectory;

    private PreviewTranslator(string workDirectory, HttpClient http, AppSettings settings, TranslationOrchestrator orchestrator, WindowsOcrProvider ocr)
    {
        _workDirectory = workDirectory;
        _http = http;
        Settings = settings;
        Orchestrator = orchestrator;
        Ocr = ocr;
    }

    public AppSettings Settings { get; }
    public TranslationOrchestrator Orchestrator { get; }
    public WindowsOcrProvider Ocr { get; }

    public static PreviewTranslator Create(PreviewOptions options)
    {
        var workDirectory = Path.Combine(options.Output, "_praca-" + Guid.NewGuid().ToString("N"));
        var http = new HttpClient(new NoNetworkHandler());
        try
        {
            Directory.CreateDirectory(workDirectory);
            var paths = new AppPaths(Path.Combine(workDirectory, "app-data"));
            Directory.CreateDirectory(paths.RootDirectory);
            Directory.CreateDirectory(paths.CorpusDirectory);

            var databasePath = Path.Combine(workDirectory, "cache.db");
            if (options.Cache is { } cache)
            {
                File.Copy(cache, databasePath);
                if (File.Exists(cache + "-wal")) File.Copy(cache + "-wal", databasePath + "-wal");
            }

            if (options.Corpus is { } corpus && options.Profile is { } profile)
            {
                File.Copy(corpus, Path.Combine(paths.CorpusDirectory, profile + CorpusCatalog.FileSuffix));
            }

            var settings = new AppSettings
            {
                SourceLanguage = "en",
                TargetLanguage = "pl",
                Provider = MockTranslationProvider.ProviderName,
                ActiveProfileId = options.Profile,
                CacheOnlyMode = false,
                PrivateMode = false,
                OverlayPlacement = options.Placement,
                LiveDisplayMode = "at-source",
                OverlayFontSize = options.FontSize,
                OverlayFontFamily = options.FontFamily,
                OverlayBackgroundOpacity = options.Opacity,
            };

            var ocr = new WindowsOcrProvider();
            var persistentCache = new SqliteTranslationCache(databasePath);
            persistentCache.Initialize();
            var orchestrator = new TranslationOrchestrator(
                settings, persistentCache, new GlossaryService(),
                GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths), new UserGlossaryStore(paths),
                new MockTranslationProvider(), new DeepLTranslationProvider(http, static () => null),
                ocr, new UsageTracker(), NullLoggerFactory.Instance,
                corpusCatalog: CorpusCatalog.CreateDefault(paths));
            orchestrator.Initialize();
            return new PreviewTranslator(workDirectory, http, settings, orchestrator, ocr);
        }
        catch
        {
            http.Dispose();
            DeleteQuietly(workDirectory);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranslationOutcome>> TranslateAsync(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        var local = await Orchestrator.TranslateLocalAsync(texts).ConfigureAwait(false);
        if (local.All(static o => o is not null)) return local.Select(static o => o!).ToList();
        return await Orchestrator.TranslateTextsAsync(texts, local).ConfigureAwait(false);
    }

    public static string OriginLabel(TranslationOutcome outcome)
    {
        var text = outcome.TranslatedText ?? string.Empty;
        var corpus = outcome.Parts?.Any(static p => p.FromCorpus) == true;
        if (outcome.TranslatedText is null) return "brak tłumaczenia";
        if (text.StartsWith(MockPrefix, StringComparison.Ordinal) && !text[MockPrefix.Length..].Contains(MockPrefix, StringComparison.Ordinal)
            && outcome.Parts is not { Count: > 1 })
            return "Mock (brak w bazie)";
        if (text.Contains(MockPrefix, StringComparison.Ordinal)) return "częściowo Mock";
        return outcome.Origin switch
        {
            TranslationOrigin.Glossary => "słownik",
            TranslationOrigin.Cache => corpus ? "baza (korpus)" : "baza",
            TranslationOrigin.Provider => "Mock (brak w bazie)",
            _ => outcome.Origin.ToString(),
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        DeleteQuietly(_workDirectory);
    }

    private static void DeleteQuietly(string directory)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP jest wyłączone w OverlayPreview.");
    }
}
