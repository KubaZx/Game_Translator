using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class NoiseAwareChangeDetectorTests
{
    private const int Columns = 8;
    private const int Rows = 4;

    private static LuminanceGrid Grid(Func<int, float> cellValue)
    {
        var cells = new float[Columns * Rows];
        for (var i = 0; i < cells.Length; i++) cells[i] = cellValue(i);
        return new LuminanceGrid(Columns, Rows, cells);
    }

    [Fact]
    public void Stale_migotanie_tla_przestaje_byc_istotne_po_kilku_klatkach()
    {
        var detector = new NoiseAwareChangeDetector();
        // Lewa połowa siatki pulsuje co klatkę (±15), prawa stoi w miejscu.
        var previous = Grid(i => i % Columns < 4 ? 100 : 50);
        NoiseAwareAnalysis last = default!;
        for (var frame = 0; frame < 6; frame++)
        {
            var offset = frame % 2 == 0 ? 15f : 0f;
            var current = Grid(i => i % Columns < 4 ? 100 + offset : 50);
            last = detector.Analyze(previous, current, 800, 400);
            previous = current;
        }

        Assert.True(last.ChangedFraction > 0.4, "Szum dalej liczy się jako zmiana surowa");
        Assert.Equal(0.0, last.SignificantFraction);
        Assert.Null(last.SignificantRegion);
    }

    [Fact]
    public void Swieza_zmiana_na_spokojnej_komorce_jest_istotna_i_wyznacza_region()
    {
        var detector = new NoiseAwareChangeDetector();
        var quiet = Grid(_ => 50);
        detector.Analyze(quiet, quiet, 800, 400);

        // Nowy tekst: jedna komórka (kolumna 6, wiersz 1) jaśnieje o 20 (zmiana słaba).
        var withText = Grid(i => i == 1 * Columns + 6 ? 70 : 50);
        var analysis = detector.Analyze(quiet, withText, 800, 400);

        Assert.Equal(1.0 / (Columns * Rows), analysis.SignificantFraction, 3);
        Assert.Equal(new RectPx(600, 100, 100, 100), analysis.SignificantRegion);
    }

    [Fact]
    public void Mocna_zmiana_liczy_sie_zawsze_nawet_na_szumiacej_komorce()
    {
        var detector = new NoiseAwareChangeDetector();
        var previous = Grid(_ => 100);
        for (var frame = 0; frame < 6; frame++)
        {
            var current = Grid(_ => frame % 2 == 0 ? 115 : 100);
            detector.Analyze(previous, current, 800, 400);
            previous = current;
        }

        // Przesuw świata: wszystko zmienia się mocno.
        var strong = Grid(_ => previous.Cells[0] + 60);
        var analysis = detector.Analyze(previous, strong, 800, 400);

        Assert.Equal(1.0, analysis.StrongChangedFraction);
        Assert.Equal(1.0, analysis.SignificantFraction);
        Assert.Equal(new RectPx(0, 0, 800, 400), analysis.SignificantRegion);
    }

    [Fact]
    public void Zmiana_wymiarow_siatki_to_pelna_zmiana()
    {
        var detector = new NoiseAwareChangeDetector();
        var a = new LuminanceGrid(2, 2, new float[4]);
        var b = new LuminanceGrid(4, 4, new float[16]);

        var analysis = detector.Analyze(a, b, 400, 200);

        Assert.Equal(1.0, analysis.SignificantFraction);
        Assert.Equal(new RectPx(0, 0, 400, 200), analysis.SignificantRegion);
    }
}
