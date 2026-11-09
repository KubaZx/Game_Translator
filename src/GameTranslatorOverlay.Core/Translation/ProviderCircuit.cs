namespace GameTranslatorOverlay.Core.Translation;

public enum ProviderPauseKind
{
    None,
    Permanent,
    Temporary,
}

public sealed record ProviderPauseStatus(
    ProviderPauseKind Kind, TranslationFailureKind Failure, string Message, DateTimeOffset? ResumeAt)
{
    public static ProviderPauseStatus Running { get; } = new(ProviderPauseKind.None, TranslationFailureKind.Unknown, string.Empty, null);

    public bool IsPaused => Kind != ProviderPauseKind.None;

    public TranslationException ToException() => new(Failure, Message);
}

public sealed class ProviderCircuit(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    public static readonly TimeSpan ProbeLease = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private ProviderPauseStatus? _permanent;
    private int _temporaryFailures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _probeUntil = DateTimeOffset.MinValue;
    private TranslationFailureKind _lastKind;
    private string _lastMessage = string.Empty;

    public static IReadOnlyList<TimeSpan> Schedule => Backoff;

    public static bool IsPermanent(TranslationFailureKind kind) => kind is
        TranslationFailureKind.MissingApiKey or TranslationFailureKind.InvalidApiKey or TranslationFailureKind.QuotaExceeded
        or TranslationFailureKind.ModelNotFound or TranslationFailureKind.InvalidConfiguration;

    public static bool IsTemporary(TranslationFailureKind kind) => kind is
        TranslationFailureKind.RateLimited or TranslationFailureKind.NetworkError or TranslationFailureKind.Timeout
        or TranslationFailureKind.ServiceUnavailable;

    public ProviderPauseStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_permanent is { } permanent) return permanent;
                var now = _time.GetUtcNow();
                if (_temporaryFailures > 0 && now < _blockedUntil) return Temporary(_blockedUntil);
                return ProviderPauseStatus.Running;
            }
        }
    }

    public ProviderPauseStatus? TryAcquire()
    {
        lock (_gate)
        {
            if (_permanent is { } permanent) return permanent;
            if (_temporaryFailures == 0) return null;
            var now = _time.GetUtcNow();
            if (now < _blockedUntil) return Temporary(_blockedUntil);
            if (now < _probeUntil) return Temporary(_probeUntil);
            _probeUntil = now + ProbeLease;
            return null;
        }
    }

    public void RecordFailure(TranslationFailureKind kind, string message)
    {
        lock (_gate)
        {
            if (IsPermanent(kind))
            {
                _permanent = new ProviderPauseStatus(ProviderPauseKind.Permanent, kind, message, null);
                return;
            }
            if (!IsTemporary(kind))
            {
                RecordResponseUnderLock();
                return;
            }
            _lastKind = kind;
            _lastMessage = message;
            _temporaryFailures++;
            _blockedUntil = _time.GetUtcNow() + Backoff[Math.Min(_temporaryFailures, Backoff.Length) - 1];
            _probeUntil = DateTimeOffset.MinValue;
        }
    }

    public void RecordResponse()
    {
        lock (_gate) RecordResponseUnderLock();
    }

    public void Abandon()
    {
        lock (_gate) _probeUntil = DateTimeOffset.MinValue;
    }

    public void Resume()
    {
        lock (_gate)
        {
            _permanent = null;
            RecordResponseUnderLock();
        }
    }

    private void RecordResponseUnderLock()
    {
        _temporaryFailures = 0;
        _blockedUntil = DateTimeOffset.MinValue;
        _probeUntil = DateTimeOffset.MinValue;
    }

    private ProviderPauseStatus Temporary(DateTimeOffset resumeAt) =>
        new(ProviderPauseKind.Temporary, _lastKind, _lastMessage, resumeAt);
}
