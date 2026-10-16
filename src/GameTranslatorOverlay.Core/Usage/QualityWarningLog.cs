using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Usage;

public sealed record QualityWarningContext(string ProfileId, string SourceLanguage, string TargetLanguage);

public sealed record QualityWarningEntry(
    long Id,
    string SourceText,
    string NormalizedText,
    string TranslatedText,
    TranslationQualityFlags Issues,
    DateTimeOffset Time,
    string? CacheKey = null)
{
    public string? CorrectedText { get; init; }

    public QualityWarningContext? Context { get; init; }

    public bool PartOfBlock { get; init; }

    public bool IsCorrected => CorrectedText is not null;

    public string Reason => TranslationQualityGate.ShortReason(Issues) ?? string.Empty;

    public string Hint => TranslationQualityGate.Hint(Issues, PartOfBlock) ?? string.Empty;
}

public sealed class QualityWarningLog(Func<DateTimeOffset>? clock = null)
{
    public const int Capacity = 20;

    private const int MaxRemembered = 4096;

    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.Now);
    private readonly Lock _gate = new();
    private readonly List<QualityWarningEntry> _entries = new(Capacity);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private long _nextId;
    private long _total;
    private long _version;
    private long _generation;

    public Func<QualityWarningContext?>? ContextSource { get; set; }

    public long Total { get { lock (_gate) return _total; } }

    public long Generation { get { lock (_gate) return _generation; } }

    public long Version { get { lock (_gate) return _version; } }

    public IReadOnlyList<QualityWarningEntry> Recent
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public bool Record(TranslationOutcome? outcome, long? generation = null)
    {
        if (outcome is not { TranslatedText: { } translated } || outcome.QualityIssues == TranslationQualityFlags.None) return false;
        var key = Key(outcome.NormalizedText, translated);
        var context = ContextSource?.Invoke();
        lock (_gate)
        {
            if (generation is { } expected && expected != _generation) return false;
            if (_seen.Count >= MaxRemembered && !_seen.Contains(key))
            {
                _seen.Clear();
                foreach (var entry in _entries) _seen.Add(Key(entry.NormalizedText, entry.TranslatedText));
            }
            if (!_seen.Add(key)) return false;
            _entries.Insert(0, new QualityWarningEntry(
                ++_nextId, outcome.SourceText, outcome.NormalizedText, translated, outcome.QualityIssues, _clock(), outcome.CacheKey)
            {
                Context = context,
                PartOfBlock = outcome.Parts is { Count: > 1 },
            });
            if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
            _total++;
            _version++;
            return true;
        }
    }

    private static string Key(string normalizedText, string translatedText) => normalizedText + "\u0001" + translatedText;

    public void Forget()
    {
        lock (_gate)
        {
            _generation++;
            _seen.Clear();
            _total = 0;
            _entries.Clear();
            _version++;
        }
    }

    public int RecordAll(IEnumerable<TranslationOutcome?> outcomes, long? generation = null)
    {
        var added = 0;
        foreach (var outcome in outcomes)
        {
            if (Record(outcome, generation)) added++;
        }
        return added;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            _version++;
        }
    }

    public int MarkCorrected(string normalizedText, string correctedText)
    {
        var changed = 0;
        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (!string.Equals(_entries[i].NormalizedText, normalizedText, StringComparison.Ordinal)) continue;
                _entries[i] = _entries[i] with { CorrectedText = correctedText };
                changed++;
            }
            if (changed > 0) _version++;
        }
        return changed;
    }
}
