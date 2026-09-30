using BenchmarkDotNet.Attributes;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.Benchmarks;

/// <summary>
/// Klatka trybu live, w której każdy tekst na ekranie jest już przetłumaczony: pipeline
/// przechodzi słownik → cache i nie może dotknąć sieci (dostawca rzuca wyjątkiem).
/// Warianty: SQLite z rozgrzaną pamięcią trafień (typowa sytuacja w trakcie gry), SQLite
/// „na zimno” (świeża instancja na tym samym pliku — pierwsza klatka po starcie aplikacji)
/// oraz cache w pamięci (tryb prywatny).
/// </summary>
[MemoryDiagnoser]
public class CacheHitFrameBenchmarks
{
    private const string SourceLanguage = "en";
    private const string TargetLanguage = "pl";

    [Params(20, 100)]
    public int Texts { get; set; }

    private string _directory = string.Empty;
    private string _databasePath = string.Empty;
    private List<string> _frameTexts = [];
    private readonly GlossaryService _glossary = new();

    private TranslationPipeline _sqliteWarm = null!;
    private SqliteTranslationCache _sqliteWarmCache = null!;
    private TranslationPipeline _sqliteCold = null!;
    private SqliteTranslationCache? _sqliteColdCache;
    private TranslationPipeline _inMemory = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Wyłącznie katalog tymczasowy — nigdy %LOCALAPPDATA% z prawdziwym cache gracza.
        _directory = Path.Combine(Path.GetTempPath(), "gto-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "cache.db");

        _frameTexts = Enumerable.Range(0, Texts)
            .Select(static i => $"Quest {i}: talk to the blacksmith about the broken sword number {i}.")
            .ToList();

        var seeding = new SqliteTranslationCache(_databasePath);
        seeding.Initialize();
        var memory = new InMemoryTranslationCache();
        foreach (var text in _frameTexts)
        {
            var entry = new NewCacheEntry(
                text, TextNormalizer.Normalize(text), SourceLanguage, TargetLanguage,
                $"Tłumaczenie: {text}", "DeepL");
            seeding.StoreAsync(entry).GetAwaiter().GetResult();
            memory.StoreAsync(entry).GetAwaiter().GetResult();
        }

        _sqliteWarmCache = new SqliteTranslationCache(_databasePath);
        _sqliteWarm = CreatePipeline(_sqliteWarmCache);
        // Jedna klatka rozgrzewająca wypełnia pamięć trafień — tak jak po kilku sekundach gry.
        EnsureAllCached(_sqliteWarm.TranslateAsync(_frameTexts, SourceLanguage, TargetLanguage).GetAwaiter().GetResult());

        _inMemory = CreatePipeline(memory);
        EnsureAllCached(_inMemory.TranslateAsync(_frameTexts, SourceLanguage, TargetLanguage).GetAwaiter().GetResult());

        // Wariant zimny też musi trafiać w cache: gdyby świeża instancja chybiała (np. po zmianie
        // klucza wyszukiwania), dostawca rzuciłby, pipeline obsłużyłby błąd, a benchmark po cichu
        // mierzyłby ścieżkę błędu zamiast odczytu z pliku.
        var coldProbe = new SqliteTranslationCache(_databasePath);
        EnsureAllCached(CreatePipeline(coldProbe).TranslateAsync(_frameTexts, SourceLanguage, TargetLanguage).GetAwaiter().GetResult());
        coldProbe.FlushUsageStatistics();
    }

    [IterationSetup(Target = nameof(SqliteZimnaPamiec))]
    public void CreateColdSqliteInstance()
    {
        // Poprzednia instancja zapisuje swoje liczniki tu, poza pomiarem — inaczej jej zapis
        // w tle mógłby jeszcze trzymać plik przy sprzątaniu katalogu.
        FlushQuietly(_sqliteColdCache);
        // Nowa instancja = pusta pamięć trafień; każdy odczyt idzie do pliku bazy.
        _sqliteColdCache = new SqliteTranslationCache(_databasePath);
        _sqliteCold = CreatePipeline(_sqliteColdCache);
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<TranslationOutcome>> SqliteRozgrzanaPamiec() =>
        _sqliteWarm.TranslateAsync(_frameTexts, SourceLanguage, TargetLanguage);

    [Benchmark]
    public Task<IReadOnlyList<TranslationOutcome>> SqliteZimnaPamiec() =>
        _sqliteCold.TranslateAsync(_frameTexts, SourceLanguage, TargetLanguage);

    [Benchmark]
    public Task<IReadOnlyList<TranslationOutcome>> PamiecTrybPrywatny() =>
        _inMemory.TranslateAsync(_frameTexts, SourceLanguage, TargetLanguage);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Liczniki użycia zapisywane w tle muszą skończyć, zanim pula połączeń zwolni plik.
        FlushQuietly(_sqliteWarmCache);
        FlushQuietly(_sqliteColdCache);
        SqliteConnection.ClearAllPools();
        // Na Windows plik trzymany jeszcze przez zapis w tle albo skanowany przez antywirusa
        // daje UnauthorizedAccessException zamiast IOException — jedna ponowna próba po chwili,
        // potem odpuszczamy: to katalog tymczasowy, a błąd sprzątania nie może unieważnić pomiaru.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0) Thread.Sleep(200);
            }
        }
    }

    private static void FlushQuietly(SqliteTranslationCache? cache)
    {
        try { cache?.FlushUsageStatistics(); } catch (SqliteException) { }
    }

    private TranslationPipeline CreatePipeline(ITranslationCache cache) =>
        new(_glossary, cache, new ThrowingProvider(), new UsageTracker(), new TranslationPipelineOptions());

    private static void EnsureAllCached(IReadOnlyList<TranslationOutcome> outcomes)
    {
        // Pomiar ma sens tylko wtedy, gdy każdy tekst trafia w cache — inaczej mierzylibyśmy błąd dostawcy.
        if (outcomes.Any(static o => o.Origin != TranslationOrigin.Cache))
            throw new InvalidOperationException("Nie wszystkie teksty klatki trafiły w cache.");
    }

    /// <summary>Dostawca, który nie może zostać wywołany: trafienie w cache nie ma prawa iść do sieci.</summary>
    private sealed class ThrowingProvider : ITranslationProvider
    {
        public string Name => "DeepL";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Benchmark cache: dostawca nie powinien zostać wywołany.");

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "benchmark"));
    }
}
