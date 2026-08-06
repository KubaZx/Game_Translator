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
}
