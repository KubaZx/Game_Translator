using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveHotkeyMessagesTests
{
    [Fact]
    public void Zaznaczona_gra_i_zarejestrowany_skrot_podpowiada_skrot()
    {
        var message = LiveHotkeyMessages.WindowListRefreshed(7, rememberedGameSelected: true, "Ctrl+Shift+L");

        Assert.Equal("Znaleziono 7 okien. Zaznaczono ostatnią grę — kliknij Start live albo wciśnij Ctrl+Shift+L w grze.", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Niezarejestrowany_skrot_nie_jest_podpowiadany(string? hotkey)
    {
        var message = LiveHotkeyMessages.WindowListRefreshed(7, rememberedGameSelected: true, hotkey);

        Assert.Equal("Znaleziono 7 okien. Zaznaczono ostatnią grę — kliknij Start live.", message);
        Assert.DoesNotContain("wciśnij", message);
    }

    [Theory]
    [InlineData("Ctrl+Shift+L")]
    [InlineData(null)]
    public void Bez_zapamietanej_gry_komunikat_prosi_o_wybor_okna(string? hotkey)
    {
        var message = LiveHotkeyMessages.WindowListRefreshed(3, rememberedGameSelected: false, hotkey);

        Assert.Equal("Znaleziono 3 okien. Wybierz okno gry albo od razu użyj Ctrl+Shift+T.", message);
    }
}
