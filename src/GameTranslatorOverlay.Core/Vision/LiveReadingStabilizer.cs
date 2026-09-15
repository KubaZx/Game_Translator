using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Vision;

public enum LiveReadingDecision { Keep, Confirm, Replace }

/// <summary>
/// Stabilizes successive OCR observations of an already displayed block. A pending
/// candidate occupies one slot per block and must repeat on the next observation;
/// unrelated partial scans need not reset it. The caller resets a missing block.
/// </summary>
public sealed class LiveReadingStabilizer
{
    private const double SimilarityThreshold = 0.5;
    private const double CleanQuality = 0.9;
    private const double QualityTolerance = 0.1;
    private readonly Dictionary<string, (string Displayed, string Candidate)> _pending = new(StringComparer.Ordinal);

    public LiveReadingDecision Observe(string key, string displayed, string candidate)
    {
        displayed = TextNormalizer.Normalize(displayed);
        candidate = TextNormalizer.Normalize(candidate);
        if (candidate.Length == 0 || string.Equals(candidate, displayed, StringComparison.OrdinalIgnoreCase))
        {
            Reset(key);
            return LiveReadingDecision.Keep;
        }

        var similarity = TextSimilarity.Ratio(candidate, displayed);
        var candidateQuality = ReadingQuality.Score(candidate);
        var displayedQuality = ReadingQuality.Score(displayed);
        // An obvious whole-word crop must not masquerade as an entirely new short
        // sentence. A materially cleaner reading can still correct an old bad OCR.
        if (candidateQuality <= displayedQuality + QualityTolerance && IsWholeWordFragment(candidate, displayed))
        {
            Reset(key);
            return LiveReadingDecision.Keep;
        }
        // Preserve immediate admission of a clearly different, clean new sentence.
        if (similarity < SimilarityThreshold && candidateQuality >= CleanQuality)
        {
            Reset(key);
            return LiveReadingDecision.Replace;
        }
        if (!CanReplace(candidate, displayed, similarity, candidateQuality, displayedQuality))
        {
            Reset(key);
            return LiveReadingDecision.Keep;
        }

        if (_pending.TryGetValue(key, out var previous)
            && string.Equals(previous.Displayed, displayed, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.Candidate, candidate, StringComparison.OrdinalIgnoreCase))
        {
            Reset(key);
            return LiveReadingDecision.Replace;
        }
        _pending[key] = (displayed, candidate);
        return LiveReadingDecision.Confirm;
    }

    public void Reset(string key) => _pending.Remove(key);
    public void Clear() => _pending.Clear();
    public void Prune(IReadOnlySet<string> liveKeys)
    {
        foreach (var key in _pending.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
            _pending.Remove(key);
    }

    private static bool CanReplace(string candidate, string displayed, double similarity,
        double candidateQuality, double displayedQuality)
    {
        if (candidateQuality < displayedQuality - QualityTolerance) return false;
        if (similarity < SimilarityThreshold) return true;
        if (candidateQuality > displayedQuality + QualityTolerance) return true;

        var candidateLetters = new string(candidate.Where(char.IsLetter).ToArray());
        var displayedLetters = new string(displayed.Where(char.IsLetter).ToArray());
        var candidateDigits = new string(candidate.Where(char.IsDigit).ToArray());
        var displayedDigits = new string(displayed.Where(char.IsDigit).ToArray());
        if (string.Equals(candidateLetters, displayedLetters, StringComparison.OrdinalIgnoreCase)
            && candidateDigits != displayedDigits) return true;
        if (candidate.Length >= displayed.Length + 2) return true;

        // A clean new meaning can be equally long or shorter (locked -> open).
        // Length alone must not freeze it forever. Still protect an obvious crop
        // that contains only a prefix/suffix of whole words from the old reading.
        return candidateQuality >= CleanQuality && !IsWholeWordFragment(candidate, displayed);
    }

    private static bool IsWholeWordFragment(string candidate, string displayed)
    {
        static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => word.Trim('.', ',', ':', ';', '!', '?', '\"', '\'', '(', ')', '[', ']'))
            .Where(static word => word.Length > 0).ToArray();
        var next = Words(candidate);
        var old = Words(displayed);
        if (next.Length == 0) return true;
        if (next.Length >= old.Length) return false;
        return next.SequenceEqual(old.Take(next.Length), StringComparer.OrdinalIgnoreCase)
            || next.SequenceEqual(old.Skip(old.Length - next.Length), StringComparer.OrdinalIgnoreCase);
    }
}