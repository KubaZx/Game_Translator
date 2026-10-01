using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
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

    private static OcrLine Line(string text, int y) =>
        new(text, new RectPx(100, y, 300, 20), [new OcrWord(text, new RectPx(100, y, 300, 20))]);

    [Fact]
    public void Linia_komunikatu_jest_usuwana_przed_grupowaniem_z_tekstem_gry()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("⚠ Brak klucza DeepL");
        // Komunikat tuż nad tytułem rozdziału — grupowanie skleiłoby je w jeden blok,
        // którego tekst nie przypomina już komunikatu.
        var lines = new[] { Line("A Brak klucza DeepL", 12), Line("Chapter 3 - The Docks", 34) };
        var merged = TextBlockGrouper.Group(lines);
        Assert.False(echo.IsEcho(Assert.Single(merged).Text));

        var filtered = echo.RemoveEchoLines(lines);

        var block = Assert.Single(TextBlockGrouper.Group(filtered));
        Assert.Equal("Chapter 3 - The Docks", block.Text);
    }

    [Fact]
    public void Bez_echa_lista_linii_zostaje_ta_sama()
    {
        var echo = new OverlayNoticeEcho();
        var lines = new[] { Line("Press E to open the door", 10) };
        Assert.Same(lines, echo.RemoveEchoLines(lines));

        echo.Remember("⚠ Brak klucza DeepL");
        Assert.Same(lines, echo.RemoveEchoLines(lines));
    }

    [Fact]
    public void Odczyt_o_bardzo_innej_dlugosci_nie_jest_echem()
    {
        var echo = new OverlayNoticeEcho();
        echo.Remember("Cache-only: 7 tekstów bez tłumaczenia");

        Assert.True(echo.IsEcho("Cache-only: 8 tekstów bez tłumaczenia"));
        Assert.False(echo.IsEcho("Cache-only: 7 tekstów bez tłumaczenia and the rest of a long dialogue line"));
    }
}
