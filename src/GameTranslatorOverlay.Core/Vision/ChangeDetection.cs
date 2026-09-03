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
/// <summary>
/// Mini-siatka kolorów tła spod bloku (RGB, wiersz po wierszu). Rozciągnięta z interpolacją
/// daje rozmytą kopię tła — na grafice (zachód słońca za napisem) wtapia łatkę o wiele
/// lepiej niż jeden uśredniony kolor, a na płaskim oknie dialogowym jest po prostu płaska.
/// </summary>
public sealed record BackgroundTexture(byte[] Rgb, int Columns, int Rows)
{
    /// <summary>
    /// Średnia bezwzględna różnica kanałów (0–255) między dwiema teksturami; 255 przy
    /// różnych wymiarach. Pozwala nie podmieniać łatki, gdy zmienił się tylko szum
    /// próbkowania — inaczej rozmyta łatka „oddychała” co przebieg.
    /// </summary>
    public static double MeanDifference(BackgroundTexture a, BackgroundTexture b)
    {
        if (a.Columns != b.Columns || a.Rows != b.Rows || a.Rgb.Length != b.Rgb.Length || a.Rgb.Length == 0) return 255;
        long sum = 0;
        for (var i = 0; i < a.Rgb.Length; i++) sum += Math.Abs(a.Rgb[i] - b.Rgb[i]);
        return (double)sum / a.Rgb.Length;
    }
}

public sealed record BlockColors(int TextRgb, int BackgroundRgb, int OutlineRgb = -1)
{
    public static readonly BlockColors Unknown = new(-1, -1, -1);

    public BackgroundTexture? Texture { get; init; }
}

public static class BlockColorSampler
{
    /// <summary>Największy wycinek analizowany piksel po pikselu; większe są próbkowane co 2 px.</summary>
    private const int FullResolutionArea = 60_000;

    /// <summary>
    /// Pierścień wokół boxu OCR: tam na pewno nie ma liter, więc to najpewniejsza próbka
    /// tła — gruby kontur glifów potrafi liczebnie „wygrać” z tłem wewnątrz samego boxu.
    /// </summary>
    public const int RingMarginPx = 4;

    /// <summary>
    /// Zwraca kolory bloku jako 0xRRGGBB (-1 = nieustalony): tekst, tło oraz obwódka —
    /// czcionki gier prawie zawsze mają ciemny (lub jasny) kontur wokół glifów i to on
    /// robi „natywność” tłumaczenia. Kontur to piksele sąsiadujące z pikselami tekstu.
    /// Dodatkowo <see cref="BlockColors.Texture"/> — mini-siatka tła spod tekstu.
    /// </summary>
    public static BlockColors SampleColors(
        byte[] pixelsBgra32, int width, int height, int stride,
        GameTranslatorOverlay.Core.Ocr.RectPx box)
    {
        var innerX0 = Math.Max(0, box.X);
        var innerY0 = Math.Max(0, box.Y);
        var innerX1 = Math.Min(width, box.Right);
        var innerY1 = Math.Min(height, box.Bottom);
        if (innerX1 <= innerX0 || innerY1 <= innerY0) return BlockColors.Unknown;

        var x0 = Math.Max(0, innerX0 - RingMarginPx);
        var y0 = Math.Max(0, innerY0 - RingMarginPx);
        var x1 = Math.Min(width, innerX1 + RingMarginPx);
        var y1 = Math.Min(height, innerY1 + RingMarginPx);

        var step = (long)(x1 - x0) * (y1 - y0) > FullResolutionArea ? 2 : 1;
        var cols = (x1 - x0 + step - 1) / step;
        var rows = (y1 - y0 + step - 1) / step;
        var count = cols * rows;
        if (count < 16) return BlockColors.Unknown;

        var lums = new float[count];
        var rs = new byte[count];
        var gs = new byte[count];
        var bs = new byte[count];
        var inner = new bool[count];
        var i = 0;
        for (var y = y0; y < y1; y += step)
        {
            var rowOffset = y * stride;
            for (var x = x0; x < x1; x += step)
            {
                var offset = rowOffset + x * 4;
                var b = pixelsBgra32[offset];
                var g = pixelsBgra32[offset + 1];
                var r = pixelsBgra32[offset + 2];
                lums[i] = 0.299f * r + 0.587f * g + 0.114f * b;
                rs[i] = r;
                gs[i] = g;
                bs[i] = b;
                inner[i] = x >= innerX0 && x < innerX1 && y >= innerY0 && y < innerY1;
                i++;
            }
        }

        var min = lums.Min();
        var max = lums.Max();
        if (max - min < 30f)
        {
            // Płaski wycinek bez kontrastu — tekstu nie da się odróżnić, tło = średnia.
            var flat = new bool[count];
            return new BlockColors(-1, AverageRgb(rs, gs, bs, lums, _ => true))
            {
                Texture = BuildTexture(rs, gs, bs, flat, cols, rows),
            };
        }

        // 3-średnie po jasności (tło / tekst / ewentualna obwódka). Dwa klastry
        // myliłyby ciemną obwódkę jasnego tekstu z samym tekstem — kontur bywa
        // klastrem mniejszościowym i „wygrywałby” jako tekst.
        var centers = new[] { min, (min + max) / 2f, max };
        var cluster = new int[count];
        var counts = new int[3];
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var sums = new float[3];
            Array.Clear(counts);
            for (var k = 0; k < count; k++)
            {
                var best = 0;
                var bestDistance = Math.Abs(lums[k] - centers[0]);
                for (var c = 1; c < 3; c++)
                {
                    var distance = Math.Abs(lums[k] - centers[c]);
                    if (distance < bestDistance) { bestDistance = distance; best = c; }
                }
                cluster[k] = best;
                sums[best] += lums[k];
                counts[best]++;
            }
            for (var c = 0; c < 3; c++)
            {
                if (counts[c] > 0) centers[c] = sums[c] / counts[c];
            }
        }

        // Tło = klaster dominujący w PIERŚCIENIU wokół boxu (bez liter); gdy pierścienia
        // brak (box przy krawędzi klatki), zapasowo największy klaster w ogóle.
        var ringCounts = new int[3];
        var innerCounts = new int[3];
        for (var k = 0; k < count; k++)
        {
            if (inner[k]) innerCounts[cluster[k]]++;
            else ringCounts[cluster[k]]++;
        }
        var ringTotal = ringCounts.Sum();
        var backgroundCluster = ringTotal >= 16
            ? Array.IndexOf(ringCounts, ringCounts.Max())
            : Array.IndexOf(counts, counts.Max());

        // Tekst = liczniejszy z pozostałych klastrów WEWNĄTRZ boxu, kontur = mniej liczny.
        var others = Enumerable.Range(0, 3).Where(c => c != backgroundCluster).OrderByDescending(c => innerCounts[c]).ToArray();
        var textCluster = others[0];
        var outlineCluster = others[1];

        var minimumCluster = Math.Max(4, count / 50);
        if (innerCounts[textCluster] < minimumCluster)
        {
            var noText = new bool[count];
            return new BlockColors(-1, AverageRgb(rs, gs, bs, lums, _ => true))
            {
                Texture = BuildTexture(rs, gs, bs, noText, cols, rows),
            };
        }

        var isText = new bool[count];
        for (var k = 0; k < count; k++) isText[k] = cluster[k] == textCluster;

        var textRgb = AverageRgb(rs, gs, bs, lums, k => isText[k]);
        var backgroundRgb = AverageRgb(rs, gs, bs, lums, k => cluster[k] == backgroundCluster);
        var outlineRgb = counts[outlineCluster] >= Math.Max(8, minimumCluster)
            ? SampleOutline(rs, gs, bs, lums, isText, cluster, outlineCluster, cols, rows)
            : -1;

        // Do tekstury tła wykluczamy litery i wszystko, co ich dotyka (kontur, antyaliasing).
        var excluded = DilateMask(isText, cols, rows, iterations: 2);
        if (outlineRgb >= 0)
        {
            for (var k = 0; k < count; k++) excluded[k] |= cluster[k] == outlineCluster;
        }

        return new BlockColors(textRgb, backgroundRgb, outlineRgb)
        {
            Texture = BuildTexture(rs, gs, bs, excluded, cols, rows),
        };
    }

    private static bool[] DilateMask(bool[] mask, int cols, int rows, int iterations)
    {
        var current = (bool[])mask.Clone();
        for (var it = 0; it < iterations; it++)
        {
            var next = (bool[])current.Clone();
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    if (!current[row * cols + col] && TouchesText(current, cols, rows, col, row))
                    {
                        next[row * cols + col] = true;
                    }
                }
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Siatka średnich kolorów tła (do 12×4 komórek). Komórka bez pikseli tła (cała pod
    /// literami) dziedziczy kolor najbliższej wypełnionej komórki w wierszu, a w ostateczności
    /// średnią całej siatki — łatka nie może mieć dziur.
    /// </summary>
    private static BackgroundTexture BuildTexture(byte[] rs, byte[] gs, byte[] bs, bool[] excluded, int cols, int rows)
    {
        var textureCols = Math.Clamp(cols / 12, 2, 12);
        var textureRows = Math.Clamp(rows / 8, 1, 4);
        var sums = new long[textureCols * textureRows * 3];
        var counts = new int[textureCols * textureRows];

        for (var row = 0; row < rows; row++)
        {
            var tr = Math.Min(textureRows - 1, row * textureRows / rows);
            for (var col = 0; col < cols; col++)
            {
                var index = row * cols + col;
                if (excluded[index]) continue;
                var tc = Math.Min(textureCols - 1, col * textureCols / cols);
                var cell = tr * textureCols + tc;
                sums[cell * 3] += rs[index];
                sums[cell * 3 + 1] += gs[index];
                sums[cell * 3 + 2] += bs[index];
                counts[cell]++;
            }
        }

        var rgb = new byte[textureCols * textureRows * 3];
        var filled = new bool[textureCols * textureRows];
        long totalR = 0, totalG = 0, totalB = 0;
        var totalCount = 0;
        for (var cell = 0; cell < counts.Length; cell++)
        {
            if (counts[cell] == 0) continue;
            rgb[cell * 3] = (byte)(sums[cell * 3] / counts[cell]);
            rgb[cell * 3 + 1] = (byte)(sums[cell * 3 + 1] / counts[cell]);
            rgb[cell * 3 + 2] = (byte)(sums[cell * 3 + 2] / counts[cell]);
            filled[cell] = true;
            totalR += sums[cell * 3];
            totalG += sums[cell * 3 + 1];
            totalB += sums[cell * 3 + 2];
            totalCount += counts[cell];
        }

        var fallback = totalCount > 0
            ? new[] { (byte)(totalR / totalCount), (byte)(totalG / totalCount), (byte)(totalB / totalCount) }
            : new byte[] { 0x0B, 0x0E, 0x11 };

        for (var tr = 0; tr < textureRows; tr++)
        {
            for (var tc = 0; tc < textureCols; tc++)
            {
                var cell = tr * textureCols + tc;
                if (filled[cell]) continue;
                var source = -1;
                for (var distance = 1; distance < textureCols && source < 0; distance++)
                {
                    if (tc - distance >= 0 && filled[tr * textureCols + tc - distance]) source = tr * textureCols + tc - distance;
                    else if (tc + distance < textureCols && filled[tr * textureCols + tc + distance]) source = tr * textureCols + tc + distance;
                }
                if (source >= 0)
                {
                    rgb[cell * 3] = rgb[source * 3];
                    rgb[cell * 3 + 1] = rgb[source * 3 + 1];
                    rgb[cell * 3 + 2] = rgb[source * 3 + 2];
                }
                else
                {
                    rgb[cell * 3] = fallback[0];
                    rgb[cell * 3 + 1] = fallback[1];
                    rgb[cell * 3 + 2] = fallback[2];
                }
            }
        }

        return new BackgroundTexture(rgb, textureCols, textureRows);
    }

    /// <summary>
    /// Kontur to trzeci klaster, ale tylko wtedy, gdy jego piksele faktycznie STYKAJĄ SIĘ
    /// z tekstem (wyraźna większość) — w przeciwnym razie to np. drugi kolor tła.
    /// </summary>
    private static int SampleOutline(
        byte[] rs, byte[] gs, byte[] bs, float[] lums, bool[] isText, int[] cluster, int outlineCluster,
        int cols, int rows)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        var outlineCount = 0;
        var touching = 0;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var index = row * cols + col;
                if (cluster[index] != outlineCluster) continue;
                outlineCount++;
                if (!TouchesText(isText, cols, rows, col, row)) continue;
                touching++;
                sumR += rs[index];
                sumG += gs[index];
                sumB += bs[index];
            }
        }

        if (touching < 8 || touching < outlineCount * 0.6) return -1;
        return (int)(sumR / touching) << 16 | (int)(sumG / touching) << 8 | (int)(sumB / touching);
    }

    private static bool TouchesText(bool[] isText, int cols, int rows, int col, int row)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            var y = row + dy;
            if (y < 0 || y >= rows) continue;
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var x = col + dx;
                if (x < 0 || x >= cols) continue;
                if (isText[y * cols + x]) return true;
            }
        }
        return false;
    }

    private static int AverageRgb(byte[] rs, byte[] gs, byte[] bs, float[] lums, Func<int, bool> take)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        var count = 0;
        for (var i = 0; i < lums.Length; i++)
        {
            if (!take(i)) continue;
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
