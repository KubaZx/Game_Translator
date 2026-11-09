using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Infrastructure.Caching;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class SqliteDataGuardTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string DatabasePath => Path.Combine(_temp.Path, "cache.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static NewCacheEntry Entry(string text, string translated) =>
        new(text, text, "en", "pl", translated, "Mock", "");

    private void CreateDatabaseWithVersion(int version)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE translations (id INTEGER); PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Baza_z_nowszej_wersji_nie_jest_zapisywana_ani_zmieniana()
    {
        CreateDatabaseWithVersion(CurrentPlusOne());
        var before = File.ReadAllBytes(DatabasePath);
        var cache = new SqliteTranslationCache(DatabasePath);

        var error = Assert.Throws<CacheStorageException>(cache.Initialize);
        await Assert.ThrowsAsync<CacheStorageException>(() => cache.StoreAsync(Entry("Hello", "Cześć")));
        await Assert.ThrowsAsync<CacheStorageException>(() => cache.LookupAsync("Hello", "en", "pl", ""));
        SqliteConnection.ClearAllPools();

        Assert.Contains("nowszej wersji programu", error.Message);
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*corrupt*"));
        Assert.Null(cache.RecoveryNotice);
    }

    private static int CurrentPlusOne() => SqliteTranslationCache.CurrentSchemaVersion + 1;

    [Fact]
    public async Task Uszkodzona_baza_jest_odkladana_a_nowa_dziala_bez_starego_wal()
    {
        File.WriteAllText(DatabasePath, "to nie jest baza SQLite, tylko zwykły tekst o długości większej niż nagłówek pliku bazy");
        File.WriteAllText(DatabasePath + "-wal", "stary wal");
        var cache = new SqliteTranslationCache(DatabasePath);

        cache.Initialize();
        await cache.StoreAsync(Entry("Hello", "Cześć"));
        var hit = await cache.LookupAsync("Hello", "en", "pl", "");

        Assert.Equal("Cześć", hit!.TranslatedText);
        var quarantined = cache.QuarantinedDatabasePath;
        Assert.NotNull(quarantined);
        Assert.StartsWith("cache.corrupt-", Path.GetFileName(quarantined));
        Assert.Equal("to nie jest baza SQLite, tylko zwykły tekst o długości większej niż nagłówek pliku bazy", File.ReadAllText(quarantined!));
        if (File.Exists(DatabasePath + "-wal"))
        {
            using var wal = new FileStream(DatabasePath + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(wal);
            Assert.DoesNotContain("stary wal", reader.ReadToEnd());
        }
        Assert.Contains(Path.GetFileName(quarantined), cache.RecoveryNotice);
        Assert.DoesNotContain("usuń", cache.RecoveryNotice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kolejne_uszkodzenia_nie_nadpisuja_poprzednich_kopii()
    {
        File.WriteAllText(DatabasePath, "pierwsza uszkodzona baza, dostatecznie długa, żeby przekroczyć rozmiar nagłówka SQLite");
        var first = new SqliteTranslationCache(DatabasePath);
        first.Initialize();
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(DatabasePath + suffix)) File.Delete(DatabasePath + suffix);
        }
        File.WriteAllText(DatabasePath, "druga uszkodzona baza, dostatecznie długa, żeby przekroczyć rozmiar nagłówka SQLite");
        var second = new SqliteTranslationCache(DatabasePath);

        second.Initialize();

        Assert.NotEqual(first.QuarantinedDatabasePath, second.QuarantinedDatabasePath);
        Assert.True(File.Exists(first.QuarantinedDatabasePath));
        Assert.True(File.Exists(second.QuarantinedDatabasePath));
    }

    [Fact]
    public void Poprawna_baza_nie_jest_odkladana()
    {
        var cache = new SqliteTranslationCache(DatabasePath);

        cache.Initialize();

        Assert.Null(cache.RecoveryNotice);
        Assert.Null(cache.QuarantinedDatabasePath);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*corrupt*"));
    }
}
