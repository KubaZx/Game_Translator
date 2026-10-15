using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Profiles;

public static class ProfileProcessMatcher
{
    public static GameProfile? FindByProcess(IEnumerable<GameProfile> profiles, string? processName)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.FirstOrDefault(p => Matches(p, processName));
    }

    public static GameProfile? AutoSelect(IEnumerable<GameProfile> profiles, GameProfile? current, string? processName)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (FindByProcess(profiles, processName) is not { } match) return null;
        if (current is null) return match;
        if (string.Equals(current.Id, match.Id, StringComparison.Ordinal)) return null;
        return current.ProcessNames.Count > 0 && !Matches(current, processName) ? match : null;
    }

    private static bool Matches(GameProfile profile, string? processName) =>
        profile.ProcessNames.Any(name => LiveTargetResolver.SameProcess(name, processName));
}
