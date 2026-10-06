using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class ProfileOverlayTests
{
    [Fact]
    public void Profil_moze_wskazac_kroj_nakladki()
    {
        var profile = ProfileSerializer.FromJson("""
            { "id": "gra", "name": "Gra", "sourceLanguage": "en", "overlay": { "fontFamily": "Lexend Deca" } }
            """);

        Assert.Equal("Lexend Deca", profile.Overlay?.FontFamily);
        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Theory]
    [InlineData("C:\\Windows\\Fonts\\x.ttf")]
    [InlineData("./#Lexend")]
    [InlineData("   ")]
    public void Kroj_nie_moze_byc_sciezka_ani_pusty(string font)
    {
        var profile = new GameProfile { Id = "gra", Name = "Gra", Overlay = new OverlayProfileSettings { FontFamily = font } };

        Assert.Contains(ProfileValidator.Validate(profile), e => e.Contains("overlay.fontFamily", StringComparison.Ordinal));
    }

    [Fact]
    public void Profil_bez_sekcji_nakladki_jest_poprawny()
    {
        var profile = new GameProfile { Id = "gra", Name = "Gra" };

        Assert.Null(profile.Overlay);
        Assert.Empty(ProfileValidator.Validate(profile));
    }
}
