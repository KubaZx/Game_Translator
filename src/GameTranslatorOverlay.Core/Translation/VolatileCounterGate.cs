using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.Core.Translation;

public enum CounterAction
{
    Send,
    Substitute,
    Skip,
}

public sealed record CounterDecision(CounterAction Action, string? Translation = null)
{
    public static CounterDecision Send { get; } = new(CounterAction.Send);
    public static CounterDecision Skip { get; } = new(CounterAction.Skip);
}

public sealed partial class VolatileCounterGate(TimeProvider? timeProvider = null)
{
    public const char DigitsMarker = '\u0001';
    public const int DistinctTextsToMute = 3;
    public const int DistinctWordedTextsToMute = 8;
    public const int MaxMasks = 256;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Mute = TimeSpan.FromMinutes(10);

    private sealed class MaskState
    {
        public List<(string Text, long Batch, DateTimeOffset At)> Recent { get; } = [];
        public DateTimeOffset MutedUntil { get; set; } = DateTimeOffset.MinValue;
        public string? LastSource { get; set; }
        public string? LastTranslation { get; set; }
    }

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MaskState> _masks = new(StringComparer.Ordinal);

    [GeneratedRegex(@"(?<![0-9])(?:[0-9]{1,2}(?::[0-9]{2}){1,2}|[0-9]+/[0-9]+|[0-9]+(?:[.,][0-9]+)?%)(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ClockFractionPercent();

    public static bool HasLetter(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) return true;
        }
        return false;
    }

    public static string Mask(string text)
    {
        StringBuilder? builder = null;
        var inDigits = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsDigit(ch))
            {
                if (builder is null)
                {
                    builder = new StringBuilder(text.Length);
                    builder.Append(text, 0, i);
                }
                if (!inDigits) builder.Append(DigitsMarker);
                inDigits = true;
                continue;
            }
            inDigits = false;
            builder?.Append(ch);
        }
        return builder?.ToString() ?? text;
    }

    public static IReadOnlyList<string>? ClockFractionPercentNumbers(string text)
    {
        var matches = ClockFractionPercent().Matches(text);
        if (matches.Count == 0) return null;
        if (ClockFractionPercent().Replace(text, string.Empty).Any(char.IsDigit)) return null;
        return matches.Select(static m => m.Value).ToList();
    }

    public static string? Substitute(string newText, string lastSource, string lastTranslation)
    {
        if (ClockFractionPercentNumbers(newText) is not { } newNumbers) return null;
        if (ClockFractionPercentNumbers(lastSource) is not { } sourceNumbers) return null;
        if (ClockFractionPercentNumbers(lastTranslation) is not { } translatedNumbers) return null;
        if (!sourceNumbers.SequenceEqual(translatedNumbers, StringComparer.Ordinal)) return null;
        if (newNumbers.Count != sourceNumbers.Count) return null;
        var index = 0;
        return ClockFractionPercent().Replace(lastTranslation, _ => newNumbers[index++]);
    }

    public static IReadOnlyList<string> DigitRuns(string text)
    {
        var runs = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var digit = i < text.Length && char.IsDigit(text[i]);
            if (digit && start < 0) start = i;
            if (!digit && start >= 0)
            {
                runs.Add(text[start..i]);
                start = -1;
            }
        }
        return runs;
    }

    public static string? SubstituteDigitRuns(string newText, string lastSource, string lastTranslation)
    {
        var newRuns = DigitRuns(newText);
        var sourceRuns = DigitRuns(lastSource);
        if (newRuns.Count == 0 || newRuns.Count != sourceRuns.Count) return null;
        if (!sourceRuns.SequenceEqual(DigitRuns(lastTranslation), StringComparer.Ordinal)) return null;

        var builder = new StringBuilder(lastTranslation.Length + 8);
        var index = 0;
        var inDigits = false;
        foreach (var ch in lastTranslation)
        {
            if (char.IsDigit(ch))
            {
                if (!inDigits) builder.Append(newRuns[index++]);
                inDigits = true;
                continue;
            }
            inDigits = false;
            builder.Append(ch);
        }
        return builder.ToString();
    }

    public CounterDecision Evaluate(string normalizedText, long batch, bool commit = true)
    {
        var mask = Mask(normalizedText);
        if (ReferenceEquals(mask, normalizedText)) return CounterDecision.Send;

        var worded = ClockFractionPercentNumbers(normalizedText) is null;
        var threshold = worded ? DistinctWordedTextsToMute : DistinctTextsToMute;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_masks.TryGetValue(mask, out var state))
            {
                if (!commit) return CounterDecision.Send;
                if (_masks.Count >= MaxMasks) MakeRoom(now);
                state = _masks[mask] = new MaskState();
            }

            if (state.MutedUntil > now)
            {
                if (commit && !worded) state.MutedUntil = now + Mute;
                return Muted(state, normalizedText);
            }

            state.Recent.RemoveAll(entry => now - entry.At >= Window);
            if (state.Recent.Any(entry => entry.Text == normalizedText)) return CounterDecision.Send;
            if (state.Recent.Any(entry => entry.Batch == batch))
            {
                if (commit) state.Recent.Add((normalizedText, batch, now));
                return CounterDecision.Send;
            }
            if (state.Recent.Select(static entry => entry.Batch).Distinct().Count() + 1 >= threshold)
            {
                if (!commit) return CounterDecision.Send;
                state.MutedUntil = now + Mute;
                return Muted(state, normalizedText);
            }
            if (commit) state.Recent.Add((normalizedText, batch, now));
            return CounterDecision.Send;
        }
    }

    public void RecordTranslated(string normalizedSource, string translation)
    {
        var mask = Mask(normalizedSource);
        if (ReferenceEquals(mask, normalizedSource)) return;
        lock (_gate)
        {
            if (!_masks.TryGetValue(mask, out var state)) return;
            var sourceRuns = DigitRuns(normalizedSource);
            if (sourceRuns.Count == 0 || !sourceRuns.SequenceEqual(DigitRuns(translation), StringComparer.Ordinal))
            {
                state.LastSource = state.LastTranslation = null;
                return;
            }
            state.LastSource = normalizedSource;
            state.LastTranslation = translation;
        }
    }

    public bool IsMuted(string normalizedText)
    {
        var mask = Mask(normalizedText);
        if (ReferenceEquals(mask, normalizedText)) return false;
        lock (_gate)
        {
            return _masks.TryGetValue(mask, out var state) && state.MutedUntil > _time.GetUtcNow();
        }
    }

    private static CounterDecision Muted(MaskState state, string normalizedText) =>
        state.LastSource is { } source && state.LastTranslation is { } translation
        && (Substitute(normalizedText, source, translation) ?? SubstituteDigitRuns(normalizedText, source, translation)) is { } substituted
            ? new CounterDecision(CounterAction.Substitute, substituted)
            : CounterDecision.Skip;

    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var key in _masks.Where(e => e.Value.MutedUntil <= now && e.Value.Recent.TrueForAll(r => now - r.At >= Window))
                     .Select(static e => e.Key).ToList())
            _masks.Remove(key);
        if (_masks.Count >= MaxMasks) _masks.Clear();
    }
}
