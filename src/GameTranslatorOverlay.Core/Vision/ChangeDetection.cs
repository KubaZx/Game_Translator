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

/// <summary>
/// <paramref name="ChangedFraction"/> — ułamek komórek zmienionych ponad próg bazowy
/// (łapie też subtelne animacje). <paramref name="StrongChangedFraction"/> — ułamek
/// komórek zmienionych MOCNO (2,5× progu): przewijanie świata bije w piksele o wiele
/// silniej niż falująca mgła, więc to on odróżnia bieg gracza od animacji otoczenia.
/// </summary>
public sealed record ChangeAnalysis(
    double ChangedFraction,
    GameTranslatorOverlay.Core.Ocr.RectPx? ChangedRegion,
    double StrongChangedFraction = 0);

public static class FrameChangeDetector
{
    /// <summary>
    /// Ułamek komórek (0–1), których średnia jasność zmieniła się bardziej niż
    /// <paramref name="cellDelta"/> (skala 0–255). Drobny szum animacji tła zostaje
    /// poniżej progu; nowy tekst/okno wyraźnie go przekracza.
    /// </summary>
    public static double ChangedFraction(LuminanceGrid previous, LuminanceGrid current, double cellDelta = 10.0) =>
        Analyze(previous, current, 1, 1, cellDelta).ChangedFraction;

    /// <summary>
    /// Pełna analiza zmiany: ułamek zmienionych komórek + prostokąt obejmujący zmiany
    /// (w pikselach klatki o wymiarach <paramref name="frameWidth"/>×<paramref name="frameHeight"/>).
    /// Region pozwala OCR-ować tylko zmieniony wycinek zamiast całej klatki.
    /// </summary>
    public static ChangeAnalysis Analyze(
        LuminanceGrid previous, LuminanceGrid current,
        int frameWidth, int frameHeight, double cellDelta = 10.0)
    {
        if (previous.Columns != current.Columns || previous.Rows != current.Rows)
        {
            return new ChangeAnalysis(1.0, new GameTranslatorOverlay.Core.Ocr.RectPx(0, 0, frameWidth, frameHeight));
        }

        var strongDelta = cellDelta * 2.5;
        var changed = 0;
        var strongChanged = 0;
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
                if (delta > cellDelta)
                {
                    changed++;
                    if (delta > strongDelta) strongChanged++;
                    if (column < minColumn) minColumn = column;
                    if (column > maxColumn) maxColumn = column;
                    if (row < minRow) minRow = row;
                    if (row > maxRow) maxRow = row;
                }
            }
        }

        if (changed == 0)
        {
            return new ChangeAnalysis(0.0, null);
        }

        var x0 = minColumn * frameWidth / current.Columns;
        var x1 = (maxColumn + 1) * frameWidth / current.Columns;
        var y0 = minRow * frameHeight / current.Rows;
        var y1 = (maxRow + 1) * frameHeight / current.Rows;

        return new ChangeAnalysis(
            (double)changed / current.Cells.Length,
            new GameTranslatorOverlay.Core.Ocr.RectPx(x0, y0, x1 - x0, y1 - y0),
            (double)strongChanged / current.Cells.Length);
    }
}

/// <summary>
/// Próbkuje dominujący kolor jasnych pikseli (tekstu) w prostokącie klatki BGRA32 —
/// tłumaczenie może wtedy przejąć kolor oryginału (np. kolor rzadkości przedmiotu).
/// </summary>
public static class TextColorSampler
{
    /// <summary>Zwraca kolor 0xRRGGBB albo -1, gdy w prostokącie nie ma wyraźnego tekstu.</summary>
    public static int SampleTextColorRgb(
        byte[] pixelsBgra32, int width, int height, int stride,
        GameTranslatorOverlay.Core.Ocr.RectPx box, byte minLuminance = 140)
    {
        var x0 = Math.Max(0, box.X);
        var y0 = Math.Max(0, box.Y);
        var x1 = Math.Min(width, box.Right);
        var y1 = Math.Min(height, box.Bottom);
        if (x1 <= x0 || y1 <= y0) return -1;

        long sumR = 0, sumG = 0, sumB = 0;
        var count = 0;

        for (var y = y0; y < y1; y += 2)
        {
            var rowOffset = y * stride;
            for (var x = x0; x < x1; x += 2)
            {
                var offset = rowOffset + x * 4;
                var b = pixelsBgra32[offset];
                var g = pixelsBgra32[offset + 1];
                var r = pixelsBgra32[offset + 2];
                var luminance = 0.299f * r + 0.587f * g + 0.114f * b;
                if (luminance >= minLuminance)
                {
                    sumR += r;
                    sumG += g;
                    sumB += b;
                    count++;
                }
            }
        }

        if (count < 8) return -1;
        return (int)(sumR / count) << 16 | (int)(sumG / count) << 8 | (int)(sumB / count);
    }
}

/// <summary>
/// Próbkuje z prostokąta klatki DWA kolory bloku tekstu: kolor znaków i kolor tła.
/// Piksele dzielone są na dwa klastry po jasności (2-średnie); tekst to klaster
/// MNIEJSZOŚCIOWY — działa więc zarówno dla jasnego tekstu na ciemnym tle (RPG),
/// jak i ciemnego tekstu na jasnym oknie (visual novele), gdzie sampler „tylko
/// jasnych pikseli” brał tło za tekst.
/// </summary>
public static class BlockColorSampler
{
    /// <summary>Zwraca (kolorTekstu, kolorTła) jako 0xRRGGBB; -1 gdy nie da się ustalić.</summary>
    public static (int TextRgb, int BackgroundRgb) SampleColors(
        byte[] pixelsBgra32, int width, int height, int stride,
        GameTranslatorOverlay.Core.Ocr.RectPx box)
    {
        var x0 = Math.Max(0, box.X);
        var y0 = Math.Max(0, box.Y);
        var x1 = Math.Min(width, box.Right);
        var y1 = Math.Min(height, box.Bottom);
        if (x1 <= x0 || y1 <= y0) return (-1, -1);

        var lums = new List<float>();
        var rs = new List<byte>();
        var gs = new List<byte>();
        var bs = new List<byte>();
        for (var y = y0; y < y1; y += 2)
        {
            var rowOffset = y * stride;
            for (var x = x0; x < x1; x += 2)
            {
                var offset = rowOffset + x * 4;
                var b = pixelsBgra32[offset];
                var g = pixelsBgra32[offset + 1];
                var r = pixelsBgra32[offset + 2];
                lums.Add(0.299f * r + 0.587f * g + 0.114f * b);
                rs.Add(r);
                gs.Add(g);
                bs.Add(b);
            }
        }

        if (lums.Count < 16) return (-1, -1);

        var min = lums.Min();
        var max = lums.Max();
        if (max - min < 30f)
        {
            // Płaski wycinek bez kontrastu — tekstu nie da się odróżnić, tło = średnia.
            return (-1, AverageRgb(rs, gs, bs, lums, threshold: float.MaxValue, takeBelow: true));
        }

        // 2-średnie po jasności: kilka iteracji w zupełności wystarcza.
        var threshold = (min + max) / 2f;
        for (var i = 0; i < 4; i++)
        {
            float sumLow = 0, sumHigh = 0;
            int countLow = 0, countHigh = 0;
            foreach (var lum in lums)
            {
                if (lum < threshold) { sumLow += lum; countLow++; }
                else { sumHigh += lum; countHigh++; }
            }
            if (countLow == 0 || countHigh == 0) break;
            threshold = (sumLow / countLow + sumHigh / countHigh) / 2f;
        }

        var below = lums.Count(l => l < threshold);
        var above = lums.Count - below;
        if (below == 0 || above == 0) return (-1, -1);

        // Znaki zajmują mniejszość pola bloku; przy remisie jaśniejszy klaster to tekst.
        var textIsDark = below < above;
        var textRgb = AverageRgb(rs, gs, bs, lums, threshold, takeBelow: textIsDark);
        var backgroundRgb = AverageRgb(rs, gs, bs, lums, threshold, takeBelow: !textIsDark);
        return (textRgb, backgroundRgb);
    }

    private static int AverageRgb(
        List<byte> rs, List<byte> gs, List<byte> bs, List<float> lums, float threshold, bool takeBelow)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        var count = 0;
        for (var i = 0; i < lums.Count; i++)
        {
            var isBelow = lums[i] < threshold;
            if (isBelow != takeBelow) continue;
            sumR += rs[i];
            sumG += gs[i];
            sumB += bs[i];
            count++;
        }
        if (count == 0) return -1;
        return (int)(sumR / count) << 16 | (int)(sumG / count) << 8 | (int)(sumB / count);
    }
}

/// <summary>
/// Debouncing zmian obrazu: OCR uruchamiamy, gdy po serii zmian obraz ustoi się na
/// <see cref="_stabilityDelay"/>. Gry z animowanym tłem nigdy nie „stoją” — dlatego
/// po <see cref="_maxDirtyDuration"/> ciągłych zmian przetwarzamy klatkę mimo wszystko,
/// inaczej tłumaczenie czekałoby w nieskończoność na spokój, który nie nadejdzie.
/// </summary>
public sealed class ChangeStabilizer(TimeSpan stabilityDelay, TimeSpan? maxDirtyDuration = null)
{
    private readonly TimeSpan _stabilityDelay = stabilityDelay;
    private readonly TimeSpan _maxDirtyDuration = maxDirtyDuration ?? TimeSpan.FromTicks(stabilityDelay.Ticks * 4);
    private bool _dirty;
    private TimeSpan _lastChangeAt;
    private TimeSpan _dirtySince;

    public bool IsDirty => _dirty;

    /// <summary>Zwraca true, gdy warto uruchomić OCR (stabilizacja albo wymuszenie po ciągłych zmianach).</summary>
    public bool Update(bool frameChanged, TimeSpan elapsed)
    {
        if (frameChanged)
        {
            if (!_dirty)
            {
                _dirtySince = elapsed;
            }
            _dirty = true;
            _lastChangeAt = elapsed;

            // Animowane tło: obraz zmienia się bez przerwy — przetwarzaj cyklicznie.
            if (elapsed - _dirtySince >= _maxDirtyDuration)
            {
                _dirtySince = elapsed;
                return true;
            }
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
        _dirtySince = elapsed;
    }

    public void Reset() => _dirty = false;
}
