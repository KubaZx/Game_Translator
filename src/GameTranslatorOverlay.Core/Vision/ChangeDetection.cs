namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Zredukowana mapa jasności klatki (siatka komórek). Porównywanie dwóch siatek
/// jest o rzędy wielkości tańsze niż porównywanie pełnych klatek — wystarcza
/// do stwierdzenia „obraz się zmienił / stoi w miejscu”.
/// </summary>
public sealed record LuminanceGrid(int Columns, int Rows, float[] Cells)
{
    public const int DefaultColumns = 48;
    public const int DefaultRows = 27;
    public const int SamplesPerAxis = 3;

    /// <summary>Współrzędna próbki nr <paramref name="index"/> wewnątrz komórki [start, end).</summary>
    public static int SampleCoordinate(int cellStart, int cellEnd, int index) =>
        cellStart + (cellEnd - cellStart) * (2 * index + 1) / (2 * SamplesPerAxis);

    /// <summary>
    /// Wiersze obrazu faktycznie próbkowane przez <see cref="FromBgra32"/> — pozwala
    /// wołającemu skopiować z klatki tylko te wiersze zamiast całego obrazu.
    /// </summary>
    public static IEnumerable<int> GetSampledRows(int height, int rows = DefaultRows)
    {
        rows = Math.Min(rows, height);
        var seen = new HashSet<int>();
        for (var row = 0; row < rows; row++)
        {
            var cellTop = row * height / rows;
            var cellBottom = Math.Max(cellTop + 1, (row + 1) * height / rows);
            for (var sy = 0; sy < SamplesPerAxis; sy++)
            {
                var y = SampleCoordinate(cellTop, cellBottom, sy);
                if (seen.Add(y))
                {
                    yield return y;
                }
            }
        }
    }

    /// <summary>Buduje siatkę z surowych pikseli BGRA32, próbkując po kilka punktów na komórkę.</summary>
    public static LuminanceGrid FromBgra32(
        byte[] pixels, int width, int height, int stride,
        int columns = DefaultColumns, int rows = DefaultRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        columns = Math.Min(columns, width);
        rows = Math.Min(rows, height);
        var cells = new float[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            var cellTop = row * height / rows;
            var cellBottom = Math.Max(cellTop + 1, (row + 1) * height / rows);

            for (var column = 0; column < columns; column++)
            {
                var cellLeft = column * width / columns;
                var cellRight = Math.Max(cellLeft + 1, (column + 1) * width / columns);

                var sum = 0f;
                var count = 0;
                for (var sy = 0; sy < SamplesPerAxis; sy++)
                {
                    var y = SampleCoordinate(cellTop, cellBottom, sy);
                    for (var sx = 0; sx < SamplesPerAxis; sx++)
                    {
                        var x = SampleCoordinate(cellLeft, cellRight, sx);
                        var offset = y * stride + x * 4;
                        var b = pixels[offset];
                        var g = pixels[offset + 1];
                        var r = pixels[offset + 2];
                        sum += 0.299f * r + 0.587f * g + 0.114f * b;
                        count++;
                    }
                }

                cells[row * columns + column] = sum / count;
            }
        }

        return new LuminanceGrid(columns, rows, cells);
    }
}

public static class FrameChangeDetector
{
    /// <summary>
    /// Ułamek komórek (0–1), których średnia jasność zmieniła się bardziej niż
    /// <paramref name="cellDelta"/> (skala 0–255). Drobny szum animacji tła zostaje
    /// poniżej progu; nowy tekst/okno wyraźnie go przekracza.
    /// </summary>
    public static double ChangedFraction(LuminanceGrid previous, LuminanceGrid current, double cellDelta = 10.0)
    {
        if (previous.Columns != current.Columns || previous.Rows != current.Rows)
        {
            return 1.0;
        }

        var changed = 0;
        for (var i = 0; i < current.Cells.Length; i++)
        {
            if (Math.Abs(previous.Cells[i] - current.Cells[i]) > cellDelta)
            {
                changed++;
            }
        }

        return (double)changed / current.Cells.Length;
    }
}

/// <summary>
/// Debouncing zmian obrazu: OCR uruchamiamy dopiero, gdy po serii zmian obraz
/// ustoi się na <see cref="_stabilityDelay"/>. Chroni przed rozpoznawaniem
/// półprzezroczystych animacji i migotaniem wyników.
/// </summary>
public sealed class ChangeStabilizer(TimeSpan stabilityDelay)
{
    private readonly TimeSpan _stabilityDelay = stabilityDelay;
    private bool _dirty;
    private TimeSpan _lastChangeAt;

    public bool IsDirty => _dirty;

    /// <summary>Zwraca true dokładnie raz — gdy obraz po zmianach właśnie się ustabilizował.</summary>
    public bool Update(bool frameChanged, TimeSpan elapsed)
    {
        if (frameChanged)
        {
            _dirty = true;
            _lastChangeAt = elapsed;
            return false;
        }

        if (_dirty && elapsed - _lastChangeAt >= _stabilityDelay)
        {
            _dirty = false;
            return true;
        }

        return false;
    }

    public void ForceDirty(TimeSpan elapsed)
    {
        _dirty = true;
        _lastChangeAt = elapsed;
    }

    public void Reset() => _dirty = false;
}
