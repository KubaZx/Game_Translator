using System.Security.Cryptography;
using System.Text;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Infrastructure.Caching;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// <see cref="SqliteTranslationCache.LookupManyAsync"/> (cała klatka jednym połączeniem) musi
/// zachowywać się dokładnie jak <see cref="SqliteTranslationCache.LookupAsync"/> wołany po kolei:
/// priorytety wpisów, pamięć trafień z unieważnianiem, liczniki użycia, błędy pojedynczych odczytów.
/// </summary>
public sealed class SqliteBatchLookupTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteTranslationCache _cache;

    public SqliteBatchLookupTests()
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

    private static NewCacheEntry Entry(string text, string translated, string profile = "", bool approved = false) =>
        new(text, text, "en", "pl", translated, "DeepL", profile, IsApproved: approved);

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private async Task SeedAsync()
    {
        await _cache.StoreAsync(Entry("Global only", "Globalne"));
        await _cache.StoreAsync(Entry("Profile wins", "Globalne"));
        await _cache.StoreAsync(Entry("Profile wins", "Z profilu", profile: "poe2"));
        await _cache.StoreAsync(Entry("Approved wins", "Zatwierdzone globalne", approved: true));
        await _cache.StoreAsync(Entry("Approved wins", "Z profilu", profile: "poe2"));
        await _cache.StoreAsync(Entry("Manual wins", "Z profilu", profile: "poe2"));
        await _cache.SaveManualCorrectionAsync(Entry("Manual wins", "Ręczna"));
        await _cache.StoreAsync(Entry("Other profile", "Z innego profilu", profile: "witcher"));
    }

    private static readonly string[] Texts =
        ["Profile wins", "Missing", "Manual wins", "Global only", "Approved wins", "Other profile", "Profile wins"];

    [Fact]
    public async Task Partia_daje_te_same_wpisy_co_pojedyncze_odczyty_z_priorytetami()
    {
        await SeedAsync();
        var sequential = new SqliteTranslationCache(_databasePath);
        var batched = new SqliteTranslationCache(_databasePath);
        // Część tekstów już w pamięci trafień, część nie — partia łączy obie ścieżki.
        await batched.LookupAsync("Global only", "en", "pl", "poe2");
        await sequential.LookupAsync("Global only", "en", "pl", "poe2");

        var expected = new List<CachedTranslation?>();
        foreach (var text in Texts) expected.Add(await sequential.LookupAsync(text, "en", "pl", "poe2"));
        var actual = await batched.LookupManyAsync(Texts, "en", "pl", "poe2");

        Assert.Equal(Texts.Length, actual.Count);
        Assert.All(actual, static result => Assert.Null(result.Error));
        Assert.Equal(
            expected.Select(static e => (e?.Id, e?.TranslatedText, e?.GameProfile, e?.IsManual)),
            actual.Select(static a => (a.Translation?.Id, a.Translation?.TranslatedText, a.Translation?.GameProfile, a.Translation?.IsManual)));
        Assert.Equal(
            ["Z profilu", null, "Ręczna", "Globalne", "Zatwierdzone globalne", null, "Z profilu"],
            actual.Select(static a => a.Translation?.TranslatedText));
    }

    [Fact]
    public async Task Partia_liczy_uzycia_i_zapisuje_je_jak_pojedyncze_odczyty()
    {
        await SeedAsync();
        var cache = new SqliteTranslationCache(_databasePath);

        var first = await cache.LookupManyAsync(Texts, "en", "pl", "poe2");
        var second = await cache.LookupManyAsync(Texts, "en", "pl", "poe2");

        // „Profile wins” jest w partii dwa razy: 1 (zapis) + 2 + 2 odczyty.
        Assert.Equal(3, first[6].Translation!.UseCount);
        Assert.Equal(5, second[6].Translation!.UseCount);
        cache.FlushUsageStatistics();
        var reopened = new SqliteTranslationCache(_databasePath);
        Assert.Equal(6, (await reopened.LookupAsync("Profile wins", "en", "pl", "poe2"))!.UseCount);
    }

    [Fact]
    public async Task Partia_korzysta_z_pamieci_trafien_a_zapis_ja_uniewaznia()
    {
        await _cache.StoreAsync(Entry("Hello", "Stare"));
        Assert.Equal("Stare", (await _cache.LookupManyAsync(["Hello"], "en", "pl", ""))[0].Translation!.TranslatedText);

        // Zmiana poza tą instancją nie jest widoczna — drugi odczyt idzie z pamięci, nie z bazy.
        Execute("UPDATE translations SET translated_text = 'Spoza instancji';");
        Assert.Equal("Stare", (await _cache.LookupManyAsync(["Hello"], "en", "pl", ""))[0].Translation!.TranslatedText);

        // Zapis przez tę instancję unieważnia pamięć tego tekstu we wszystkich profilach.
        await _cache.SaveManualCorrectionAsync(Entry("Hello", "Ręczna"));
        var after = await _cache.LookupManyAsync(["Hello", "Hello"], "en", "pl", "poe2");
        Assert.All(after, static a => Assert.Equal("Ręczna", a.Translation!.TranslatedText));
    }

    [Fact]
    public async Task Blad_odczytu_z_bazy_trafia_do_wyniku_tekstu_a_trafienia_z_pamieci_zostaja()
    {
        await _cache.StoreAsync(Entry("Remembered", "Z pamięci"));
        await _cache.LookupAsync("Remembered", "en", "pl", "");
        Execute("DROP TABLE translations;");

        var results = await _cache.LookupManyAsync(["Remembered", "Not remembered", "Also missing"], "en", "pl", "");

        Assert.Equal("Z pamięci", results[0].Translation!.TranslatedText);
        Assert.Null(results[0].Error);
        Assert.IsType<SqliteException>(results[1].Error);
        Assert.IsType<SqliteException>(results[2].Error);
        Assert.Null(results[1].Translation);
        // Pojedynczy odczyt zgłasza ten sam błąd wyjątkiem — partia tylko go nie rzuca.
        await Assert.ThrowsAsync<SqliteException>(() => _cache.LookupAsync("Not remembered", "en", "pl", ""));
    }

    [Fact]
    public async Task Niedostepna_baza_daje_blad_kazdego_odczytu_zamiast_wyjatku_partii()
    {
        var directoryAsFile = Path.Combine(Path.GetTempPath(), "gto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryAsFile);
        try
        {
            var broken = new SqliteTranslationCache(directoryAsFile);
            var results = await broken.LookupManyAsync(["A", "B"], "en", "pl", "");
            Assert.All(results, static r => Assert.IsType<CacheStorageException>(r.Error));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directoryAsFile, recursive: true);
        }
    }

    [Fact]
    public async Task Anulowana_partia_rzuca_wyjatek_anulowania()
    {
        await _cache.StoreAsync(Entry("Hello", "Cześć"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _cache.LookupManyAsync(["Hello", "Missing"], "en", "pl", "", cts.Token));
    }

    [Fact]
    public async Task Rownolegle_partie_i_pojedyncze_odczyty_licza_kazde_uzycie()
    {
        await _cache.StoreAsync(Entry("Hello", "Cześć"));
        await _cache.StoreAsync(Entry("World", "Świat"));
        var fresh = new SqliteTranslationCache(_databasePath);

        // Dwa sloty tłumaczeń i próba live naraz, na zimnej pamięci trafień.
        var batches = Enumerable.Range(0, 20).Select(_ => fresh.LookupManyAsync(["Hello", "World"], "en", "pl", ""));
        var singles = Enumerable.Range(0, 20).Select(_ => fresh.LookupAsync("Hello", "en", "pl", ""));
        var batchResults = await Task.WhenAll(batches);
        var singleResults = await Task.WhenAll(singles);
        Assert.All(batchResults, static r => Assert.Equal(["Cześć", "Świat"], r.Select(static x => x.Translation!.TranslatedText)));
        Assert.All(singleResults, static r => Assert.Equal("Cześć", r!.TranslatedText));

        fresh.FlushUsageStatistics();
        var reopened = new SqliteTranslationCache(_databasePath);
        Assert.Equal(1 + 40 + 1, (await reopened.LookupAsync("Hello", "en", "pl", ""))!.UseCount);
        Assert.Equal(1 + 20 + 1, (await reopened.LookupAsync("World", "en", "pl", ""))!.UseCount);
    }

    [Fact]
    public async Task Kolumna_text_hash_ma_niezmieniony_format()
    {
        await _cache.StoreAsync(Entry("Zażółć gęślą jaźń", "x"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_hash FROM translations;";
        var stored = (string)command.ExecuteScalar()!;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Zażółć gęślą jaźń"))), stored);
        // Istniejąca baza (wpis zapisany wcześniej) jest nadal znajdowana przez świeżą instancję partią.
        var reopened = new SqliteTranslationCache(_databasePath);
        Assert.Equal("x", (await reopened.LookupManyAsync(["Zażółć gęślą jaźń"], "en", "pl", ""))[0].Translation!.TranslatedText);
    }
}
