using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class ProfileProcessMatcherTests
{
    private static GameProfile Profile(string id, params string[] processNames) =>
        new() { Id = id, Name = id, ProcessNames = [.. processNames] };

    private static readonly GameProfile[] Profiles =
    [
        Profile("szablon"),
        Profile("unreal", "Gra-Win64-Shipping.exe", "Gra.exe"),
        Profile("unity", "Moja Gra.exe"),
    ];

    [Theory]
    [InlineData("Gra-Win64-Shipping.exe", "unreal")]
    [InlineData("gra-win64-shipping", "unreal")]
    [InlineData("GRA.EXE", "unreal")]
    [InlineData("Moja Gra.exe", "unity")]
    [InlineData(" moja gra.exe ", "unity")]
    public void Dopasowanie_nie_zalezy_od_wielkosci_liter_ani_rozszerzenia(string processName, string expectedId)
    {
        var match = ProfileProcessMatcher.FindByProcess(Profiles, processName);

        Assert.NotNull(match);
        Assert.Equal(expectedId, match.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    [InlineData("Gra-Win64")]
    [InlineData("Moja")]
    [InlineData("GraCrashHandler64.exe")]
    public void Brak_profilu_dla_obcego_albo_pustego_procesu(string? processName)
    {
        Assert.Null(ProfileProcessMatcher.FindByProcess(Profiles, processName));
    }

    [Fact]
    public void Profil_bez_nazw_procesow_nigdy_sie_nie_dopasowuje()
    {
        GameProfile[] onlyTemplate = [Profile("szablon"), Profile("pusty", "", "  ")];

        Assert.Null(ProfileProcessMatcher.FindByProcess(onlyTemplate, ""));
        Assert.Null(ProfileProcessMatcher.FindByProcess(onlyTemplate, "szablon.exe"));
    }

    [Fact]
    public void Przy_powtorzonej_nazwie_procesu_wygrywa_pierwszy_profil_z_listy()
    {
        GameProfile[] duplicated = [Profile("pierwszy", "Wspolny.exe"), Profile("drugi", "wspolny")];

        Assert.Equal("pierwszy", ProfileProcessMatcher.FindByProcess(duplicated, "WSPOLNY.EXE")?.Id);
    }

    [Fact]
    public void Wybor_okna_innej_gry_przelacza_profil_gry_ale_nie_ogolny()
    {
        var first = Profile("gra-pierwsza", "Pierwsza.exe");
        var second = Profile("gra-druga", "Druga.exe");
        var generic = Profile("generic");
        GameProfile[] all = [generic, first, second];

        Assert.Equal("gra-druga", ProfileProcessMatcher.AutoSelect(all, null, "Druga.exe")?.Id);
        Assert.Equal("gra-druga", ProfileProcessMatcher.AutoSelect(all, first, "Druga.exe")?.Id);
        Assert.Null(ProfileProcessMatcher.AutoSelect(all, second, "Druga.exe"));
        Assert.Null(ProfileProcessMatcher.AutoSelect(all, generic, "Druga.exe"));
        Assert.Null(ProfileProcessMatcher.AutoSelect(all, first, "notepad.exe"));
    }
}
