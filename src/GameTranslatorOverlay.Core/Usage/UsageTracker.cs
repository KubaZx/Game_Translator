using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Usage;

/// <summary>
/// Liczniki problemów wykrytych przez kontrolę jakości tłumaczeń w tej sesji — same liczby,
/// bez treści tekstów. <see cref="Retries"/> to ponowienia wysłane do dostawcy.
/// </summary>
public sealed record QualityIssueSummary(long Empty, long NumbersChanged, long Untranslated, long Runaway, long Retries)
{
    public long Total => Empty + NumbersChanged + Untranslated + Runaway;
}

/// <summary>Thread-safe local session counters and admission budget for provider requests.</summary>
public sealed class UsageTracker
{
    private readonly Lock _budgetGate = new();
    private long _apiRequests;
    private long _apiCharacters;
    private long _reservedApiCharacters;
    private long? _sessionCharacterLimit;
    private long _cacheHits;
    private long _glossaryHits;
    private long _failedRequests;
    private long _qualityEmpty;
    private long _qualityNumbers;
    private long _qualityUntranslated;
    private long _qualityRunaway;
    private long _qualityRetries;

    public long ApiRequests { get { lock (_budgetGate) return _apiRequests; } }
    public long ApiCharacters { get { lock (_budgetGate) return _apiCharacters; } }
    public long ReservedApiCharacters { get { lock (_budgetGate) return _reservedApiCharacters; } }
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long GlossaryHits => Interlocked.Read(ref _glossaryHits);
    public long FailedRequests => Interlocked.Read(ref _failedRequests);

    /// <summary>Problemy jakości w wynikach pokazanych graczowi (po ewentualnym ponowieniu).</summary>
    public QualityIssueSummary QualityIssues => new(
        Interlocked.Read(ref _qualityEmpty),
        Interlocked.Read(ref _qualityNumbers),
        Interlocked.Read(ref _qualityUntranslated),
        Interlocked.Read(ref _qualityRunaway),
        Interlocked.Read(ref _qualityRetries));

    /// <summary>Czasy etapów (OCR, dostawca, gotowy napis) w tej sesji aplikacji.</summary>
    public LatencyMonitor Latency { get; } = new();

    /// <summary>
    /// Null means unlimited. Changes apply to the next admission; already admitted
    /// requests retain their reservations and may complete even after the limit is lowered.
    /// </summary>
    public long? SessionCharacterLimit
    {
        get { lock (_budgetGate) return _sessionCharacterLimit; }
        set { lock (_budgetGate) _sessionCharacterLimit = value; }
    }

    /// <summary>
    /// Atomically reserves a non-deduplicated chunk against completed + pending characters.
    /// Null means local denial. Complete the lease after a successful provider response;
    /// disposing it before completion releases the pending budget on failure/cancellation.
    /// These are local counters, not a guarantee of a remote provider's billing outcome.
    /// </summary>
    public ApiRequestReservation? TryReserveApiCharacters(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        lock (_budgetGate)
        {
            if (ExceedsLimitUnderLock(characters)) return null;
            _reservedApiCharacters += characters;
            return new ApiRequestReservation(this, characters);
        }
    }

    /// <summary>Records a completed request directly; concurrent admissions should use a reservation.</summary>
    public void RecordApiRequest(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        lock (_budgetGate)
        {
            _apiRequests++;
            _apiCharacters += characters;
        }
    }

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public void RecordGlossaryHit() => Interlocked.Increment(ref _glossaryHits);
    public void RecordFailure() => Interlocked.Increment(ref _failedRequests);

    /// <summary>Zlicza każdą flagę z <paramref name="kind"/> osobno (wynik może mieć kilka problemów).</summary>
    public void RecordQualityIssue(TranslationQualityFlags kind)
    {
        if (kind.HasFlag(TranslationQualityFlags.Empty)) Interlocked.Increment(ref _qualityEmpty);
        if (kind.HasFlag(TranslationQualityFlags.NumbersChanged)) Interlocked.Increment(ref _qualityNumbers);
        if (kind.HasFlag(TranslationQualityFlags.Untranslated)) Interlocked.Increment(ref _qualityUntranslated);
        if (kind.HasFlag(TranslationQualityFlags.Runaway)) Interlocked.Increment(ref _qualityRunaway);
    }

    public void RecordQualityRetry() => Interlocked.Increment(ref _qualityRetries);

    public bool WouldExceedSessionLimit(int additionalCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalCharacters);
        lock (_budgetGate) return ExceedsLimitUnderLock(additionalCharacters);
    }

    private bool ExceedsLimitUnderLock(int characters) =>
        _sessionCharacterLimit is { } limit
        && (_apiCharacters > limit
            || _reservedApiCharacters > limit - _apiCharacters
            || characters > limit - _apiCharacters - _reservedApiCharacters);

    private void FinishReservation(int characters, bool completed)
    {
        lock (_budgetGate)
        {
            // Release and commit share the admission lock: no gap permitting a second
            // request, and no temporary double counting of pending + completed characters.
            _reservedApiCharacters -= characters;
            if (completed)
            {
                _apiCharacters += characters;
                _apiRequests++;
            }
        }
    }

    /// <summary>Resets completed counters, while active leases continue to reserve their full budget.</summary>
    public void Reset()
    {
        lock (_budgetGate)
        {
            _apiRequests = 0;
            _apiCharacters = 0;
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _glossaryHits, 0);
            Interlocked.Exchange(ref _failedRequests, 0);
            Interlocked.Exchange(ref _qualityEmpty, 0);
            Interlocked.Exchange(ref _qualityNumbers, 0);
            Interlocked.Exchange(ref _qualityUntranslated, 0);
            Interlocked.Exchange(ref _qualityRunaway, 0);
            Interlocked.Exchange(ref _qualityRetries, 0);
        }
        Latency.Reset();
    }

    /// <summary>
    /// An exactly-once local budget lease. Complete and Dispose may be called repeatedly
    /// or race; only the first terminal call changes the owner's counters.
    /// </summary>
    public sealed class ApiRequestReservation : IDisposable
    {
        private UsageTracker? _owner;
        private readonly int _characters;

        internal ApiRequestReservation(UsageTracker owner, int characters)
        {
            _owner = owner;
            _characters = characters;
        }

        public void Complete() =>
            Interlocked.Exchange(ref _owner, null)?.FinishReservation(_characters, completed: true);

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.FinishReservation(_characters, completed: false);
    }
}
