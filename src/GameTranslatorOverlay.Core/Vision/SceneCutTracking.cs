namespace GameTranslatorOverlay.Core.Vision;

public static class SceneCutTracking
{
    public static IReadOnlySet<string> Retained(IReadOnlyDictionary<string, bool> trackedMoved, IEnumerable<string> remaining)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var moved = false;
        foreach (var key in remaining)
        {
            if (!trackedMoved.TryGetValue(key, out var shifted)) continue;
            kept.Add(key);
            moved |= shifted;
        }
        if (!moved) kept.Clear();
        return kept;
    }
}
