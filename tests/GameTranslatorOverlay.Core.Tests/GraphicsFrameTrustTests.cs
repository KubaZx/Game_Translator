using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class GraphicsFrameTrustTests
{
    private static bool Never() => throw new InvalidOperationException("Zrzut porównawczy nie powinien być potrzebny.");

    [Fact]
    public void Klatka_z_trescia_jest_uzywana_i_dowodzi_zrodla()
    {
        var trust = new GraphicsFrameTrust();

        Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: false, printWindowWorks: true, gameVisible: true, Never));
        Assert.True(trust.Proven);
    }

    [Fact]
    public void Jednolita_klatka_po_dowodzie_to_obraz_gry_bez_zrzutu_porownawczego()
    {
        var trust = new GraphicsFrameTrust();
        trust.Observe(uniform: false, printWindowWorks: false, gameVisible: true, Never);

        for (var i = 0; i < 20; i++)
            Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, Never));
    }

    [Fact]
    public void Jednolita_klatka_bez_dowodu_ustepuje_dzialajacemu_PrintWindow()
    {
        var trust = new GraphicsFrameTrust();

        Assert.Equal(GraphicsFrameVerdict.Reject, trust.Observe(uniform: true, printWindowWorks: true, gameVisible: true, Never));
        Assert.False(trust.Proven);
    }

    [Fact]
    public void Jednolita_klatka_bez_dowodu_przy_zaslonietej_grze_nie_siega_po_zrzut()
    {
        var trust = new GraphicsFrameTrust();

        for (var i = 0; i < 10; i++)
            Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: false, Never));
    }

    [Fact]
    public void Czarny_ekran_gry_potwierdzony_zrzutem_nie_porzuca_zrodla()
    {
        var trust = new GraphicsFrameTrust();

        for (var i = 0; i < 10; i++)
            Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => true));
    }

    [Fact]
    public void Trzy_jednolite_klatki_przy_tresci_na_ekranie_porzucaja_zrodlo()
    {
        var trust = new GraphicsFrameTrust();

        Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false));
        Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false));
        Assert.Equal(GraphicsFrameVerdict.Abandon, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false));
    }

    [Fact]
    public void Jednolity_zrzut_w_srodku_serii_zeruje_watpliwosci()
    {
        var trust = new GraphicsFrameTrust();
        trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false);
        trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false);
        trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => true);

        Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false));
        Assert.Equal(GraphicsFrameVerdict.Use, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false));
        Assert.Equal(GraphicsFrameVerdict.Abandon, trust.Observe(uniform: true, printWindowWorks: false, gameVisible: true, static () => false));
    }
}
