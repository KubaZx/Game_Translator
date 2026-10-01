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
    long UseCount)
{
    /// <summary>Znacznik formatu/kontekstu wpisu (np. <c>reflow-1</c>); null we wpisach sprzed jego wprowadzenia.</summary>
    public string? Context { get; init; }
}

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

/// <summary>
/// Wynik jednego odczytu z <see cref="ITranslationCache.LookupManyAsync"/>: wpis (null = brak)
/// albo błąd tego jednego odczytu (<paramref name="Error"/>), dokładnie ten, który rzuciłby
/// <see cref="ITranslationCache.LookupAsync"/> dla tego tekstu.
/// </summary>
public readonly record struct CacheLookupResult(CachedTranslation? Translation, Exception? Error = null);

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

    /// <summary>
    /// Odczyt wielu tekstów jednej klatki naraz — wynik i skutki uboczne (liczniki użycia,
    /// pamięć trafień) takie jak przy <see cref="LookupAsync"/> wołanym po kolei dla każdego
    /// tekstu, w tej samej kolejności. Błąd jednego odczytu nie przerywa pozostałych — trafia
    /// do <see cref="CacheLookupResult.Error"/>; anulowanie przerywa całość wyjątkiem.
    /// Implementacja domyślna woła <see cref="LookupAsync"/> po kolei; cache trwały może zrobić
    /// to taniej (jedno połączenie i jedno przygotowane zapytanie na całą partię).
    /// </summary>
    async Task<IReadOnlyList<CacheLookupResult>> LookupManyAsync(
        IReadOnlyList<string> normalizedTexts, string sourceLanguage, string targetLanguage,
        string gameProfile, CancellationToken cancellationToken = default)
    {
        var results = new CacheLookupResult[normalizedTexts.Count];
        for (var i = 0; i < results.Length; i++)
        {
            try
            {
                results[i] = new CacheLookupResult(await LookupAsync(
                        normalizedTexts[i], sourceLanguage, targetLanguage, gameProfile, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[i] = new CacheLookupResult(null, ex);
            }
        }
        return results;
    }

    Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Zapisuje ręczną korektę — nadpisuje istniejący wpis i chroni go przed automatycznym nadpisaniem.</summary>
    Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default);

    Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections, CancellationToken cancellationToken = default);
    Task<string> ExportJsonAsync(CancellationToken cancellationToken = default);
    Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default);
}
