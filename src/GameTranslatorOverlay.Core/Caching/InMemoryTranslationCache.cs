using System.Collections.Concurrent;
using System.Text.Json;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Caching;

/// <summary>
/// Ulotny cache w pamięci — używany w trybie prywatnym (nic nie trafia na dysk)
/// oraz w testach. Po zakończeniu sesji wystarczy porzucić instancję.
/// </summary>
public sealed class InMemoryTranslationCache : ITranslationCache
{
    private sealed record Entry(CachedTranslation Translation, string SourceLanguage, string TargetLanguage);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private long _nextId;

    private static string Key(string hash, string src, string tgt, string profile) => $"{hash}|{src}|{tgt}|{profile}";

    public Task<CachedTranslation?> LookupAsync(
        string normalizedText, string sourceLanguage, string targetLanguage,
        string gameProfile, CancellationToken cancellationToken = default)
    {
        var hash = TextHasher.Sha256Hex(normalizedText);
        Entry? best = null;

        if (gameProfile.Length > 0 && _entries.TryGetValue(Key(hash, sourceLanguage, targetLanguage, gameProfile), out var profileEntry))
        {
            best = profileEntry;
        }

        if (_entries.TryGetValue(Key(hash, sourceLanguage, targetLanguage, string.Empty), out var globalEntry))
        {
            if (best is null || (!best.Translation.IsManual && globalEntry.Translation.IsManual))
            {
                best = globalEntry;
            }
        }

        if (best is null) return Task.FromResult<CachedTranslation?>(null);

        var updated = best.Translation with { LastUsedAt = DateTimeOffset.UtcNow, UseCount = best.Translation.UseCount + 1 };
        _entries[Key(hash, sourceLanguage, targetLanguage, updated.GameProfile)] = best with { Translation = updated };
        return Task.FromResult<CachedTranslation?>(updated);
    }

    public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
    {
        Upsert(entry, manualOverwrite: false);
        return Task.CompletedTask;
    }

    public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
    {
        Upsert(entry with { IsManual = true, IsApproved = true, Provider = "manual" }, manualOverwrite: true);
        return Task.CompletedTask;
    }

    private void Upsert(NewCacheEntry entry, bool manualOverwrite)
    {
        var hash = TextHasher.Sha256Hex(entry.NormalizedText);
        var key = Key(hash, entry.SourceLanguage, entry.TargetLanguage, entry.GameProfile);
        var now = DateTimeOffset.UtcNow;

        _entries.AddOrUpdate(
            key,
            _ => new Entry(
                new CachedTranslation(
                    Interlocked.Increment(ref _nextId), entry.SourceText, entry.NormalizedText,
                    entry.TranslatedText, entry.Provider, entry.GameProfile,
                    entry.IsManual, entry.IsApproved, now, now, 1),
                entry.SourceLanguage, entry.TargetLanguage),
            (_, existing) =>
            {
                if (existing.Translation.IsManual && !manualOverwrite) return existing;
                return existing with
                {
                    Translation = existing.Translation with
                    {
                        TranslatedText = entry.TranslatedText,
                        Provider = entry.Provider,
                        IsManual = entry.IsManual,
                        IsApproved = entry.IsApproved,
                        LastUsedAt = now,
                    },
                };
            });
    }

    public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var entries = _entries.Values.ToList();
        return Task.FromResult(new CacheStats(entries.Count, entries.Count(static e => e.Translation.IsManual), 0));
    }

    public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var (key, entry) in _entries.ToList())
        {
            if (keepManualCorrections && entry.Translation.IsManual) continue;
            if (_entries.TryRemove(key, out _)) removed++;
        }
        return Task.FromResult(removed);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var (key, entry) in _entries.ToList())
        {
            if (entry.Translation.LastUsedAt >= cutoff) continue;
            if (keepManualCorrections && entry.Translation.IsManual) continue;
            if (_entries.TryRemove(key, out _)) removed++;
        }
        return Task.FromResult(removed);
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default)
    {
        var export = _entries.Values
            .Select(static e => new CacheExportEntry(
                e.Translation.SourceText, e.Translation.NormalizedText, e.SourceLanguage, e.TargetLanguage,
                e.Translation.TranslatedText, e.Translation.Provider, e.Translation.GameProfile,
                e.Translation.IsManual, e.Translation.IsApproved))
            .ToList();
        return Task.FromResult(JsonSerializer.Serialize(export, CacheExportEntry.JsonOptions));
    }

    public async Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var entries = JsonSerializer.Deserialize<List<CacheExportEntry>>(json, CacheExportEntry.JsonOptions) ?? [];
        var imported = 0;
        foreach (var entry in entries)
        {
            var hash = TextHasher.Sha256Hex(entry.NormalizedText);
            var key = Key(hash, entry.SourceLanguage, entry.TargetLanguage, entry.GameProfile);
            if (_entries.ContainsKey(key)) continue;
            await StoreAsync(entry.ToNewCacheEntry(), cancellationToken).ConfigureAwait(false);
            imported++;
        }
        return imported;
    }
}

public sealed record CacheExportEntry(
    string SourceText,
    string NormalizedText,
    string SourceLanguage,
    string TargetLanguage,
    string TranslatedText,
    string Provider,
    string GameProfile,
    bool IsManual,
    bool IsApproved)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public NewCacheEntry ToNewCacheEntry() => new(
        SourceText, NormalizedText, SourceLanguage, TargetLanguage,
        TranslatedText, Provider, GameProfile,
        IsManual: IsManual, IsApproved: IsApproved);
}
