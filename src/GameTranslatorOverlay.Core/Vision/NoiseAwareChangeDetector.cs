using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Wynik analizy z rozróżnieniem zmian ISTOTNYCH od stałego szumu tła.
/// <paramref name="SignificantFraction"/> i <paramref name="SignificantRegion"/> pomijają
/// komórki, które migoczą klatka w klatkę (mgła, pogoda, animowane tło) — zostają zmiany
/// jednorazowe (nowy tekst, zamknięty tooltip) oraz każda zmiana mocna.
/// </summary>
public sealed record NoiseAwareAnalysis(
    double ChangedFraction,
    double StrongChangedFraction,
    double SignificantFraction,
    RectPx? SignificantRegion);

/// <summary>
/// Stanowy detektor zmian: dla każdej komórki siatki utrzymuje wykładniczy wynik
/// „jak często ostatnio się zmieniała”. Komórka zmieniająca się w większości ostatnich
/// klatek to szum sceny — pełnoklatkowy OCR co 600 ms w żywej grze 3D brał się właśnie
/// z tego, że rozproszony szum rozciągał region zmian na cały ekran. Zmiana mocna
/// (przesuw świata, nowy jasny tekst) zawsze liczy się jako istotna.
/// </summary>
public sealed class NoiseAwareChangeDetector(double decay = 0.8, double noiseThreshold = 0.4)
{
    private float[]? _scores;
    private int _columns;
    private int _rows;

    public NoiseAwareAnalysis Analyze(
        LuminanceGrid previous, LuminanceGrid current, int frameWidth, int frameHeight, double cellDelta = 10.0)
    {
        if (previous.Columns != current.Columns || previous.Rows != current.Rows)
        {
            Reset();
            return new NoiseAwareAnalysis(1.0, 1.0, 1.0, new RectPx(0, 0, frameWidth, frameHeight));
        }

        if (_scores is null || _columns != current.Columns || _rows != current.Rows)
        {
            _columns = current.Columns;
            _rows = current.Rows;
            _scores = new float[current.Cells.Length];
        }

        var strongDelta = cellDelta * 2.5;
        var changed = 0;
        var strong = 0;
        var significant = 0;
        var minColumn = int.MaxValue;
        var maxColumn = -1;
        var minRow = int.MaxValue;
        var maxRow = -1;

        for (var row = 0; row < current.Rows; row++)
        {
            for (var column = 0; column < current.Columns; column++)
            {
                var index = row * current.Columns + column;
                var delta = Math.Abs(previous.Cells[index] - current.Cells[index]);
                var isChanged = delta > cellDelta;
                var isStrong = delta > strongDelta;

                // Szum oceniamy po historii SPRZED tej klatki — świeża zmiana na spokojnej
                // komórce jest istotna, nawet jeśli od teraz zacznie migotać.
                var wasNoisy = _scores[index] >= noiseThreshold;
                _scores[index] = (float)(_scores[index] * decay + (isChanged ? 1 - decay : 0));

                if (!isChanged) continue;
                changed++;
                if (isStrong) strong++;
                if (!isStrong && wasNoisy) continue;

                significant++;
                if (column < minColumn) minColumn = column;
                if (column > maxColumn) maxColumn = column;
                if (row < minRow) minRow = row;
                if (row > maxRow) maxRow = row;
            }
        }

        var total = (double)current.Cells.Length;
        RectPx? region = null;
        if (significant > 0)
        {
            var x0 = minColumn * frameWidth / current.Columns;
            var x1 = (maxColumn + 1) * frameWidth / current.Columns;
            var y0 = minRow * frameHeight / current.Rows;
            var y1 = (maxRow + 1) * frameHeight / current.Rows;
            region = new RectPx(x0, y0, x1 - x0, y1 - y0);
        }

        return new NoiseAwareAnalysis(changed / total, strong / total, significant / total, region);
    }

    public void Reset() => _scores = null;
}
