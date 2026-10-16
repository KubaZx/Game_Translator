using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class SceneCutTrackingTests
{
    [Fact]
    public void Ciecie_w_szybkim_ruchu_kamery_zachowuje_stan_blokow_dalej_sledzonych()
    {
        var tracked = new Dictionary<string, bool> { ["tabliczka"] = true, ["menu"] = false };

        var kept = SceneCutTracking.Retained(tracked, ["tabliczka", "menu"]);

        Assert.Equal(["menu", "tabliczka"], kept.Order());
    }

    [Fact]
    public void Ciecie_bez_przesunietego_bloku_czysci_stan_jak_prawdziwa_zmiana_sceny()
    {
        var tracked = new Dictionary<string, bool> { ["menu"] = false, ["etykieta"] = false };

        Assert.Empty(SceneCutTracking.Retained(tracked, ["menu", "etykieta"]));
        Assert.Empty(SceneCutTracking.Retained(new Dictionary<string, bool>(), ["menu"]));
    }

    [Fact]
    public void Blok_zdjety_przy_cieciu_albo_niesledzony_w_tej_klatce_traci_stan()
    {
        var tracked = new Dictionary<string, bool> { ["tabliczka"] = true, ["znak"] = true };

        var kept = SceneCutTracking.Retained(tracked, ["tabliczka", "stary"]);

        Assert.Equal(["tabliczka"], kept);
    }

    [Fact]
    public void Przesuniety_blok_zdjety_przy_cieciu_nie_zachowuje_stanu_pozostalych()
    {
        var tracked = new Dictionary<string, bool> { ["tabliczka"] = true, ["menu"] = false };

        Assert.Empty(SceneCutTracking.Retained(tracked, ["menu"]));
    }
}
