using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Infrastructure.Caching;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class SqliteTranslationCacheTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteTranslationCache _cache;

    public SqliteTranslationCacheTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gto-tests");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        _cache = new SqliteTranslationCache(_databasePath);
        _cache.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static NewCacheEntry Entry(
        string text, string translated, string profile = "", bool manual = false) =>
        new(text, text, "en", "pl", translated, manual ? "manual" : "Mock", profile);

    [Fact]
    public async Task Zapis_i_odczyt_dziala_oraz_zlicza_uzycia()
    {
        await _cache.StoreAsync(Entry("Hello", "Cześć"));

        var first = await _cache.LookupAsync("Hello", "en", "pl", "");
        var second = await _cache.LookupAsync("Hello", "en", "pl", "");

        Assert.NotNull(first);
        Assert.Equal("Cześć", first.TranslatedText);
        Assert.NotNull(second);
        Assert.Equal(3, second.UseCount);
    }

    [Fact]
    public async Task Odczyt_nieistniejacego_wpisu_zwraca_null()
    {
        Assert.Null(await _cache.LookupAsync("Nie ma", "en", "pl", ""));
    }

    [Fact]
    public async Task Wpis_przetrwa_ponowne_otwarcie_bazy()
    {
        await _cache.StoreAsync(Entry("Hello", "Cześć"));

        var reopened = new SqliteTranslationCache(_databasePath);
        var hit = await reopened.LookupAsync("Hello", "en", "pl", "");

        Assert.NotNull(hit);
        Assert.Equal("Cześć", hit.TranslatedText);
    }

    [Fact]
    public async Task Wpis_profilu_ma_pierwszenstwo_przed_globalnym()
    {
        await _cache.StoreAsync(Entry("Hello", "Globalne"));
        await _cache.StoreAsync(Entry("Hello", "Z profilu", profile: "poe2"));

        var withProfile = await _cache.LookupAsync("Hello", "en", "pl", "poe2");
        var withoutProfile = await _cache.LookupAsync("Hello", "en", "pl", "");

        Assert.Equal("Z profilu", withProfile!.TranslatedText);
        Assert.Equal("Globalne", withoutProfile!.TranslatedText);
    }

    [Fact]
    public async Task Reczna_korekta_wygrywa_z_wpisem_profilu()
    {
        await _cache.StoreAsync(Entry("Hello", "Z profilu", profile: "poe2"));
        await _cache.SaveManualCorrectionAsync(Entry("Hello", "Ręczna", manual: true));

        var hit = await _cache.LookupAsync("Hello", "en", "pl", "poe2");

        Assert.Equal("Ręczna", hit!.TranslatedText);
        Assert.True(hit.IsManual);
    }

    [Fact]
    public async Task Automatyczny_zapis_nie_nadpisuje_recznej_korekty()
    {
        await _cache.SaveManualCorrectionAsync(Entry("Hello", "Ręczna"));
        await _cache.StoreAsync(Entry("Hello", "Automatyczna"));

        var hit = await _cache.LookupAsync("Hello", "en", "pl", "");

        Assert.Equal("Ręczna", hit!.TranslatedText);
    }

    [Fact]
    public async Task Czyszczenie_moze_zachowac_reczne_korekty()
    {
        await _cache.StoreAsync(Entry("A", "1"));
        await _cache.SaveManualCorrectionAsync(Entry("B", "2"));

        var removed = await _cache.ClearAsync(keepManualCorrections: true);

        Assert.Equal(1, removed);
        Assert.Null(await _cache.LookupAsync("A", "en", "pl", ""));
        Assert.NotNull(await _cache.LookupAsync("B", "en", "pl", ""));
    }

    [Fact]
    public async Task Statystyki_licza_wpisy_i_rozmiar()
    {
        await _cache.StoreAsync(Entry("A", "1"));
        await _cache.SaveManualCorrectionAsync(Entry("B", "2"));

        var stats = await _cache.GetStatsAsync();

        Assert.Equal(2, stats.TotalEntries);
        Assert.Equal(1, stats.ManualEntries);
        Assert.True(stats.DatabaseSizeBytes > 0);
    }

    [Fact]
    public async Task Usuwanie_starych_wpisow_zachowuje_reczne()
    {
        await _cache.StoreAsync(Entry("A", "1"));
        await _cache.SaveManualCorrectionAsync(Entry("B", "2"));

        var removed = await _cache.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(5), keepManualCorrections: true);

        Assert.Equal(1, removed);
        Assert.NotNull(await _cache.LookupAsync("B", "en", "pl", ""));
    }

    [Fact]
    public async Task Import_recznej_korekty_nadpisuje_wpis_automatyczny()
    {
        await _cache.StoreAsync(Entry("Hello", "Automatyczne"));

        var backup = new GameTranslatorOverlay.Core.Caching.InMemoryTranslationCache();
        await backup.SaveManualCorrectionAsync(Entry("Hello", "Poprawione"));
        var json = await backup.ExportJsonAsync();

        var imported = await _cache.ImportJsonAsync(json);

        Assert.Equal(1, imported);
        var hit = await _cache.LookupAsync("Hello", "en", "pl", "");
        Assert.NotNull(hit);
        Assert.Equal("Poprawione", hit.TranslatedText);
        Assert.True(hit.IsManual);
    }

    [Fact]
    public async Task Eksport_i_import_wykonuja_roundtrip_bez_duplikatow()
    {
        await _cache.StoreAsync(Entry("Hello", "Cześć"));
        var json = await _cache.ExportJsonAsync();

        var importedAgain = await _cache.ImportJsonAsync(json);

        Assert.Equal(0, importedAgain);

        var otherPath = _databasePath + ".other.db";
        var other = new SqliteTranslationCache(otherPath);
        try
        {
            var imported = await other.ImportJsonAsync(json);
            Assert.Equal(1, imported);
            Assert.NotNull(await other.LookupAsync("Hello", "en", "pl", ""));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(otherPath)) File.Delete(otherPath);
        }
    }
}
