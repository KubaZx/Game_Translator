using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Infrastructure.Caching;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// Zapis liczników użycia to tylko statystyka: gdy baza odrzuca zapis (pełny dysk, tylko do
/// odczytu, blokada), odczyty i właściwe operacje cache muszą nadal działać.
/// </summary>
public sealed class SqliteUsageFlushFailureTests : IDisposable
{
    // Więcej niż próg zbiorczego zapisu liczników (64), żeby zapis na pewno był „należny”.
    private const int EntryCount = 80;

    private readonly string _databasePath;
    private readonly SqliteTranslationCache _cache;

    public SqliteUsageFlushFailureTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gto-tests");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        _cache = new SqliteTranslationCache(_databasePath);
        _cache.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long UseCount(string text)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT use_count FROM translations WHERE normalized_text = $text;";
        command.Parameters.AddWithValue("$text", text);
        return (long)command.ExecuteScalar()!;
    }

    private void BreakUsageFlush() => Execute("""
        CREATE TRIGGER fail_usage BEFORE UPDATE OF use_count ON translations
        BEGIN SELECT RAISE(ABORT, 'usage flush disabled by test'); END;
        """);

    private static string Text(int i) => $"Line number {i}";

    [Fact]
    public async Task Nieudany_zapis_licznikow_nie_psuje_odczytow_ani_zapisow()
    {
        for (var i = 0; i < EntryCount; i++)
        {
            await _cache.StoreAsync(new NewCacheEntry(Text(i), Text(i), "en", "pl", $"Linia {i}", "DeepL"));
        }
        BreakUsageFlush();

        // Każdy pierwszy odczyt idzie do bazy; po przekroczeniu progu zapis liczników jest
        // należny, ale jego błąd nie może wyjść z odczytu.
        for (var i = 0; i < EntryCount; i++)
        {
            var hit = await _cache.LookupAsync(Text(i), "en", "pl", "");
            Assert.Equal($"Linia {i}", hit!.TranslatedText);
        }

        await _cache.StoreAsync(new NewCacheEntry("Extra", "Extra", "en", "pl", "Dodatkowy", "DeepL"));
        await _cache.SaveManualCorrectionAsync(new NewCacheEntry(Text(0), Text(0), "en", "pl", "Wiersz 0", "manual"));
        var stats = await _cache.GetStatsAsync();
        var json = await _cache.ExportJsonAsync();

        Assert.Equal(EntryCount + 1, stats.TotalEntries);
        Assert.Equal(1, stats.ManualEntries);
        Assert.Contains("Dodatkowy", json);
        Assert.Equal("Wiersz 0", (await _cache.LookupAsync(Text(0), "en", "pl", ""))!.TranslatedText);
    }

    [Fact]
    public async Task Liczniki_nie_gina_i_trafiaja_do_bazy_po_ustaniu_bledu()
    {
        for (var i = 0; i < EntryCount; i++)
        {
            await _cache.StoreAsync(new NewCacheEntry(Text(i), Text(i), "en", "pl", $"Linia {i}", "DeepL"));
        }
        BreakUsageFlush();
        for (var i = 0; i < EntryCount; i++)
        {
            await _cache.LookupAsync(Text(i), "en", "pl", "");
        }
        Assert.Equal(1, UseCount(Text(5)));

        Execute("DROP TRIGGER fail_usage;");
        // Nieudany zapis w tle mógł jeszcze nie oddać liczników do kolejki — ponawiamy
        // tak, jak zrobi to aplikacja przy kolejnej operacji albo zamknięciu.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            _cache.FlushUsageStatistics();
            if (UseCount(Text(5)) == 2 && UseCount(Text(EntryCount - 1)) == 2) break;
            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Equal(2, UseCount(Text(5)));
        Assert.Equal(2, UseCount(Text(EntryCount - 1)));
    }

    [Fact]
    public async Task Czyszczenie_i_usuwanie_starych_wpisow_dzialaja_mimo_bledu_licznikow()
    {
        for (var i = 0; i < EntryCount; i++)
        {
            await _cache.StoreAsync(new NewCacheEntry(Text(i), Text(i), "en", "pl", $"Linia {i}", "DeepL"));
        }
        BreakUsageFlush();
        for (var i = 0; i < EntryCount; i++)
        {
            await _cache.LookupAsync(Text(i), "en", "pl", "");
        }

        Assert.Equal(0, await _cache.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1), keepManualCorrections: true));
        Assert.Equal(EntryCount, await _cache.ClearAsync(keepManualCorrections: false));
        Assert.Null(await _cache.LookupAsync(Text(1), "en", "pl", ""));
    }
}
