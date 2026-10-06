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

    [Fact]
    public void Obszary_pomijane_w_live_sa_czytane_i_walidowane()
    {
        var profile = ProfileSerializer.FromJson("""
            { "id": "gra", "name": "Gra", "sourceLanguage": "en",
              "live": { "ignoreRegions": [ { "x": 0.425, "y": 0, "width": 0.15, "height": 0.08 } ] } }
            """);

        var region = Assert.Single(profile.Live!.IgnoreRegions!);
        Assert.True(region.Contains(0.5, 0.03));
        Assert.False(region.Contains(0.5, 0.2));
        Assert.Empty(ProfileValidator.Validate(profile));

        var broken = new GameProfile { Id = "gra", Name = "Gra", Live = new LiveProfileSettings { IgnoreRegions = [new RelativeRegion { X = 0.9, Y = 0, Width = 0.3, Height = 0.1 }] } };
        Assert.Contains(ProfileValidator.Validate(broken), e => e.Contains("live.ignoreRegions", StringComparison.Ordinal));
    }
}
