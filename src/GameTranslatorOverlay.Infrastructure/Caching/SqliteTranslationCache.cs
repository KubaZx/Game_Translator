using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Caching;

public sealed class CacheStorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Trwały cache tłumaczeń w SQLite. Migracje przez PRAGMA user_version.
/// Priorytet odczytu: ręczna korekta → wpis profilu gry → wpis globalny.
/// Trafienia są pamiętane w RAM (tryb live pyta o te same teksty w każdej klatce),
/// a liczniki użycia trafiają do bazy zbiorczo zamiast zapisu przy każdym odczycie.
/// Pamięć jest unieważniana przy każdym zapisie przez tę instancję — aplikacja
/// używa jednej instancji na plik bazy.
/// </summary>
public sealed class SqliteTranslationCache : ITranslationCache
{
    private const int MaxMemoryEntries = 5000;
    private const int UsageFlushThreshold = 64;
    private static readonly TimeSpan UsageFlushInterval = TimeSpan.FromSeconds(30);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly Lock _initGate = new();
    private volatile bool _initialized;

    private sealed class MemoEntry(CachedTranslation value)
    {
        public CachedTranslation Value { get; } = value;
        public long Uses = value.UseCount;
    }

    private readonly record struct PendingUsage(long Uses, DateTimeOffset LastUsed);

    private readonly ConcurrentDictionary<string, MemoEntry> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, PendingUsage> _pendingUsage = new();
    private readonly Lock _flushGate = new();
    // Wersja pamięci: odczyt z bazy trwający w czasie zapisu nie może wstawić starej wartości.
    private readonly Lock _memoryGate = new();
    private long _memoryVersion;
    private long _lastFlushTicks = Environment.TickCount64;
    private int _backgroundFlush;

    public SqliteTranslationCache(string databasePath, ILogger<SqliteTranslationCache>? logger = null)
    {
        _databasePath = databasePath;
        _logger = logger ?? NullLogger<SqliteTranslationCache>.Instance;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public void Initialize()
    {
        if (_initialized) return;
        lock (_initGate)
        {
            if (_initialized) return;
            try
            {
                using var connection = OpenConnection(initializing: true);
                Migrate(connection);
                _initialized = true;
            }
            catch (SqliteException ex)
            {
                throw new CacheStorageException(
                    $"Nie udało się otworzyć bazy cache ({_databasePath}). Plik może być uszkodzony albo zablokowany — " +
                    "zamknij inne kopie aplikacji, a w ostateczności usuń plik: aplikacja utworzy nową, pustą bazę.",
                    ex);
            }
        }
    }

    private SqliteConnection OpenConnection(bool initializing = false)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            // Przy uszkodzonym pliku dopiero ta instrukcja padnie (SQLite otwiera plik leniwie).
            // Tryb WAL jest zapisany w pliku bazy, więc wystarczy ustawić go przy inicjalizacji.
            // synchronous=NORMAL w WAL nie grozi uszkodzeniem bazy; zanik zasilania może
            // najwyżej cofnąć ostatnie zapisy — cache i tak da się odbudować.
            pragma.CommandText = initializing
                ? "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"
                : "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            // Bez sprzątnięcia puli wyciekłe połączenie trzymałoby uchwyt pliku
            // i uniemożliwiało użytkownikowi usunięcie uszkodzonej bazy.
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            throw;
        }
    }

    private static void Migrate(SqliteConnection connection)
    {
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS translations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    text_hash TEXT NOT NULL,
                    source_text TEXT NOT NULL,
                    normalized_text TEXT NOT NULL,
                    source_lang TEXT NOT NULL,
                    target_lang TEXT NOT NULL,
                    translated_text TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    game_profile TEXT NOT NULL DEFAULT '',
                    context TEXT NULL,
                    is_manual INTEGER NOT NULL DEFAULT 0,
                    is_approved INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    last_used_at TEXT NOT NULL,
                    use_count INTEGER NOT NULL DEFAULT 1,
                    UNIQUE (text_hash, source_lang, target_lang, game_profile)
                );
                CREATE INDEX IF NOT EXISTS ix_translations_lookup
                    ON translations (text_hash, source_lang, target_lang);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static string MemoryPrefix(string hash, string sourceLanguage, string targetLanguage) =>
        $"{hash}\u001f{sourceLanguage}\u001f{targetLanguage}\u001f";

    public Task<CachedTranslation?> LookupAsync(
        string normalizedText, string sourceLanguage, string targetLanguage,
        string gameProfile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = TextHasher.Sha256Hex(normalizedText);
        var memoryKey = MemoryPrefix(hash, sourceLanguage, targetLanguage) + gameProfile;

        // Trafienie w pamięci: bez wątku tła, połączenia i zapisu na dysk.
        if (_memory.TryGetValue(memoryKey, out var remembered))
        {
            var hit = RecordUse(remembered);
            FlushUsageInBackgroundIfDue();
            return Task.FromResult<CachedTranslation?>(hit);
        }

        return Task.Run<CachedTranslation?>(() =>
        {
            Initialize();
            var version = Interlocked.Read(ref _memoryVersion);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, source_text, normalized_text, translated_text, provider, game_profile,
                       is_manual, is_approved, created_at, last_used_at, use_count, context
                FROM translations
                WHERE text_hash = $hash AND source_lang = $src AND target_lang = $tgt
                  AND game_profile IN ($profile, '')
                ORDER BY is_manual DESC, is_approved DESC,
                         CASE WHEN game_profile = $profile THEN 0 ELSE 1 END
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$src", sourceLanguage);
            command.Parameters.AddWithValue("$tgt", targetLanguage);
            command.Parameters.AddWithValue("$profile", gameProfile);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var result = new CachedTranslation(
                Id: reader.GetInt64(0),
                SourceText: reader.GetString(1),
                NormalizedText: reader.GetString(2),
                TranslatedText: reader.GetString(3),
                Provider: reader.GetString(4),
                GameProfile: reader.GetString(5),
                IsManual: reader.GetInt64(6) != 0,
                IsApproved: reader.GetInt64(7) != 0,
                CreatedAt: ParseTimestamp(reader.GetString(8)),
                LastUsedAt: ParseTimestamp(reader.GetString(9)),
                UseCount: reader.GetInt64(10))
            {
                Context = reader.IsDBNull(11) ? null : reader.GetString(11),
            };
            reader.Close();

            // Liczniki niezapisane jeszcze w bazie wliczamy do zwracanej wartości.
            var pendingUses = _pendingUsage.TryGetValue(result.Id, out var pending) ? pending.Uses : 0;
            var entry = new MemoEntry(result with { UseCount = result.UseCount + pendingUses });
            lock (_memoryGate)
            {
                if (Interlocked.Read(ref _memoryVersion) == version)
                {
                    if (_memory.Count >= MaxMemoryEntries) _memory.Clear();
                    entry = _memory.GetOrAdd(memoryKey, entry);
                }
            }
            var hit = RecordUse(entry);
            // Odczyt nigdy nie zapisuje synchronicznie: zapis liczników może czekać do 30 s na
            // blokadę albo paść (pełny dysk, plik tylko do odczytu) — a to tylko statystyka.
            FlushUsageInBackgroundIfDue();
            return hit;
        }, cancellationToken);
    }

    private CachedTranslation RecordUse(MemoEntry entry)
    {
        var uses = Interlocked.Increment(ref entry.Uses);
        var now = DateTimeOffset.UtcNow;
        _pendingUsage.AddOrUpdate(entry.Value.Id,
            static (_, time) => new PendingUsage(1, time),
            static (_, current, time) => new PendingUsage(current.Uses + 1, time),
            now);
        return entry.Value with { UseCount = uses, LastUsedAt = now };
    }

    private bool IsUsageFlushDue() =>
        _pendingUsage.Count >= UsageFlushThreshold
        || (!_pendingUsage.IsEmpty && Environment.TickCount64 - Interlocked.Read(ref _lastFlushTicks) >= UsageFlushInterval.TotalMilliseconds);

    /// <summary>
    /// Zapis liczników przed właściwą operacją (zapis, statystyki, czyszczenie, eksport).
    /// Błąd statystyk jest logowany i nie blokuje operacji — liczniki wracają do kolejki.
    /// </summary>
    private void TryFlushUsage(SqliteConnection connection)
    {
        try
        {
            FlushUsage(connection);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Nie udało się zapisać liczników użycia cache (SQLite {Code}) — spróbuję ponownie później",
                ex.SqliteErrorCode);
        }
    }

    private void FlushUsageInBackgroundIfDue()
    {
        if (!IsUsageFlushDue() || Interlocked.Exchange(ref _backgroundFlush, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                FlushUsageStatistics();
            }
            catch (Exception ex) when (ex is SqliteException or IOException or CacheStorageException)
            {
                // Liczniki użycia to tylko statystyka — chwilowo zablokowana baza nie może
                // przerywać tłumaczenia. Kolejna próba nastąpi przy następnym zapisie.
                _logger.LogDebug(ex, "Zapis liczników użycia cache w tle nie powiódł się");
            }
            finally
            {
                Volatile.Write(ref _backgroundFlush, 0);
            }
        });
    }

    /// <summary>
    /// Zapisuje zebrane liczniki użycia (use_count, last_used_at) w jednej transakcji.
    /// Wołane automatycznie; aplikacja wywołuje je także przy zamknięciu.
    /// </summary>
    public void FlushUsageStatistics()
    {
        if (_pendingUsage.IsEmpty) return;
        Initialize();
        using var connection = OpenConnection();
        FlushUsage(connection);
    }

    private void FlushUsage(SqliteConnection connection)
    {
        lock (_flushGate)
        {
            Interlocked.Exchange(ref _lastFlushTicks, Environment.TickCount64);
            if (_pendingUsage.IsEmpty) return;

            var taken = new List<(long Id, PendingUsage Usage)>();
            foreach (var id in _pendingUsage.Keys)
            {
                if (_pendingUsage.TryRemove(id, out var usage)) taken.Add((id, usage));
            }
            if (taken.Count == 0) return;

            try
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE translations
                    SET use_count = use_count + $uses,
                        last_used_at = MAX(last_used_at, $now)
                    WHERE id = $id;
                    """;
                var uses = command.Parameters.Add("$uses", SqliteType.Integer);
                var now = command.Parameters.Add("$now", SqliteType.Text);
                var id = command.Parameters.Add("$id", SqliteType.Integer);
                foreach (var (entryId, usage) in taken)
                {
                    uses.Value = usage.Uses;
                    now.Value = FormatTimestamp(usage.LastUsed);
                    id.Value = entryId;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch
            {
                // Nieudany zapis nie gubi liczników — wracają do kolejki.
                foreach (var (entryId, usage) in taken)
                {
                    _pendingUsage.AddOrUpdate(entryId, usage,
                        (_, current) => new PendingUsage(current.Uses + usage.Uses,
                            current.LastUsed > usage.LastUsed ? current.LastUsed : usage.LastUsed));
                }
                throw;
            }
        }
    }

    /// <summary>Zapis zmienia wynik odczytu dla tego tekstu we wszystkich profilach.</summary>
    private void ForgetMemory(NewCacheEntry entry)
    {
        var prefix = MemoryPrefix(TextHasher.Sha256Hex(entry.NormalizedText), entry.SourceLanguage, entry.TargetLanguage);
        lock (_memoryGate)
        {
            Interlocked.Increment(ref _memoryVersion);
            foreach (var key in _memory.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) _memory.TryRemove(key, out _);
            }
        }
    }

    private void ForgetAllMemory()
    {
        lock (_memoryGate)
        {
            Interlocked.Increment(ref _memoryVersion);
            _memory.Clear();
        }
    }

    public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            TryFlushUsage(connection);
            UpsertEntry(connection, entry, manualOverwrite: false);
            ForgetMemory(entry);
        }, cancellationToken);
    }

    public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            TryFlushUsage(connection);
            UpsertEntry(
                connection,
                entry with { IsManual = true, IsApproved = true, Provider = "manual" },
                manualOverwrite: true);
            ForgetMemory(entry);
        }, cancellationToken);
    }

    private static void UpsertEntry(SqliteConnection connection, NewCacheEntry entry, bool manualOverwrite, SqliteTransaction? transaction = null)
    {
        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO translations
                (text_hash, source_text, normalized_text, source_lang, target_lang, translated_text,
                 provider, game_profile, context, is_manual, is_approved, created_at, last_used_at, use_count)
            VALUES
                ($hash, $source, $normalized, $src, $tgt, $translated,
                 $provider, $profile, $context, $manual, $approved, $now, $now, 1)
            ON CONFLICT (text_hash, source_lang, target_lang, game_profile) DO UPDATE SET
                translated_text = excluded.translated_text,
                provider = excluded.provider,
                context = excluded.context,
                is_manual = excluded.is_manual,
                is_approved = excluded.is_approved,
                last_used_at = excluded.last_used_at
            {(manualOverwrite ? string.Empty : "WHERE translations.is_manual = 0")};
            """;
        command.Parameters.AddWithValue("$hash", TextHasher.Sha256Hex(entry.NormalizedText));
        command.Parameters.AddWithValue("$source", entry.SourceText);
        command.Parameters.AddWithValue("$normalized", entry.NormalizedText);
        command.Parameters.AddWithValue("$src", entry.SourceLanguage);
        command.Parameters.AddWithValue("$tgt", entry.TargetLanguage);
        command.Parameters.AddWithValue("$translated", entry.TranslatedText);
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$profile", entry.GameProfile);
        command.Parameters.AddWithValue("$context", (object?)entry.Context ?? DBNull.Value);
        command.Parameters.AddWithValue("$manual", entry.IsManual ? 1 : 0);
        command.Parameters.AddWithValue("$approved", entry.IsApproved ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            TryFlushUsage(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(is_manual), 0) FROM translations;";
            using var reader = command.ExecuteReader();
            reader.Read();
            var total = reader.GetInt64(0);
            var manual = reader.GetInt64(1);
            var size = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
            return new CacheStats(total, manual, size);
        }, cancellationToken);
    }

    public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            TryFlushUsage(connection);
            using var command = connection.CreateCommand();
            command.CommandText = keepManualCorrections
                ? "DELETE FROM translations WHERE is_manual = 0;"
                : "DELETE FROM translations;";
            var removed = command.ExecuteNonQuery();
            ForgetAllMemory();
            return removed;
        }, cancellationToken);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            // Najpierw zapisujemy użycia — inaczej usunęlibyśmy wpisy czytane przed chwilą.
            // Gdy ten zapis się nie uda (np. zablokowana baza), sprzątanie i tak idzie dalej:
            // w najgorszym razie zniknie wpis, który trzeba będzie przetłumaczyć ponownie.
            TryFlushUsage(connection);
            using var command = connection.CreateCommand();
            command.CommandText = keepManualCorrections
                ? "DELETE FROM translations WHERE last_used_at < $cutoff AND is_manual = 0;"
                : "DELETE FROM translations WHERE last_used_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
            var removed = command.ExecuteNonQuery();
            ForgetAllMemory();
            return removed;
        }, cancellationToken);
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            var entries = new List<CacheExportEntry>();
            using var connection = OpenConnection();
            TryFlushUsage(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_text, normalized_text, source_lang, target_lang, translated_text,
                       provider, game_profile, is_manual, is_approved, context
                FROM translations;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new CacheExportEntry(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetInt64(7) != 0, reader.GetInt64(8) != 0,
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
            return JsonSerializer.Serialize(entries, CacheExportEntry.JsonOptions);
        }, cancellationToken);
    }

    public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            var entries = JsonSerializer.Deserialize<List<CacheExportEntry>>(json, CacheExportEntry.JsonOptions) ?? [];
            var imported = 0;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var entry in entries)
            {
                // Importowana ręczna korekta nadpisuje istniejący wpis (zachowanie priorytetu
                // korekt); zwykłe wpisy nie nadpisują niczego.
                if (entry.IsManual)
                {
                    UpsertEntry(connection, entry.ToNewCacheEntry(), manualOverwrite: true, transaction);
                    imported++;
                    continue;
                }

                var now = FormatTimestamp(DateTimeOffset.UtcNow);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO translations
                        (text_hash, source_text, normalized_text, source_lang, target_lang, translated_text,
                         provider, game_profile, context, is_manual, is_approved, created_at, last_used_at, use_count)
                    VALUES
                        ($hash, $source, $normalized, $src, $tgt, $translated,
                         $provider, $profile, $context, $manual, $approved, $now, $now, 1);
                    """;
                command.Parameters.AddWithValue("$hash", TextHasher.Sha256Hex(entry.NormalizedText));
                command.Parameters.AddWithValue("$source", entry.SourceText);
                command.Parameters.AddWithValue("$normalized", entry.NormalizedText);
                command.Parameters.AddWithValue("$src", entry.SourceLanguage);
                command.Parameters.AddWithValue("$tgt", entry.TargetLanguage);
                command.Parameters.AddWithValue("$translated", entry.TranslatedText);
                command.Parameters.AddWithValue("$provider", entry.Provider);
                command.Parameters.AddWithValue("$profile", entry.GameProfile);
                command.Parameters.AddWithValue("$context", (object?)entry.Context ?? DBNull.Value);
                command.Parameters.AddWithValue("$manual", entry.IsManual ? 1 : 0);
                command.Parameters.AddWithValue("$approved", entry.IsApproved ? 1 : 0);
                command.Parameters.AddWithValue("$now", now);
                imported += command.ExecuteNonQuery();
            }
            transaction.Commit();
            ForgetAllMemory();
            return imported;
        }, cancellationToken);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
}
