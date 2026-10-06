using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Infrastructure.Caching;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public sealed class SqliteCorpusCacheStore(SqliteTranslationCache cache) : ICorpusCacheStore
{
    public Task<IReadOnlyList<CachedTranslation?>> PeekAsync(
        IReadOnlyList<string> keys, string sourceLanguage, string targetLanguage, string gameProfile, CancellationToken cancellationToken) =>
        cache.PeekManyAsync(keys, sourceLanguage, targetLanguage, gameProfile, cancellationToken);

    public Task<int> StoreAsync(IReadOnlyList<NewCacheEntry> entries, CancellationToken cancellationToken) =>
        cache.StoreManyAsync(entries, cancellationToken);
}
