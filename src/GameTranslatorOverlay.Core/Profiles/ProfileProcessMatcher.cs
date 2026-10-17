using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Profiles;

public static class ProfileProcessMatcher
{
    public static GameProfile? FindByProcess(IEnumerable<GameProfile> profiles, string? processName, Func<GameProfile, bool>? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        GameProfile? first = null;
        foreach (var profile in profiles)
        {
            if (!Matches(profile, processName)) continue;
            if (preferred is null || preferred(profile)) return profile;
            first ??= profile;
        }
        return first;
    }

    public static GameProfile? AutoSelect(IEnumerable<GameProfile> profiles, GameProfile? current, string? processName, Func<GameProfile, bool>? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (FindByProcess(profiles, processName, preferred) is not { } match) return null;
        if (current is null) return match;
        if (string.Equals(current.Id, match.Id, StringComparison.Ordinal)) return null;
        return current.ProcessNames.Count > 0 && !Matches(current, processName) ? match : null;
    }

    private static bool Matches(GameProfile profile, string? processName) =>
        profile.ProcessNames.Any(name => LiveTargetResolver.SameProcess(name, processName));
}
