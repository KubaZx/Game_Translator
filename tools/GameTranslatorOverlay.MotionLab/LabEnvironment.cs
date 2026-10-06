using System.Net.Http;
using GameTranslatorOverlay.App.Services;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal sealed record LabOptions(
    string? Cache, string Corpus, string Profile, int ProviderDelayMs, string Placement, string LiveMode,
    double Opacity, double FontSize, string FontFamily);

internal sealed class LabEnvironment : IDisposable
{
    private readonly HttpClient _http;
    private readonly SqliteTranslationCache _cache;
    private readonly bool _keep;

    private LabEnvironment(string root, AppPaths paths, AppSettings settings, TranslationOrchestrator orchestrator,
        UsageTracker usage, HttpClient http, SqliteTranslationCache cache, bool keep)
    {
        Root = root;
        Paths = paths;
        Settings = settings;
        Orchestrator = orchestrator;
        Usage = usage;
        _http = http;
        _cache = cache;
        _keep = keep;
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public TranslationOrchestrator Orchestrator { get; }
    public UsageTracker Usage { get; }

    public static LabEnvironment Create(string root, LabOptions options, IOcrProvider ocr, ILoggerFactory loggers, bool keep)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException($"Katalog roboczy nie jest pusty: {root}");
        var http = new HttpClient(new NoNetworkHandler());
        try
        {
            var paths = new AppPaths(root);
            Directory.CreateDirectory(paths.RootDirectory);
            Directory.CreateDirectory(paths.CorpusDirectory);
            if (options.Cache is { } cache)
            {
                File.Copy(cache, paths.DatabasePath);
                if (File.Exists(cache + "-wal")) File.Copy(cache + "-wal", paths.DatabasePath + "-wal");
            }
            File.Copy(options.Corpus, Path.Combine(paths.CorpusDirectory, options.Profile + CorpusCatalog.FileSuffix));

            var settings = new AppSettings
            {
                SourceLanguage = "en",
                TargetLanguage = "pl",
                Provider = MockTranslationProvider.ProviderName,
                ActiveProfileId = options.Profile,
                CacheOnlyMode = false,
                PrivateMode = options.Cache is null,
                OverlayPlacement = options.Placement,
                LiveDisplayMode = options.LiveMode,
                OverlayFontSize = options.FontSize,
                OverlayFontFamily = options.FontFamily,
                OverlayBackgroundOpacity = options.Opacity,
                ShowOverlayNotices = true,
            };
            var usage = new UsageTracker();
            var persistent = new SqliteTranslationCache(paths.DatabasePath, loggers.CreateLogger<SqliteTranslationCache>());
            persistent.Initialize();
            var orchestrator = new TranslationOrchestrator(
                settings, persistent, new GlossaryService(),
                GlossaryCatalog.CreateDefault(paths), ProfileCatalog.CreateDefault(paths), new UserGlossaryStore(paths),
                new MockTranslationProvider { Delay = TimeSpan.FromMilliseconds(options.ProviderDelayMs) },
                new DeepLTranslationProvider(http, static () => null),
                ocr, usage, loggers,
                corpusCatalog: CorpusCatalog.CreateDefault(paths));
            orchestrator.Initialize();
            if (orchestrator.ActiveProfile is null)
                throw new InvalidOperationException($"Nie znaleziono profilu „{options.Profile}” (profiles/ obok exe).");
            if (!orchestrator.ActiveCorpus.IsLoaded)
                throw new InvalidOperationException($"Korpus nie został wczytany: {orchestrator.ActiveCorpus.Issue ?? "nieznana przyczyna"}.");
            return new LabEnvironment(root, paths, settings, orchestrator, usage, http, persistent, keep);
        }
        catch
        {
            http.Dispose();
            SqliteConnection.ClearAllPools();
            if (!keep) DeleteQuietly(root);
            throw;
        }
    }

    public void Dispose()
    {
        try { _cache.FlushUsageStatistics(); }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException) { }
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        if (!_keep) DeleteQuietly(Root);
    }

    private static void DeleteQuietly(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP jest wyłączone w MotionLab.");
    }
}

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly StreamWriter _writer = new(path, append: false) { AutoFlush = true };
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try { _writer.WriteLine(line); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
            owner.Write(line);
        }
    }
}
