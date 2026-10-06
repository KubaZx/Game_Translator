using System.Text;
using System.Text.RegularExpressions;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Translation;

public sealed record TranslationOutcomePart(string ScreenText, string? CacheKey, TranslationOrigin? Origin, bool FromCorpus)
{
    public bool IsLiteral => CacheKey is null;
}

internal sealed record TranslationUnit(string Key, string ScreenText, string Separator, bool Literal, bool FromCorpus, bool ExactMatch = false, bool Prefix = false);

internal sealed class UnitPlan(IReadOnlyList<TranslationUnit> units)
{
    public IReadOnlyList<TranslationUnit> Units { get; } = units;

    public string? SingleKey => Units is [{ Literal: false } only] ? only.Key : null;

    public string? CorrectionKey => Units is [{ Literal: false } only] && (!only.FromCorpus || only.ExactMatch) ? only.Key : null;

    public IEnumerable<TranslationUnit> Translatable => Units.Where(static u => !u.Literal);
}

internal static partial class TranslationUnitPlanner
{
    [GeneratedRegex(@"\{[^{}\r\n]{1,40}\}|%(?:\d+\$)?[sdif]", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimePlaceholder();

    [GeneratedRegex(@"\[[A-Za-z0-9]{1,3}\]", RegexOptions.CultureInvariant)]
    private static partial Regex ButtonMarker();

    public static UnitPlan? Plan(string normalized, CorpusSnapper? corpus, bool splitParagraphs)
    {
        if (normalized.Length == 0) return null;
        var lines = normalized.Split('\n');
        var builder = new Builder(lines, splitParagraphs, corpus is { Index.IsEmpty: false } ? corpus : null);
        if (corpus is { Index.IsEmpty: false }) builder.AddCorpus(corpus, corpus.SnapBlock(normalized));
        else builder.AddRun(0, lines.Length);
        var units = builder.Build();
        if (units.Count == 0) return null;
        return units is [{ Literal: false } single] && string.Equals(single.Key, normalized, StringComparison.Ordinal)
            ? null
            : new UnitPlan(units);
    }

    public static string Compose(UnitPlan plan, IReadOnlyList<string?> translations)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < plan.Units.Count; i++)
        {
            var unit = plan.Units[i];
            builder.Append(unit.Separator);
            builder.Append(unit.Literal ? unit.ScreenText : Display(unit, translations[i] ?? string.Empty));
        }
        return builder.ToString();
    }

    internal static string Display(TranslationUnit unit, string translation)
    {
        if (!unit.FromCorpus || string.Equals(unit.Key, unit.ScreenText, StringComparison.Ordinal)) return translation;
        var laidOut = unit.Prefix
            ? ToPrefixLayout(translation, unit.Key, unit.ScreenText)
            : ToScreenLayout(translation, unit.Key, unit.ScreenText);
        return IsUpperCase(unit.ScreenText) && !IsUpperCase(unit.Key) ? laidOut.ToUpperInvariant() : laidOut;
    }

    internal static string ToScreenLayout(string translation, string key, string screenText)
    {
        if (string.IsNullOrWhiteSpace(translation)) return translation;
        var (_, keyPlan) = TextReflow.Unwrap(key);
        var (_, screenPlan) = TextReflow.Unwrap(screenText);
        if (screenPlan.ParagraphLineCounts.Count == 0) return translation;
        var paragraphs = TextReflow.ToParagraphs(translation, keyPlan);
        var parts = paragraphs.Split('\n');
        if (parts.Length == screenPlan.ParagraphLineCounts.Count) return TextReflow.Rewrap(paragraphs, screenPlan);
        var joined = string.Join(' ', parts.Select(static p => p.Trim()).Where(static p => p.Length > 0));
        return TextReflow.WrapBalanced(joined, Math.Max(1, screenPlan.ParagraphLineCounts.Sum()));
    }

    internal static string ToPrefixLayout(string translation, string key, string screenText)
    {
        if (string.IsNullOrWhiteSpace(translation)) return translation;
        var screenLines = screenText.Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0).ToArray();
        if (screenLines.Length < 2 || key.Contains('\n')) return ToScreenLayout(translation, key, screenText);
        var width = screenLines[..^1].Max(static l => l.Length);
        var lineCount = Math.Max(screenLines.Length, (int)Math.Ceiling((double)key.Length / Math.Max(1, width)));
        var joined = string.Join(' ', translation.Split('\n').Select(static p => p.Trim()).Where(static p => p.Length > 0));
        return TextReflow.WrapBalanced(joined, lineCount);
    }

    internal static bool IsUpperCase(string text)
    {
        var letters = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            if (char.IsLower(ch)) return false;
            if (char.IsUpper(ch)) letters++;
        }
        return letters >= 2;
    }

    internal static bool IsUsable(CorpusEntry entry, string screenText)
    {
        var canonical = CorpusTranslationKey.DisplayText(entry.En);
        if (RuntimePlaceholder().IsMatch(canonical)) return false;
        foreach (Match marker in ButtonMarker().Matches(canonical))
        {
            if (screenText.IndexOf(marker.Value, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    private sealed class Builder(string[] lines, bool splitParagraphs, CorpusSnapper? corpus)
    {
        private List<TranslationUnit> Units { get; } = [];
        private readonly List<int> _junk = [];
        private int _lastLine = -1;

        public List<TranslationUnit> Build()
        {
            if (_junk.Count == 0 || Units.Count(static u => !u.Literal) <= _junk.Count) return Units;
            foreach (var index in _junk) Units[index] = Units[index] with { Literal = true };
            return Units;
        }

        private string SeparatorFor(int line)
        {
            if (Units.Count == 0)
            {
                _lastLine = line;
                return string.Empty;
            }
            if (line == _lastLine) return " ";
            _lastLine = line;
            return "\n";
        }

        private void Add(int line, string key, string screenText, bool literal, bool fromCorpus) =>
            Units.Add(new TranslationUnit(key, screenText, SeparatorFor(line), literal, fromCorpus));

        private void Add(int line, TranslationUnit unit) => Units.Add(unit with { Separator = SeparatorFor(line) });

        public void AddRun(int start, int end)
        {
            if (end <= start) return;
            if (!splitParagraphs)
            {
                var text = string.Join('\n', lines[start..end]);
                Add(start, text, text, literal: false, fromCorpus: false);
                _lastLine = end - 1;
                return;
            }
            var (_, plan) = TextReflow.Unwrap(string.Join('\n', lines[start..end]));
            if (plan.ParagraphLineCounts.Sum() != end - start)
            {
                var text = string.Join('\n', lines[start..end]);
                Add(start, text, text, literal: false, fromCorpus: false);
                _lastLine = end - 1;
                return;
            }
            var first = start;
            foreach (var count in plan.ParagraphLineCounts)
            {
                var text = string.Join('\n', lines[first..(first + count)]);
                if (!JunkFilter.IsMeaningful(text) || corpus?.LooksLikeNoise(text) == true) _junk.Add(Units.Count);
                Add(first, text, text, literal: false, fromCorpus: false);
                _lastLine = first + count - 1;
                first += count;
            }
        }

        public void AddCorpus(CorpusSnapper corpus, CorpusBlockSnap snap)
        {
            if (snap.LineKeys.Count != lines.Length)
            {
                AddRun(0, lines.Length);
                return;
            }

            var starts = new Dictionary<int, CorpusSegment>();
            var partials = new Dictionary<int, List<CorpusSegment>>();
            foreach (var segment in snap.Segments)
            {
                if (segment.Match.Kind == CorpusMatchKind.Fragment) continue;
                if (segment.Partial)
                {
                    if (segment.LineCount != 1 || segment.FirstToken < 0 || segment.TokenCount <= 0) continue;
                    if (!partials.TryGetValue(segment.FirstLine, out var list)) partials[segment.FirstLine] = list = [];
                    list.Add(segment);
                }
                else if (segment.LineCount >= 1 && segment.FirstLine + segment.LineCount <= lines.Length)
                {
                    starts[segment.FirstLine] = segment;
                }
            }

            var runStart = 0;
            var line = 0;
            while (line < lines.Length)
            {
                if (starts.TryGetValue(line, out var segment) && TryWhole(corpus, segment) is { } whole)
                {
                    AddRun(runStart, line);
                    foreach (var (offset, unit) in whole) Add(line + offset, unit);
                    _lastLine = line + segment.LineCount - 1;
                    line += segment.LineCount;
                    runStart = line;
                    continue;
                }
                if (partials.TryGetValue(line, out var list) && TryPartial(corpus, line, snap.LineKeys[line], list) is { } parts)
                {
                    AddRun(runStart, line);
                    foreach (var unit in parts) Add(line, unit);
                    line++;
                    runStart = line;
                    continue;
                }
                line++;
            }
            AddRun(runStart, lines.Length);
        }

        private List<(int Offset, TranslationUnit Unit)>? TryWhole(CorpusSnapper corpus, CorpusSegment segment)
        {
            var screenLines = lines[segment.FirstLine..(segment.FirstLine + segment.LineCount)];
            var entry = segment.Match.Entry;
            var key = CorpusTranslationKey.For(entry);
            if (key.Length == 0) return null;

            string? literal = null;
            var offset = 0;
            var prefix = corpus.SpeakerPrefixLength(segment.Text);
            if (prefix > 0 && !CorpusText.MatchKey(entry.En).StartsWith(segment.Text[..prefix], StringComparison.Ordinal))
            {
                var delimiter = segment.Text[prefix - 1];
                var first = screenLines[0];
                var cut = first.IndexOf(delimiter);
                if (cut < 0) return null;
                literal = first[..(cut + 1)].TrimEnd();
                var rest = first[(cut + 1)..].TrimStart();
                if (rest.Length == 0)
                {
                    if (screenLines.Length == 1) return null;
                    screenLines = screenLines[1..];
                    offset = 1;
                }
                else
                {
                    screenLines = [rest, .. screenLines[1..]];
                }
                if (literal.Length == 0) literal = null;
            }

            var screenText = string.Join('\n', screenLines);
            if (!IsUsable(entry, screenText)) return null;
            var units = new List<(int, TranslationUnit)>(2);
            if (literal is not null) units.Add((0, new TranslationUnit(literal, literal, string.Empty, Literal: true, FromCorpus: false)));
            units.Add((offset, new TranslationUnit(key, screenText, string.Empty, Literal: false, FromCorpus: true,
                ExactMatch: segment.Match.Kind == CorpusMatchKind.Exact, Prefix: segment.Match.IsPrefix)));
            return units;
        }

        private List<TranslationUnit>? TryPartial(CorpusSnapper corpus, int line, string lineKey, List<CorpusSegment> segments)
        {
            var screenTokens = lines[line].Split(' ');
            var keyTokens = lineKey.Split(' ');
            if (screenTokens.Length != keyTokens.Length) return null;

            segments.Sort(static (a, b) => a.FirstToken.CompareTo(b.FirstToken));
            var units = new List<TranslationUnit>();
            var position = 0;
            foreach (var segment in segments)
            {
                var end = segment.FirstToken + segment.TokenCount;
                if (segment.FirstToken < position || end > screenTokens.Length) return null;
                if (segment.FirstToken > position)
                {
                    var crumb = string.Join(' ', screenTokens[position..segment.FirstToken]);
                    units.Add(new TranslationUnit(crumb, crumb, string.Empty, Literal: true, FromCorpus: false));
                }
                var key = CorpusTranslationKey.For(segment.Match.Entry);
                var screenText = string.Join(' ', screenTokens[segment.FirstToken..end]);
                if (key.Length == 0 || !IsUsable(segment.Match.Entry, screenText)) return null;
                var speakerTag = corpus.SpeakerPrefixLength(segment.Text) >= segment.Text.Length;
                units.Add(speakerTag
                    ? new TranslationUnit(screenText, screenText, string.Empty, Literal: true, FromCorpus: false)
                    : new TranslationUnit(key, screenText, string.Empty, Literal: false, FromCorpus: true,
                        ExactMatch: segment.Match.Kind == CorpusMatchKind.Exact));
                position = end;
            }
            if (position < screenTokens.Length)
            {
                var crumb = string.Join(' ', screenTokens[position..]);
                units.Add(new TranslationUnit(crumb, crumb, string.Empty, Literal: true, FromCorpus: false));
            }
            return units.Any(static u => !u.Literal) ? units : null;
        }
    }
}
