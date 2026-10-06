using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class TextBlockGrouperTests
{
    private static OcrLine Line(string text, int x, int y, int width = 200, int height = 20) =>
        new(text, new RectPx(x, y, width, height), [new OcrWord(text, new RectPx(x, y, width, height))]);

    [Fact]
    public void Group_skleja_sasiadujace_linie_tooltipa()
    {
        var lines = new[]
        {
            Line("Energy Shield: 120", 100, 100),
            Line("Armour: 50", 100, 124),
            Line("Evasion: 30", 100, 148),
        };

        var blocks = TextBlockGrouper.Group(lines);

        var block = Assert.Single(blocks);
        Assert.Equal("Energy Shield: 120\nArmour: 50\nEvasion: 30", block.Text);
        Assert.Equal(100, block.Box.Y);
        Assert.Equal(168, block.Box.Bottom);
    }

    [Fact]
    public void Group_oddziela_bloki_odlegle_w_pionie()
    {
        var lines = new[]
        {
            Line("Tooltip przedmiotu", 100, 100),
            Line("Dialog na dole ekranu", 100, 600),
        };

        var blocks = TextBlockGrouper.Group(lines);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Group_oddziela_kolumny_odlegle_w_poziomie()
    {
        var lines = new[]
        {
            Line("Lewa kolumna", 100, 100, width: 120),
            Line("Prawa kolumna", 600, 100, width: 120),
        };

        var blocks = TextBlockGrouper.Group(lines);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Group_zachowuje_kolejnosc_linii_od_gory()
    {
        var lines = new[]
        {
            Line("Druga", 100, 124),
            Line("Pierwsza", 100, 100),
        };

        var blocks = TextBlockGrouper.Group(lines);

        var block = Assert.Single(blocks);
        Assert.Equal("Pierwsza\nDruga", block.Text);
    }

    [Fact]
    public void Group_pustej_listy_zwraca_pusta_liste()
    {
        Assert.Empty(TextBlockGrouper.Group([]));
    }

    [Fact]
    public void Group_scala_klastry_polaczone_linia_mostkiem()
    {
        var lines = new[]
        {
            Line("Lewa kolumna", 0, 100, width: 80),
            Line("Prawa kolumna", 500, 100, width: 100),
            Line("Szeroki nagłówek pod spodem", 0, 130, width: 600),
        };

        var blocks = TextBlockGrouper.Group(lines);

        var block = Assert.Single(blocks);
        Assert.Equal(3, block.Lines.Count);
    }

    [Fact]
    public void Duzy_napis_w_kadrze_nie_skleja_odleglych_wierszy_hud()
    {
        var lines = new[]
        {
            Line("X", 125, 1866, 39, 41),
            Line("Hint", 224, 1864, 141, 52),
            Line("Tab", 121, 2012, 49, 22),
            Line("Items", 225, 1999, 191, 53),
            Line("EXIT", 2440, 922, 341, 174),
            Line("Escape!", 2423, 1727, 593, 208),
            Line("Welcome", 1200, 300, 600, 200),
        };

        var blocks = TextBlockGrouper.Group(lines);

        Assert.Contains(blocks, b => b.Text == "X Hint");
        Assert.DoesNotContain(blocks, b => b.Text.Contains("Hint") && b.Text.Contains("Items"));
    }
}
