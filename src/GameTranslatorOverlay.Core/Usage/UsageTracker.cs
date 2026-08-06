namespace GameTranslatorOverlay.Core.Usage;

/// <summary>Liczniki sesji: zapytania do API, znaki, trafienia w cache i słownik. Bezpieczny wątkowo.</summary>
public sealed class UsageTracker
{
    private long _apiRequests;
    private long _apiCharacters;
    private long _cacheHits;
    private long _glossaryHits;
    private long _failedRequests;

    public long ApiRequests => Interlocked.Read(ref _apiRequests);
    public long ApiCharacters => Interlocked.Read(ref _apiCharacters);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long GlossaryHits => Interlocked.Read(ref _glossaryHits);
    public long FailedRequests => Interlocked.Read(ref _failedRequests);

    /// <summary>Limit znaków wysyłanych do API w jednej sesji. Null = bez limitu.</summary>
    public long? SessionCharacterLimit { get; set; }

    public void RecordApiRequest(int characters)
    {
        Interlocked.Increment(ref _apiRequests);
        Interlocked.Add(ref _apiCharacters, characters);
    }

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public void RecordGlossaryHit() => Interlocked.Increment(ref _glossaryHits);
    public void RecordFailure() => Interlocked.Increment(ref _failedRequests);

    public bool WouldExceedSessionLimit(int additionalCharacters) =>
        SessionCharacterLimit is { } limit && ApiCharacters + additionalCharacters > limit;

    public void Reset()
    {
        Interlocked.Exchange(ref _apiRequests, 0);
        Interlocked.Exchange(ref _apiCharacters, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _glossaryHits, 0);
        Interlocked.Exchange(ref _failedRequests, 0);
    }
}
