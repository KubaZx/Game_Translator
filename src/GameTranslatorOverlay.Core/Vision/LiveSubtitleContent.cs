namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Tracks which source blocks belong to the latest subtitle. Removing an unrelated
/// old block must not erase a newer subtitle; removing one member preserves the rest.
/// This stores content only. Visibility and the existing expiry timer belong to the UI.
/// </summary>
public sealed class LiveSubtitleContent
{
    private const int MaximumLength = 400;
    private List<KeyValuePair<string, string>> _texts = [];

    public string Replace(IEnumerable<KeyValuePair<string, string>> freshTexts)
    {
        _texts = freshTexts.ToList();
        return Render();
    }

    /// <returns>
    /// Null if no current source was removed; empty if none remain; otherwise the
    /// remaining content. The caller must not revive an already expired subtitle.
    /// </returns>
    public string? Remove(IEnumerable<string> removedKeys)
    {
        var keys = removedKeys.ToHashSet(StringComparer.Ordinal);
        return _texts.RemoveAll(pair => keys.Contains(pair.Key)) > 0 ? Render() : null;
    }

    public bool IsEmpty => _texts.Count == 0;

    public bool Contains(string key) => _texts.Any(pair => string.Equals(pair.Key, key, StringComparison.Ordinal));

    public string Restore(IEnumerable<KeyValuePair<string, string>> returnedTexts)
    {
        foreach (var pair in returnedTexts)
        {
            if (!Contains(pair.Key)) _texts.Add(pair);
        }
        return Render();
    }

    public void Clear() => _texts.Clear();

    private string Render()
    {
        var text = string.Join('\n', _texts.Select(static pair => pair.Value)).Trim();
        if (text.Length <= MaximumLength) return text;
        var length = MaximumLength - 1; // The ellipsis is part of the 400-character limit.
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length] + "…";
    }
}