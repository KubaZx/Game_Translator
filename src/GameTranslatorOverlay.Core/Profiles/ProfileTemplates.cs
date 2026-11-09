namespace GameTranslatorOverlay.Core.Profiles;

public static class ProfileTemplates
{
    public const string UniversalTemplateId = "generic";

    public static bool IsTemplate(GameProfile? profile) =>
        profile is not null && string.Equals(profile.Id, UniversalTemplateId, StringComparison.OrdinalIgnoreCase);
}
