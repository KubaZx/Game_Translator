using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Corpus;

public enum CorpusMatchKind
{
    Exact,
    Fuzzy,
    Fragment,
}

public sealed record CorpusSnapOptions
{
    public double FuzzyThreshold { get; init; } = 0.80;
    public double FragmentThreshold { get; init; } = 0.80;
    public double Margin { get; init; } = 0.08;
    public int MinFuzzyLength { get; init; } = 16;
    public int MinFuzzyWords { get; init; } = 2;
    public int MinFragmentLength { get; init; } = 16;
    public int MinFragmentWords { get; init; } = 3;
    public int MinExactLetters { get; init; } = 2;
    public int MinExactLength { get; init; }
    public int MinSpanLetters { get; init; } = 3;
    public double MinFragmentStretch { get; init; } = 1.3;
    public IReadOnlySet<string> GlyphTokens { get; init; } = DefaultGlyphTokens;
    public int MaxSpanWords { get; init; } = 12;
    public int MaxCandidates { get; init; } = 32;
    public bool AllowFragments { get; init; } = true;
    public bool AllowPartialLines { get; init; } = true;
    public bool AllowAssembly { get; init; } = true;

    public static IReadOnlySet<string> DefaultGlyphTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "tab", "esc", "enter", "space", "shift", "ctrl", "alt", "lmb", "rmb", "mmb", "lb", "rb", "lt", "rt",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
    };

    public static CorpusSnapOptions Default { get; } = new();
}

public sealed record CorpusMatch(CorpusEntry Entry, double Score, CorpusMatchKind Kind)
{
    public int SameTextEntries { get; init; } = 1;
    public string CorpusKey { get; init; } = string.Empty;
    internal int TextId { get; init; } = -1;
}

public sealed record CorpusSegment(int FirstLine, int LineCount, string Text, CorpusMatch Match)
{
    public bool Partial { get; init; }
    public bool Assembled { get; init; }
    public int FirstToken { get; init; } = -1;
    public int TokenCount { get; init; }
    public int Length => CorpusText.LetterOrDigitCount(Text);
}

public sealed class CorpusBlockSnap
{
    internal CorpusBlockSnap(IReadOnlyList<string> lineKeys, IReadOnlyList<CorpusSegment> segments, int ignorableLength)
    {
        LineKeys = lineKeys;
        Segments = segments;
        TotalLength = lineKeys.Sum(static line => CorpusText.LetterOrDigitCount(line));
        IgnorableLength = ignorableLength;
    }

    public static CorpusBlockSnap Empty { get; } = new([], [], 0);

    public IReadOnlyList<string> LineKeys { get; }
    public IReadOnlyList<CorpusSegment> Segments { get; }
    public int TotalLength { get; }
    public int IgnorableLength { get; }

    public CorpusMatch? Whole =>
        Segments is [{ FirstLine: 0, Partial: false, Match.Kind: not CorpusMatchKind.Fragment } single]
        && single.LineCount == LineKeys.Count
            ? single.Match
            : null;

    public int CoveredLength(CorpusMatchKind kind) =>
        Segments.Where(segment => segment.Match.Kind == kind).Sum(static segment => segment.Length);

    public int LocallyServedLength =>
        Segments.Where(static s => s.Match.Kind != CorpusMatchKind.Fragment).Sum(static s => s.Length);

    public bool IsFullyServed =>
        LineKeys.Count > 0 && LocallyServedLength + IgnorableLength >= TotalLength;

    public bool HasAnyMatch => Segments.Count > 0;
}

public sealed class CorpusSnapper(CorpusIndex index, CorpusSnapOptions? options = null)
{
    private readonly record struct Token(int Start, int End);

    private readonly record struct Span(int FirstToken, int EndToken, CorpusMatch Match, int Length);

    private sealed class LineState(string key)
    {
        public string Key { get; } = key;
        public bool Covered { get; set; }
        public bool InProseParagraph { get; set; }
        public CorpusMatch? Fragment { get; set; }
    }

    public CorpusIndex Index { get; } = index;
    public CorpusSnapOptions Options { get; } = options ?? CorpusSnapOptions.Default;

    public CorpusMatch? Snap(string text)
    {
        if (Index.IsEmpty) return null;
        var key = CorpusText.MatchKey(text);
        return SnapLine(key, Options.AllowFragments);
    }

    public CorpusBlockSnap SnapBlock(string text)
    {
        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0 || Index.IsEmpty)
        {
            var keys = normalized.Length == 0
                ? []
                : normalized.Split('\n').Select(CorpusText.MatchKey).Where(static k => k.Length > 0).ToList();
            return keys.Count == 0 ? CorpusBlockSnap.Empty : new CorpusBlockSnap(keys, [], 0);
        }

        var rawLines = normalized.Split('\n')
            .Select(static line => OcrTextRepair.Repair(line.Trim()))
            .Where(static line => CorpusText.MatchKey(line).Length > 0)
            .ToList();
        var lines = rawLines.Select(static line => new LineState(CorpusText.MatchKey(line))).ToList();
        if (lines.Count == 0) return CorpusBlockSnap.Empty;

        var segments = new List<CorpusSegment>();
        var ignorable = 0;

        if (lines.Count > 1)
        {
            var whole = SnapFull(string.Join(' ', lines.Select(static l => l.Key)));
            if (whole is not null)
            {
                segments.Add(new CorpusSegment(0, lines.Count, JoinKeys(lines, 0, lines.Count), whole));
                return Finish(lines, segments, ignorable);
            }

            var (_, plan) = TextReflow.Unwrap(string.Join('\n', rawLines));
            if (plan.ParagraphLineCounts.Sum() == lines.Count)
            {
                var first = 0;
                foreach (var count in plan.ParagraphLineCounts)
                {
                    if (count > 1)
                    {
                        for (var i = first; i < first + count; i++)
                        {
                            var self = i;
                            lines[i].InProseParagraph = Enumerable.Range(first, count)
                                .Any(k => k != self && CorpusText.WordCount(lines[k].Key) >= 3);
                        }
                        var paragraph = SnapFull(JoinKeys(lines, first, count));
                        if (paragraph is not null)
                        {
                            segments.Add(new CorpusSegment(first, count, JoinKeys(lines, first, count), paragraph));
                            for (var i = first; i < first + count; i++) lines[i].Covered = true;
                        }
                    }
                    first += count;
                }
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Covered) continue;
            var match = SnapLine(line.Key, allowFragment: false);
            if (match is not null && line.InProseParagraph && IsShort(match.CorpusKey)) match = null;
            var partial = match is null or { Kind: CorpusMatchKind.Fuzzy } && Options.AllowPartialLines
                ? SegmentLine(i, line.Key)
                : null;
            if (partial is not null && line.InProseParagraph && partial.Value.Segments.All(segment => IsShort(segment.Match.CorpusKey)))
            {
                partial = null;
            }
            var partialIsBetter = partial is { } candidate && candidate.Segments.Count >= 2
                && candidate.Segments.Any(segment => !IsShort(segment.Match.CorpusKey) && segment.Match.Score > (match?.Score ?? 0));
            if (match is not null && !partialIsBetter)
            {
                segments.Add(new CorpusSegment(i, 1, line.Key, match));
                line.Covered = true;
                continue;
            }
            if (partial is { } parts)
            {
                segments.AddRange(parts.Segments);
                ignorable += parts.Ignorable;
                line.Covered = true;
                continue;
            }
            if (Options.AllowFragments)
            {
                line.Fragment = FindFragment(CorpusText.LooseKey(line.Key));
            }
        }

        if (Options.AllowAssembly) Assemble(lines, segments);

        if (Options.AllowFragments) AddFragments(lines, segments);

        return Finish(lines, segments, ignorable);
    }

    private static CorpusBlockSnap Finish(List<LineState> lines, List<CorpusSegment> segments, int ignorable)
    {
        segments.Sort(static (a, b) => a.FirstLine != b.FirstLine ? a.FirstLine.CompareTo(b.FirstLine) : 0);
        return new CorpusBlockSnap(lines.Select(static l => l.Key).ToList(), segments, ignorable);
    }

    private bool IsShort(string corpusKey) => CorpusText.LooseKey(corpusKey).Length < Options.MinFuzzyLength;

    private CorpusMatch? SnapFull(string key)
    {
        var match = SnapLine(key, allowFragment: false);
        return match is { Kind: not CorpusMatchKind.Fragment } ? match : null;
    }

    private CorpusMatch? SnapKey(string key, bool allowFragment)
    {
        if (key.Length == 0) return null;
        var loose = CorpusText.LooseKey(key);
        if (loose.Length == 0) return null;
        var id = Index.FindExact(key);
        if (id < 0) id = Index.FindLoose(loose);
        if (id >= 0)
        {
            return CorpusText.LetterOrDigitCount(Index[id].Loose) >= Options.MinExactLetters
                   && Index[id].Loose.Length >= Options.MinExactLength
                ? Make(id, 1.0, CorpusMatchKind.Exact)
                : null;
        }
        return FindFuzzy(loose) ?? (allowFragment ? FindFragment(loose) : null);
    }

    private CorpusMatch? SnapLine(string key, bool allowFragment)
    {
        var prefix = SpeakerPrefixLength(key);
        if (prefix > 0 && SnapKey(key[prefix..].TrimStart(), allowFragment) is { } withoutSpeaker) return withoutSpeaker;
        return SnapKey(key, allowFragment);
    }

    public int SpeakerPrefixLength(string key)
    {
        var colon = key.IndexOf(':');
        if (colon > 0 && colon <= 40)
        {
            var name = CorpusText.LooseKey(key[..colon]);
            if (name.Length > 0 && Index.IsSpeaker(name)) return colon + 1;
        }
        if (key.Length > 2 && key[0] == '[')
        {
            var close = key.IndexOf(']');
            if (close > 1 && Index.IsSpeaker(CorpusText.LooseKey(key[1..close]))) return close + 1;
        }
        return 0;
    }

    private CorpusMatch? FindFuzzy(string loose)
    {
        if (loose.Length < Options.MinFuzzyLength || CorpusText.WordCount(loose) < Options.MinFuzzyWords) return null;

        var threshold = Options.FuzzyThreshold;
        var floor = Math.Max(0.0, threshold - Options.Margin);
        var minLength = (int)Math.Ceiling(loose.Length * floor - 1e-9);
        var maxLength = (int)Math.Floor(loose.Length / Math.Max(floor, 0.01) + 1e-9);
        var digits = CorpusText.DigitSignature(loose);

        var candidates = Index.TopCandidates(loose, text =>
            text.Loose.Length >= minLength && text.Loose.Length <= maxLength
            && text.Loose.Length >= Options.MinFuzzyLength && text.Words >= Options.MinFuzzyWords
            && text.Digits == digits, Options.MaxCandidates);

        var bestId = -1;
        var best = 0.0;
        var second = 0.0;
        foreach (var (id, _) in candidates)
        {
            var ratio = EditDistance.GuardedRatio(loose, Index[id].Loose, floor);
            if (ratio > best)
            {
                second = best;
                best = ratio;
                bestId = id;
            }
            else if (ratio > second)
            {
                second = ratio;
            }
        }

        if (bestId < 0 || best < threshold || best - second < Options.Margin) return null;
        return Make(bestId, best, best >= 1.0 ? CorpusMatchKind.Exact : CorpusMatchKind.Fuzzy);
    }

    private CorpusMatch? FindFragment(string loose)
    {
        if (!Options.AllowFragments) return null;
        if (loose.Length < Options.MinFragmentLength || CorpusText.WordCount(loose) < Options.MinFragmentWords) return null;

        var threshold = Options.FragmentThreshold;
        var floor = Math.Max(0.0, threshold - Options.Margin);
        var maxDistance = (int)Math.Floor((1.0 - floor) * loose.Length + 1e-9);
        var digits = CorpusText.DigitSignature(loose);

        var minimumTarget = (int)Math.Ceiling(loose.Length * Options.MinFragmentStretch) + 3;
        var candidates = Index.TopCandidates(loose,
            text => text.Loose.Length >= minimumTarget, Options.MaxCandidates);

        var bestId = -1;
        var best = 0.0;
        var second = 0.0;
        foreach (var (id, _) in candidates)
        {
            var target = Index[id].Loose;
            var score = FragmentScore(loose, target, maxDistance, digits);
            if (score > best)
            {
                second = best;
                best = score;
                bestId = id;
            }
            else if (score > second)
            {
                second = score;
            }
        }

        if (bestId < 0 || best < threshold || best - second < Options.Margin) return null;
        return Make(bestId, best, CorpusMatchKind.Fragment);
    }

    private static double FragmentScore(string query, string target, int maxDistance, string digits)
    {
        var position = target.IndexOf(query, StringComparison.Ordinal);
        while (position >= 0)
        {
            if (CorpusText.IsBoundary(target, position) && CorpusText.IsBoundary(target, position + query.Length)
                && CorpusText.DigitSignature(target.AsSpan(position, query.Length).ToString()) == digits
                && !CutsNumber(target, position, position + query.Length))
            {
                return 1.0;
            }
            position = target.IndexOf(query, position + 1, StringComparison.Ordinal);
        }

        if (EditDistance.FindWithin(query, target, maxDistance) is not { } alignment) return 0.0;
        if (!NearBoundary(target, alignment.Start) || !NearBoundary(target, alignment.End)) return 0.0;
        if (CorpusText.DigitSignature(target[alignment.Start..alignment.End]) != digits) return 0.0;
        if (CutsNumber(target, alignment.Start, alignment.End)) return 0.0;
        if ((CorpusText.HasDigit(query) || CorpusText.HasDigit(target.AsSpan(alignment.Start, alignment.End - alignment.Start)))
            && EditDistance.BoundedGuarded(query, target.AsSpan(alignment.Start, alignment.End - alignment.Start), maxDistance) > maxDistance)
        {
            return 0.0;
        }
        return 1.0 - (double)alignment.Distance / query.Length;
    }

    private static bool CutsNumber(string text, int start, int end) =>
        (start > 0 && CorpusText.IsNumberCharacter(text, start - 1) && start < text.Length && CorpusText.IsNumberCharacter(text, start))
        || (end > 0 && end < text.Length && CorpusText.IsNumberCharacter(text, end - 1) && CorpusText.IsNumberCharacter(text, end));

    private static bool NearBoundary(string text, int index) =>
        CorpusText.IsBoundary(text, index) || CorpusText.IsBoundary(text, index - 1) || CorpusText.IsBoundary(text, index + 1);

    private (List<CorpusSegment> Segments, int Ignorable)? SegmentLine(int lineIndex, string key)
    {
        var tokens = Tokenize(key);
        if (tokens.Count < 2) return null;

        var allowed = new bool[tokens.Count];
        var speakerEnd = SpeakerPrefixLength(key);
        if (speakerEnd > 0)
        {
            for (var t = 0; t < tokens.Count && tokens[t].End <= speakerEnd; t++) allowed[t] = true;
        }

        var spans = new List<Span>();
        for (var a = 0; a < tokens.Count; a++)
        {
            for (var b = a + 1; b <= tokens.Count && b - a <= Options.MaxSpanWords; b++)
            {
                if (a == 0 && b == tokens.Count) continue;
                var text = key[tokens[a].Start..tokens[b - 1].End];
                var loose = CorpusText.LooseKey(text);
                if (CorpusText.LetterOrDigitCount(loose) < Options.MinSpanLetters) continue;
                var id = Index.FindExact(text);
                if (id < 0) id = Index.FindLoose(loose);
                if (id >= 0 && Index[id].Loose.Length >= Options.MinExactLength) spans.Add(new Span(a, b, Make(id, 1.0, CorpusMatchKind.Exact), text.Length));
            }
        }

        AddContainedSpans(key, tokens, spans);
        if (spans.Count == 0) return null;

        var count = tokens.Count;
        var best = new int[count + 1];
        var choice = new int[count + 1];
        Array.Fill(choice, -1);
        for (var i = 1; i <= count; i++)
        {
            best[i] = best[i - 1];
            choice[i] = -1;
            for (var s = 0; s < spans.Count; s++)
            {
                var span = spans[s];
                if (span.EndToken != i) continue;
                var value = best[span.FirstToken] + span.Length;
                if (value > best[i])
                {
                    best[i] = value;
                    choice[i] = s;
                }
            }
        }

        var chosen = new List<Span>();
        var covered = new bool[count];
        for (var i = count; i > 0;)
        {
            if (choice[i] < 0)
            {
                i--;
                continue;
            }
            var span = spans[choice[i]];
            chosen.Add(span);
            for (var t = span.FirstToken; t < span.EndToken; t++) covered[t] = true;
            i = span.FirstToken;
        }
        if (chosen.Count == 0) return null;
        chosen.Reverse();

        if (chosen.Count > 1 && chosen.Count(span => span.Length < Options.MinFuzzyLength) > 1) return null;

        var firstCovered = Array.IndexOf(covered, true);
        var lastCovered = Array.LastIndexOf(covered, true);
        for (var t = 0; t < count; t++)
        {
            if (covered[t] || allowed[t]) continue;
            var token = key[tokens[t].Start..tokens[t].End];
            if (t > firstCovered && t < lastCovered) return null;
            var letters = CorpusText.LetterOrDigitCount(token);
            var glyph = Options.GlyphTokens.Contains(CorpusText.LooseKey(token));
            var crumb = t < firstCovered ? letters <= 1 || glyph : letters == 0 || glyph;
            if (!crumb) return null;
        }

        var segments = chosen
            .Select(span => new CorpusSegment(lineIndex, 1, key[tokens[span.FirstToken].Start..tokens[span.EndToken - 1].End], span.Match)
            {
                Partial = true,
                FirstToken = span.FirstToken,
                TokenCount = span.EndToken - span.FirstToken,
            })
            .ToList();
        var ignorable = Math.Max(0, CorpusText.LetterOrDigitCount(key) - segments.Sum(static s => s.Length));
        return (segments, ignorable);
    }

    private void AddContainedSpans(string key, List<Token> tokens, List<Span> spans)
    {
        var loose = CorpusText.LooseKey(key);
        if (loose.Length < Options.MinFuzzyLength) return;

        var threshold = Options.FuzzyThreshold;
        var candidates = Index.TopCandidates(loose, text =>
            text.Loose.Length >= Options.MinFuzzyLength && text.Words >= Options.MinFuzzyWords
            && text.Loose.Length < loose.Length, Options.MaxCandidates, byCoverage: true);

        foreach (var (id, shared) in candidates)
        {
            var target = Index[id];
            if ((double)shared / Math.Max(1, target.TrigramCount) < threshold - 0.35) break;
            var maxDistance = (int)Math.Floor((1.0 - threshold) * target.Loose.Length + 1e-9);
            if (EditDistance.FindWithin(target.Loose, key, maxDistance) is not { } alignment) continue;
            var first = tokens.FindIndex(t => t.End > alignment.Start);
            var last = tokens.FindLastIndex(t => t.Start < alignment.End);
            if (first < 0 || last < first) continue;
            var text = key[tokens[first].Start..tokens[last].End];
            if (CorpusText.DigitSignature(text) != target.Digits) continue;
            var ratio = EditDistance.GuardedRatio(CorpusText.LooseKey(text), target.Loose, threshold);
            if (ratio < threshold) continue;
            if (first == 0 && last == tokens.Count - 1) continue;
            if (!IsUniqueContainment(text, id, ratio)) continue;
            spans.Add(new Span(first, last + 1,
                Make(id, ratio, ratio >= 1.0 ? CorpusMatchKind.Exact : CorpusMatchKind.Fuzzy), text.Length));
        }
    }

    private bool IsUniqueContainment(string text, int id, double ratio)
    {
        var loose = CorpusText.LooseKey(text);
        var floor = Math.Max(0.0, Options.FuzzyThreshold - Options.Margin);
        var others = Index.TopCandidates(loose, t => t.Id != id
            && t.Loose.Length >= loose.Length * floor && t.Loose.Length <= loose.Length / Math.Max(floor, 0.01), Options.MaxCandidates);
        foreach (var (other, _) in others)
        {
            if (ratio - EditDistance.GuardedRatio(loose, Index[other].Loose, floor) < Options.Margin) return false;
        }
        return true;
    }

    private void Assemble(List<LineState> lines, List<CorpusSegment> segments)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Covered || lines[i].Fragment is not { } fragment) continue;

            var runStart = i;
            while (runStart > 0 && !lines[runStart - 1].Covered) runStart--;
            var runEnd = i + 1;
            while (runEnd < lines.Count && !lines[runEnd].Covered) runEnd++;

            CorpusMatch? bestMatch = null;
            var bestFirst = 0;
            var bestCount = 0;
            for (var first = runStart; first <= i; first++)
            {
                for (var end = i + 1; end <= runEnd; end++)
                {
                    if (end - first < 2) continue;
                    var joined = JoinKeys(lines, first, end - first);
                    var match = SnapFull(joined);
                    if (match is null || match.TextId != fragment.TextId) continue;
                    if (bestMatch is null || end - first > bestCount || match.Score > bestMatch.Score)
                    {
                        bestMatch = match;
                        bestFirst = first;
                        bestCount = end - first;
                    }
                }
            }

            if (bestMatch is null) continue;
            segments.Add(new CorpusSegment(bestFirst, bestCount, JoinKeys(lines, bestFirst, bestCount), bestMatch) { Assembled = true });
            for (var k = bestFirst; k < bestFirst + bestCount; k++)
            {
                lines[k].Covered = true;
                lines[k].Fragment = null;
            }
            i = bestFirst + bestCount - 1;
        }
    }

    private static void AddFragments(List<LineState> lines, List<CorpusSegment> segments)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Covered || lines[i].Fragment is not { } fragment) continue;
            var end = i + 1;
            while (end < lines.Count && !lines[end].Covered && lines[end].Fragment?.TextId == fragment.TextId) end++;
            var best = fragment;
            for (var k = i; k < end; k++)
            {
                if (lines[k].Fragment!.Score < best.Score) best = lines[k].Fragment!;
                lines[k].Covered = true;
            }
            segments.Add(new CorpusSegment(i, end - i, JoinKeys(lines, i, end - i), best));
            i = end - 1;
        }
    }

    private static string JoinKeys(List<LineState> lines, int first, int count) =>
        string.Join(' ', lines.Skip(first).Take(count).Select(static l => l.Key));

    private static List<Token> Tokenize(string key)
    {
        var tokens = new List<Token>();
        var start = -1;
        for (var i = 0; i <= key.Length; i++)
        {
            var space = i == key.Length || key[i] == ' ';
            if (space)
            {
                if (start >= 0) tokens.Add(new Token(start, i));
                start = -1;
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        return tokens;
    }

    private CorpusMatch Make(int id, double score, CorpusMatchKind kind)
    {
        var text = Index[id];
        return new CorpusMatch(text.Representative, score, kind)
        {
            SameTextEntries = text.EntryCount,
            CorpusKey = text.Key,
            TextId = id,
        };
    }
}
