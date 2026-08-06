namespace GameTranslatorOverlay.Core.Caching;

public sealed record CachedTranslation(
    long Id,
    string SourceText,
    string NormalizedText,
    string TranslatedText,
    string Provider,
    string GameProfile,
    bool IsManual,
    bool IsApproved,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    long UseCount);

public sealed record NewCacheEntry(
    string SourceText,
    string NormalizedText,
    string SourceLanguage,
    string TargetLanguage,
    string TranslatedText,
    string Provider,
    string GameProfile = "",
    string? Context = null,
    bool IsManual = false,
    bool IsApproved = false);

public sealed record CacheStats(long TotalEntries, long ManualEntries, long DatabaseSizeBytes);

public interface ITranslationCache
{
    /// <summary>
    /// Szuka tłumaczenia wg priorytetu: ręczna korekta → wpis profilu gry → wpis globalny.
    /// <paramref name="gameProfile"/> pusty string oznacza brak profilu (tylko wpisy globalne).
    /// </summary>
    Task<CachedTranslation?> LookupAsync(
        string normalizedText, string sourceLanguage, string targetLanguage,
        string gameProfile, CancellationToken cancellationToken = default);

    Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Zapisuje ręczną korektę — nadpisuje istniejący wpis i chroni go przed automatycznym nadpisaniem.</summary>
    Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default);

    Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default);
    Task<string> ExportJsonAsync(CancellationToken cancellationToken = default);
    Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default);
}
