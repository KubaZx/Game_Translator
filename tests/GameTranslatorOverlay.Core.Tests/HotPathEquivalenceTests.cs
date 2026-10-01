using System.Security.Cryptography;
using System.Text;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Szybkie ścieżki gorących miejsc (normalizacja, skrót tekstu, test kontrastu, odcisk regionu)
/// muszą dawać dokładnie to samo co prosta, oczywiście poprawna wersja — porównujemy je na
/// losowych danych ze stałym ziarnem.
/// </summary>
public class HotPathEquivalenceTests
{
    // Alfabet z pułapkami: spacje, tabulatory, CR/LF, twarda spacja, znaki zerowej szerokości,
    // BOM, znak łączący (akcent), miękki łącznik, NEL, półpauza, polskie litery i zwykłe ASCII.
    private static readonly char[] Alphabet =
    [
        'a', 'b', 'Z', '1', '+', '%', '.', ':', ' ', ' ', ' ', '\t', '\n', '\r', '\u00A0',
        '\u200B', '\u200C', '\u200D', '\uFEFF', '\u0301', '\u00AD', '\u0085', '\u2013', '\u2003',
        '\u0105', '\u0142', '\u017B', '\u00E9', 'e', '\u0007', '\u2028',
    ];

    [Fact]
    public void Normalizacja_ze_skrotem_daje_to_samo_co_pelna_dla_losowych_tekstow()
    {
        var random = new Random(20261001);
        var builder = new StringBuilder();
        for (var sample = 0; sample < 20000; sample++)
        {
            builder.Clear();
            var length = random.Next(0, 24);
            for (var i = 0; i < length; i++) builder.Append(Alphabet[random.Next(Alphabet.Length)]);
            var text = builder.ToString();
            Assert.Equal(TextNormalizer.NormalizeFull(text), TextNormalizer.Normalize(text));
        }
    }

    [Fact]
    public void Tekst_uznany_za_znormalizowany_naprawde_nie_zmienia_sie_w_pelnej_normalizacji()
    {
        // Każdy znak z zakresu skrótu, sam i w parze z każdym innym (NFC nie może niczego złożyć).
        var plain = Enumerable.Range(0x21, 0x17F - 0x21 + 1).Select(static c => (char)c)
            .Where(static c => TextNormalizer.IsTriviallyNormalized(c.ToString()))
            .ToArray();
        Assert.Contains('ą', plain);
        Assert.DoesNotContain('\u00A0', plain);
        Assert.DoesNotContain('\u0085', plain);
        foreach (var first in plain)
        {
            Assert.Equal(first.ToString(), TextNormalizer.NormalizeFull(first.ToString()));
            foreach (var second in plain)
            {
                var pair = string.Concat(first, second);
                Assert.True(pair.IsNormalized(NormalizationForm.FormC), $"U+{(int)first:X4} U+{(int)second:X4}");
            }
        }
    }

    [Theory]
    [InlineData("Quest 1: talk to the blacksmith.", true)]
    [InlineData("Zażółć gęślą jaźń\nDruga linia", true)]
    [InlineData(" leading", false)]
    [InlineData("trailing ", false)]
    [InlineData("double  space", false)]
    [InlineData("empty\n\nline", false)]
    [InlineData("line \nspace", false)]
    [InlineData("\nleading line", false)]
    [InlineData("trailing line\n", false)]
    [InlineData("tab\there", false)]
    [InlineData("10\u201315", false)]
    public void Skrot_normalizacji_obejmuje_tylko_czysty_tekst(string text, bool trivial)
    {
        Assert.Equal(trivial, TextNormalizer.IsTriviallyNormalized(text));
        Assert.Equal(TextNormalizer.NormalizeFull(text), TextNormalizer.Normalize(text));
        if (trivial) Assert.Same(text, TextNormalizer.Normalize(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("Zażółć gęślą jaźń — 🙂")]
    public void Sha256Hex_ma_niezmieniony_format_kolumny_text_hash(string text)
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(expected, TextHasher.Sha256Hex(text));
        Assert.Equal(64, TextHasher.Sha256Hex(text).Length);
    }

    /// <summary>Wzorcowa wersja testu kontrastu: piksel po pikselu, jak przed wektoryzacją.</summary>
    private static (bool HasContrast, bool IsUniform) ReferenceProbe(IEnumerable<byte[]> chunks)
    {
        int minB = 255, minG = 255, minR = 255, maxB = 0, maxG = 0, maxR = 0, sampled = 0;
        var invalid = false;
        foreach (var chunk in chunks)
        {
            if (chunk.Length % 4 != 0) { invalid = true; continue; }
            if (invalid) continue;
            for (var o = 0; o < chunk.Length; o += 4)
            {
                minB = Math.Min(minB, chunk[o]); maxB = Math.Max(maxB, chunk[o]);
                minG = Math.Min(minG, chunk[o + 1]); maxG = Math.Max(maxG, chunk[o + 1]);
                minR = Math.Min(minR, chunk[o + 2]); maxR = Math.Max(maxR, chunk[o + 2]);
                sampled++;
                if (maxB - minB > 16 || maxG - minG > 16 || maxR - minR > 16) return (true, false);
            }
        }
        return (false, !invalid && sampled >= 4);
    }

    [Fact]
    public void Wektorowy_test_kontrastu_zgadza_sie_z_przegladaniem_piksel_po_pikselu()
    {
        var random = new Random(4242);
        for (var sample = 0; sample < 3000; sample++)
        {
            var chunks = new List<byte[]>();
            var baseValue = random.Next(0, 240);
            // Różne zakresy szumu: w granicy 16 poziomów (jednolity), tuż ponad nią i duże.
            var spread = random.Next(4) switch { 0 => 16, 1 => 17, 2 => 8, _ => 60 };
            var chunkCount = random.Next(1, 5);
            for (var c = 0; c < chunkCount; c++)
            {
                // Długości także niewyrównane do wektora i większe niż porcja 1024 bajtów.
                var pixels = random.Next(0, 700);
                var chunk = new byte[pixels * 4 + (random.Next(40) == 0 ? 1 : 0)];
                for (var i = 0; i < chunk.Length; i++)
                    chunk[i] = (byte)Math.Min(255, baseValue + random.Next(spread + 1) * (random.Next(50) == 0 ? 1 : 0));
                // Alfa dowolna — nie może wpływać na wynik.
                for (var i = 3; i < chunk.Length; i += 4) chunk[i] = (byte)random.Next(256);
                // Czasem jeden odstający piksel w losowym miejscu (cienka kreska litery).
                if (chunk.Length >= 4 && random.Next(3) == 0)
                    chunk[random.Next(chunk.Length / 4) * 4 + random.Next(3)] = (byte)random.Next(256);
                chunks.Add(chunk);
            }

            var probe = new TextPresenceProbe();
            foreach (var chunk in chunks) probe.ObserveBgra32(chunk);
            var expected = ReferenceProbe(chunks);
            Assert.Equal(expected.HasContrast, probe.HasContrast);
            Assert.Equal(expected.IsUniform, probe.IsUniform);
        }
    }

    [Fact]
    public void Odcisk_jest_niezalezny_od_podzialu_na_porcje_i_kanalu_alfa_dla_losowych_obrazow()
    {
        var random = new Random(7);
        for (var sample = 0; sample < 50; sample++)
        {
            var width = random.Next(2, 300);
            var height = random.Next(2, 20);
            var pixels = new byte[width * height * 4];
            random.NextBytes(pixels);
            var frame = new OcrBitmap(pixels, width, height, width * 4);
            var reference = TextRegionFingerprint.FromBitmap(frame, new RectPx(0, 0, width, height));
            Assert.NotNull(reference);

            using var builder = new TextRegionFingerprint.Builder(width, height);
            var offset = 0;
            while (offset < pixels.Length)
            {
                var length = Math.Min(pixels.Length - offset, random.Next(1, 700) * 4);
                var chunk = pixels.AsSpan(offset, length).ToArray();
                for (var i = 3; i < chunk.Length; i += 4) chunk[i] = (byte)random.Next(256);
                builder.AppendBgra32(chunk);
                offset += length;
            }
            Assert.True(reference.Matches(builder.Finish()));
        }
    }

    /// <summary>Wzorcowa siatka luminancji: komórka po komórce, jak przed zmianą kolejności pętli.</summary>
    private static float[] ReferenceGrid(byte[] pixels, int width, int height, int stride, int columns, int rows)
    {
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
                for (var sy = 0; sy < LuminanceGrid.SamplesPerAxis; sy++)
                {
                    var y = LuminanceGrid.SampleCoordinate(cellTop, cellBottom, sy);
                    for (var sx = 0; sx < LuminanceGrid.SamplesPerAxis; sx++)
                    {
                        var offset = y * stride + LuminanceGrid.SampleCoordinate(cellLeft, cellRight, sx) * 4;
                        sum += 0.299f * pixels[offset + 2] + 0.587f * pixels[offset + 1] + 0.114f * pixels[offset];
                        count++;
                    }
                }
                cells[row * columns + column] = sum / count;
            }
        }
        return cells;
    }

    [Theory]
    [InlineData(1920, 1080, 0, 48, 27)]
    [InlineData(97, 53, 12, 48, 27)]
    [InlineData(5, 3, 4, 48, 27)]
    [InlineData(640, 360, 0, 300, 200)]
    [InlineData(1, 1, 0, 48, 27)]
    public void Siatka_luminancji_jest_co_do_bitu_taka_sama_jak_liczona_komorka_po_komorce(
        int width, int height, int padding, int columns, int rows)
    {
        var random = new Random(width * 31 + height);
        var stride = width * 4 + padding;
        var pixels = new byte[stride * height];
        random.NextBytes(pixels);

        var grid = LuminanceGrid.FromBgra32(pixels, width, height, stride, columns, rows);
        var expected = ReferenceGrid(pixels, width, height, stride, columns, rows);

        Assert.Equal(expected.Length, grid.Cells.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(grid.Cells[i]));
    }
}
