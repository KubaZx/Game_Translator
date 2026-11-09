using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class ProfileTemplatesTests
{
    [Theory]
    [InlineData("generic", true)]
    [InlineData("GENERIC", true)]
    [InlineData("generic-kopia", false)]
    [InlineData("", false)]
    public void Tylko_profil_uniwersalny_jest_szablonem(string id, bool expected) =>
        Assert.Equal(expected, ProfileTemplates.IsTemplate(new GameProfile { Id = id }));

    [Fact]
    public void Brak_profilu_nie_jest_szablonem() => Assert.False(ProfileTemplates.IsTemplate(null));
}
