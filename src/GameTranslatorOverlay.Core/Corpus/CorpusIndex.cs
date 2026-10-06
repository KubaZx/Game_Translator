namespace GameTranslatorOverlay.Core.Corpus;

public sealed class CorpusIndex
{
    internal sealed class IndexedText(int id, string key, CorpusEntry representative)
    {
        public int Id { get; } = id;
        public string Key { get; } = key;
        public string Loose { get; } = CorpusText.LooseKey(key);
        public string Digits { get; } = CorpusText.DigitSignature(key);
        public int Words { get; } = CorpusText.WordCount(CorpusText.LooseKey(key));
        public CorpusEntry Representative { get; } = representative;
        public int EntryCount { get; set; } = 1;
        public int TrigramCount { get; set; }
    }

    internal sealed class Scratch
    {
        public int[] Counts = [];
        public readonly List<int> Touched = [];
        public readonly HashSet<long> QueryTrigrams = [];
        public readonly Queue<(CorpusIndex Owner, string Query, CandidateList List)> Recent = new();
    }

    [ThreadStatic] private static Scratch? _scratch;

    private readonly IndexedText[] _texts;
    private readonly Dictionary<string, int> _exact;
    private readonly Dictionary<string, int> _loose;
    private readonly Dictionary<long, int[]> _postings;
    private readonly HashSet<string> _speakers;
    private readonly int _maxPostingLength;

    private CorpusIndex(IReadOnlyList<CorpusEntry> entries, IndexedText[] texts, Dictionary<string, int> exact,
        Dictionary<string, int> loose, Dictionary<long, int[]> postings, HashSet<string> speakers)
    {
        Entries = entries;
        _texts = texts;
        _exact = exact;
        _loose = loose;
        _postings = postings;
        _speakers = speakers;
        _maxPostingLength = Math.Max(1000, texts.Length / 16);
    }

    public static CorpusIndex Empty { get; } = Build([]);

    public IReadOnlyList<CorpusEntry> Entries { get; }

    public int TextCount => _texts.Length;

    public bool IsEmpty => _texts.Length == 0;

    internal IndexedText this[int id] => _texts[id];

    public static CorpusIndex Build(IEnumerable<CorpusEntry> entries)
    {
        var list = entries.ToList();
        var texts = new List<IndexedText>();
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var loose = new Dictionary<string, int>(StringComparer.Ordinal);
        var speakers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in list)
        {
            if (entry.Speaker is { Length: > 0 } speaker)
            {
                var speakerKey = CorpusText.LooseKey(CorpusText.MatchKey(speaker));
                if (speakerKey.Length > 0) speakers.Add(speakerKey);
            }

            var key = CorpusText.MatchKey(entry.En);
            if (CorpusText.LetterOrDigitCount(key) == 0) continue;
            if (exact.TryGetValue(key, out var existing))
            {
                texts[existing].EntryCount++;
                continue;
            }
            var text = new IndexedText(texts.Count, key, entry);
            texts.Add(text);
            exact[key] = text.Id;
            if (loose.TryGetValue(text.Loose, out var other))
            {
                if (other >= 0 && texts[other].Key != key) loose[text.Loose] = -1;
            }
            else
            {
                loose[text.Loose] = text.Id;
            }
        }

        var building = new Dictionary<long, List<int>>();
        var seen = new HashSet<long>();
        foreach (var text in texts)
        {
            seen.Clear();
            AddTrigrams(text.Loose, seen);
            text.TrigramCount = seen.Count;
            foreach (var gram in seen)
            {
                if (!building.TryGetValue(gram, out var posting))
                {
                    posting = [];
                    building[gram] = posting;
                }
                posting.Add(text.Id);
            }
        }

        var postings = new Dictionary<long, int[]>(building.Count);
        foreach (var (gram, posting) in building) postings[gram] = posting.ToArray();

        return new CorpusIndex(list, texts.ToArray(), exact, loose, postings, speakers);
    }

    public bool TryGetExact(string key, out CorpusEntry entry)
    {
        if (_exact.TryGetValue(key, out var id))
        {
            entry = _texts[id].Representative;
            return true;
        }
        entry = null!;
        return false;
    }

    internal int FindExact(string key) => _exact.TryGetValue(key, out var id) ? id : -1;

    internal int FindLoose(string looseKey) => _loose.TryGetValue(looseKey, out var id) ? id : -1;

    public bool IsSpeaker(string looseKey) => _speakers.Contains(looseKey);

    internal static void AddTrigrams(string text, HashSet<long> target)
    {
        for (var i = 0; i + 3 <= text.Length; i++)
        {
            target.Add(Pack(text[i], text[i + 1], text[i + 2]));
        }
        if (text.Length is > 0 and < 3)
        {
            target.Add(Pack(text[0], text.Length > 1 ? text[1] : '\0', '\0'));
        }
    }

    private static long Pack(char a, char b, char c) => ((long)a << 32) | ((long)b << 16) | c;

    internal const int KeepByShared = 256;
    internal const int KeepByCoverage = 128;

    internal sealed class CandidateList(CorpusIndex owner, (int Id, int Shared)[] touched, (int Id, int Shared)[] byShared)
    {
        private (int Id, int Shared)[]? _byCoverage;

        public (int Id, int Shared)[] ByShared { get; } = byShared;

        public (int Id, int Shared)[] ByCoverage => _byCoverage ??= owner.RankByCoverage(touched);
    }

    private (int Id, int Shared)[] RankByCoverage((int Id, int Shared)[] touched)
    {
        var heap = new PriorityQueue<(int Id, int Shared), double>(KeepByCoverage + 1);
        foreach (var candidate in touched)
        {
            var ratio = (double)candidate.Shared / Math.Max(1, _texts[candidate.Id].TrigramCount);
            if (heap.Count < KeepByCoverage)
            {
                heap.Enqueue(candidate, ratio);
            }
            else if (heap.TryPeek(out _, out var lowest) && ratio > lowest)
            {
                heap.EnqueueDequeue(candidate, ratio);
            }
        }
        var ranked = new List<((int Id, int Shared) Item, double Ratio)>(heap.Count);
        while (heap.TryDequeue(out var item, out var ratio)) ranked.Add((item, ratio));
        ranked.Sort(static (x, y) => y.Ratio != x.Ratio ? y.Ratio.CompareTo(x.Ratio) : x.Item.Id.CompareTo(y.Item.Id));
        return ranked.Select(static r => r.Item).ToArray();
    }

    internal List<(int Id, int Shared)> TopCandidates(string query, Func<IndexedText, bool> accept, int limit, bool byCoverage = false)
    {
        var list = Candidates(query);
        var source = byCoverage ? list.ByCoverage : list.ByShared;
        var result = new List<(int Id, int Shared)>(Math.Min(limit, source.Length));
        foreach (var candidate in source)
        {
            if (!accept(_texts[candidate.Id])) continue;
            result.Add(candidate);
            if (result.Count >= limit) break;
        }
        return result;
    }

    internal CandidateList Candidates(string query)
    {
        var scratch = GetScratch();
        foreach (var cached in scratch.Recent)
        {
            if (ReferenceEquals(cached.Owner, this) && cached.Query == query) return cached.List;
        }

        scratch.QueryTrigrams.Clear();
        AddTrigrams(query, scratch.QueryTrigrams);
        var counts = scratch.Counts;
        var touched = scratch.Touched;
        touched.Clear();
        foreach (var gram in scratch.QueryTrigrams)
        {
            if (!_postings.TryGetValue(gram, out var posting) || posting.Length > _maxPostingLength) continue;
            foreach (var id in posting)
            {
                if (counts[id]++ == 0) touched.Add(id);
            }
        }

        var maxShared = scratch.QueryTrigrams.Count;
        var buckets = new int[maxShared + 2];
        foreach (var id in touched) buckets[Math.Min(counts[id], maxShared + 1)]++;
        var threshold = 0;
        var cumulative = 0;
        for (var shared = maxShared + 1; shared >= 1; shared--)
        {
            cumulative += buckets[shared];
            if (cumulative >= KeepByShared)
            {
                threshold = shared;
                break;
            }
        }
        if (threshold == 0) threshold = 1;
        var aboveThreshold = cumulative - buckets[threshold];
        var atThresholdAllowed = KeepByShared - aboveThreshold;

        var byShared = new List<(int Id, int Shared)>(Math.Min(touched.Count, KeepByShared));
        var all = new (int Id, int Shared)[touched.Count];
        for (var i = 0; i < touched.Count; i++)
        {
            var id = touched[i];
            var shared = counts[id];
            all[i] = (id, shared);
            if (shared > threshold || (shared == threshold && atThresholdAllowed-- > 0)) byShared.Add((id, shared));
            counts[id] = 0;
        }

        byShared.Sort(static (x, y) => y.Shared != x.Shared ? y.Shared.CompareTo(x.Shared) : x.Id.CompareTo(y.Id));
        var list = new CandidateList(this, all, byShared.ToArray());
        scratch.Recent.Enqueue((this, query, list));
        if (scratch.Recent.Count > 8) scratch.Recent.Dequeue();
        return list;
    }

    private Scratch GetScratch()
    {
        var scratch = _scratch ??= new Scratch();
        if (scratch.Counts.Length < _texts.Length) scratch.Counts = new int[_texts.Length];
        return scratch;
    }
}
