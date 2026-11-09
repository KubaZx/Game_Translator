namespace GameTranslatorOverlay.Core.Translation;

public sealed record FailedTextInfo(TranslationFailureKind Kind, string Message, bool EmptyResult);

public sealed class FailedTextMemory(TimeProvider? timeProvider = null, int capacity = FailedTextMemory.DefaultCapacity)
{
    public const int DefaultCapacity = 512;
    public const int StrikesToBlock = 2;

    public static readonly TimeSpan Quarantine = TimeSpan.FromMinutes(5);

    private sealed record Entry(int Strikes, DateTimeOffset Until, FailedTextInfo Info);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity = Math.Max(1, capacity);

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public bool TryGetBlock(string key, out FailedTextInfo info)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Until <= _time.GetUtcNow())
                {
                    _entries.Remove(key);
                }
                else if (entry.Strikes >= StrikesToBlock)
                {
                    info = entry.Info;
                    return true;
                }
            }
        }
        info = null!;
        return false;
    }

    public void RecordFailure(string key, FailedTextInfo info)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var strikes = _entries.TryGetValue(key, out var entry) && entry.Until > now ? entry.Strikes + 1 : 1;
            if (!_entries.ContainsKey(key) && _entries.Count >= _capacity) MakeRoom(now);
            _entries[key] = new Entry(strikes, now + Quarantine, info);
        }
    }

    public void RecordSuccess(string key)
    {
        lock (_gate) _entries.Remove(key);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var expired in _entries.Where(e => e.Value.Until <= now).Select(static e => e.Key).ToList())
            _entries.Remove(expired);
        if (_entries.Count < _capacity) return;
        var oldest = _entries.MinBy(static e => e.Value.Until).Key;
        _entries.Remove(oldest);
    }
}
