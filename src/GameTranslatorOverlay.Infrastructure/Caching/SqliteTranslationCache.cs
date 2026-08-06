using System.Globalization;
using System.Text.Json;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Text;
using Microsoft.Data.Sqlite;

namespace GameTranslatorOverlay.Infrastructure.Caching;

public sealed class CacheStorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Trwały cache tłumaczeń w SQLite. Migracje przez PRAGMA user_version.
/// Priorytet odczytu: ręczna korekta → wpis profilu gry → wpis globalny.
/// </summary>
public sealed class SqliteTranslationCache : ITranslationCache
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initGate = new();
    private volatile bool _initialized;

    public SqliteTranslationCache(string databasePath)
    {
        _databasePath = databasePath;
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
                using var connection = OpenConnection();
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

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            // Przy uszkodzonym pliku dopiero ta instrukcja padnie (SQLite otwiera plik leniwie).
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
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

    public Task<CachedTranslation?> LookupAsync(
        string normalizedText, string sourceLanguage, string targetLanguage,
        string gameProfile, CancellationToken cancellationToken = default)
    {
        return Task.Run<CachedTranslation?>(() =>
        {
            Initialize();
            var hash = TextHasher.Sha256Hex(normalizedText);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, source_text, normalized_text, translated_text, provider, game_profile,
                       is_manual, is_approved, created_at, last_used_at, use_count
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
                UseCount: reader.GetInt64(10));
            reader.Close();

            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE translations SET use_count = use_count + 1, last_used_at = $now WHERE id = $id;";
            touch.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
            touch.Parameters.AddWithValue("$id", result.Id);
            touch.ExecuteNonQuery();

            return result with { UseCount = result.UseCount + 1 };
        }, cancellationToken);
    }

    public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            UpsertEntry(connection, entry, manualOverwrite: false);
        }, cancellationToken);
    }

    public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            UpsertEntry(
                connection,
                entry with { IsManual = true, IsApproved = true, Provider = "manual" },
                manualOverwrite: true);
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
            using var command = connection.CreateCommand();
            command.CommandText = keepManualCorrections
                ? "DELETE FROM translations WHERE is_manual = 0;"
                : "DELETE FROM translations;";
            return command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = keepManualCorrections
                ? "DELETE FROM translations WHERE last_used_at < $cutoff AND is_manual = 0;"
                : "DELETE FROM translations WHERE last_used_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
            return command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Initialize();
            var entries = new List<CacheExportEntry>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_text, normalized_text, source_lang, target_lang, translated_text,
                       provider, game_profile, is_manual, is_approved
                FROM translations;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new CacheExportEntry(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetInt64(7) != 0, reader.GetInt64(8) != 0));
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
                         provider, game_profile, is_manual, is_approved, created_at, last_used_at, use_count)
                    VALUES
                        ($hash, $source, $normalized, $src, $tgt, $translated,
                         $provider, $profile, $manual, $approved, $now, $now, 1);
                    """;
                command.Parameters.AddWithValue("$hash", TextHasher.Sha256Hex(entry.NormalizedText));
                command.Parameters.AddWithValue("$source", entry.SourceText);
                command.Parameters.AddWithValue("$normalized", entry.NormalizedText);
                command.Parameters.AddWithValue("$src", entry.SourceLanguage);
                command.Parameters.AddWithValue("$tgt", entry.TargetLanguage);
                command.Parameters.AddWithValue("$translated", entry.TranslatedText);
                command.Parameters.AddWithValue("$provider", entry.Provider);
                command.Parameters.AddWithValue("$profile", entry.GameProfile);
                command.Parameters.AddWithValue("$manual", entry.IsManual ? 1 : 0);
                command.Parameters.AddWithValue("$approved", entry.IsApproved ? 1 : 0);
                command.Parameters.AddWithValue("$now", now);
                imported += command.ExecuteNonQuery();
            }
            transaction.Commit();
            return imported;
        }, cancellationToken);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
}
