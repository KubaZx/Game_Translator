namespace GameTranslatorOverlay.Core.Usage;

public enum LiveErrorCategory
{
    BlockTracking,
    FastTracking,
    LostTextRead,
    GlyphCover,
    Correction,
}

public sealed record LiveErrorSummary(LiveErrorCategory Category, int Count, string FirstExceptionType);

public sealed class LiveErrorTally
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LiveErrorCategory, (int Count, string FirstType)> _entries = [];

    public int Total
    {
        get
        {
            lock (_gate) return _entries.Values.Sum(static e => e.Count);
        }
    }

    public bool Record(LiveErrorCategory category, Type exceptionType)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        lock (_gate)
        {
            if (_entries.TryGetValue(category, out var existing))
            {
                _entries[category] = existing with { Count = existing.Count + 1 };
                return false;
            }
            _entries[category] = (1, exceptionType.Name);
            return true;
        }
    }

    public IReadOnlyList<LiveErrorSummary> Summary()
    {
        lock (_gate)
        {
            return _entries
                .OrderBy(static e => e.Key)
                .Select(static e => new LiveErrorSummary(e.Key, e.Value.Count, e.Value.FirstType))
                .ToList();
        }
    }

    public string Describe() =>
        string.Join(", ", Summary().Select(static s => $"{s.Category}={s.Count} ({s.FirstExceptionType})"));
}
