using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Vision;

public sealed record LiveCorrection(
    string NormalizedText, string? CacheKey, string ProfileId, string SourceLanguage, string TargetLanguage)
{
    public bool AppliesTo(string profileId, string sourceLanguage, string targetLanguage) =>
        string.Equals(ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(SourceLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase)
        && string.Equals(TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase);

    public bool Covers(LiveOverlayBlock block) =>
        block is { Provisional: false, SourceText.Length: > 0 }
        && (string.Equals(block.SourceText, NormalizedText, StringComparison.Ordinal)
            || (block.CacheKey is { Length: > 0 } key && string.Equals(key, CacheKey ?? NormalizedText, StringComparison.Ordinal)));
}

public sealed class LiveCorrectionInbox
{
    private readonly Lock _gate = new();
    private readonly List<LiveCorrection> _pending = [];
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Arrived
    {
        get { lock (_gate) return _arrived.Task; }
    }

    public void Post(LiveCorrection correction)
    {
        lock (_gate)
        {
            _pending.Add(correction);
            _arrived.TrySetResult();
        }
    }

    public IReadOnlyList<LiveCorrection> TakeAll()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            var taken = _pending.ToArray();
            _pending.Clear();
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return taken;
        }
    }
}

public static class LiveCorrections
{
    public static IReadOnlyList<string> Affected(
        IEnumerable<KeyValuePair<string, LiveOverlayBlock>> blocks, IReadOnlyList<LiveCorrection> corrections) =>
        corrections.Count == 0
            ? []
            : blocks.Where(pair => corrections.Any(c => c.Covers(pair.Value))).Select(static pair => pair.Key).ToList();

    public static LiveOverlayBlock? Corrected(LiveOverlayBlock block, TranslationOutcome? outcome)
    {
        if (outcome?.TranslatedText is not { Length: > 0 } translated) return null;
        if (string.Equals(translated, block.TranslatedText, StringComparison.Ordinal) && outcome.QualityIssues == block.QualityIssues)
            return null;
        return block with
        {
            TranslatedText = translated,
            NormalizedTranslation = TextNormalizer.Normalize(translated),
            QualityIssues = outcome.QualityIssues,
            CacheKey = outcome.CacheKey ?? block.CacheKey,
        };
    }
}
