using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class OverlayNoticeEchoTests
{
    [Fact]
    public void Odczyt_wlasnego_komunikatu_jest_rozpoznany_mimo_zgubionej_ikony()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("⚠ Brak klucza DeepL");

        Assert.True(echo.IsEcho("⚠ Brak klucza DeepL"));
        Assert.True(echo.IsEcho("Brak klucza DeepL"));
        // OCR czyta ikonę ⚠ jako literę, a drobne litery myli.
        Assert.True(echo.IsEcho("A Brak klucza DeepL"));
        Assert.True(echo.IsEcho("Brak kIucza DeepL"));
    }

    [Fact]
    public void Tekst_gry_nie_jest_echem()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("⚠ Brak klucza DeepL");
        echo.Remember("Cache-only: 3 teksty bez tłumaczenia");

        Assert.False(echo.IsEcho("Press E to open the door"));
        Assert.False(echo.IsEcho("OK"));
        Assert.False(echo.IsEcho(""));
        Assert.False(echo.IsEcho("!!!"));
    }

    [Fact]
    public void Krotkie_odczyty_wymagaja_dokladnej_zgodnosci()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("ABC");

        Assert.True(echo.IsEcho("abc"));
        Assert.False(echo.IsEcho("abd"));
    }

    [Fact]
    public void Bez_zapamietanych_komunikatow_nic_nie_jest_echem()
    {
        Assert.False(new OverlayNoticeEcho().IsEcho("⚠ Brak klucza DeepL"));
    }

    [Fact]
    public void Rejestr_trzyma_tylko_ostatnie_komunikaty()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("Najstarszy komunikat z rejestru");
        echo.Remember("   ");
        for (var i = 0; i < 40; i++) echo.Remember($"Komunikat numer {i} kontrolny");

        Assert.False(echo.IsEcho("Najstarszy komunikat z rejestru"));
        Assert.True(echo.IsEcho("Komunikat numer 39 kontrolny"));
    }

    [Fact]
    public void Ponowne_zapamietanie_odswieza_komunikat()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("Pierwszy komunikat testowy");
        for (var i = 0; i < 31; i++)
        {
            echo.Remember($"Inny komunikat numer {i}");
            if (i % 10 == 0) echo.Remember("Pierwszy komunikat testowy");
        }

        Assert.True(echo.IsEcho("Pierwszy komunikat testowy"));
    }
}
