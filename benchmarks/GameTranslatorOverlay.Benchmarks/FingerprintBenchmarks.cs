using BenchmarkDotNet.Attributes;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Benchmarks;

/// <summary>
/// Odcisk regionu tekstu (skrót RGB + test kontrastu) liczony przez
/// <see cref="TextRegionFingerprint.Builder"/> wiersz po wierszu z pełnej klatki.
/// Rozmiary: pojedyncze słowo/etykieta (64×16), wiersz napisów (512×64) i największy
/// dopuszczalny region (512×512 = <see cref="TextRegionFingerprint.MaximumPixels"/>).
/// </summary>
[MemoryDiagnoser]
public class FingerprintBenchmarks
{
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;

    [Params("64x16", "512x64", "512x512")]
    public string Region { get; set; } = "64x16";

    private OcrBitmap _frame = null!;
    private OcrBitmap _emptyFrame = null!;
    private RectPx _box;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var pixels = new byte[FrameWidth * FrameHeight * 4];
        for (var y = 0; y < FrameHeight; y++)
        {
            for (var x = 0; x < FrameWidth; x++)
            {
                // Jasne „kreski liter” na ciemnym tle — region musi mieć kontrast,
                // inaczej budowniczy odrzuca go jako jednolity i pomiar nic nie znaczy.
                var glyph = (x / 3 + y / 5) % 4 == 0;
                var value = glyph ? (byte)235 : (byte)(30 + (x * 7 + y * 13) % 20);
                var offset = (y * FrameWidth + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        _frame = new OcrBitmap(pixels, FrameWidth, FrameHeight, FrameWidth * 4);
        var parts = Region.Split('x');
        _box = new RectPx(200, 300, int.Parse(parts[0]), int.Parse(parts[1]));

        if (TextRegionFingerprint.FromBitmap(_frame, _box) is null)
            throw new InvalidOperationException("Syntetyczny region nie dał odcisku — pomiar byłby fałszywy.");

        // Klatka po zniknięciu tekstu: jednolite tło z drobnym szumem (do 8 poziomów, poniżej
        // progu 16) — test kontrastu musi obejrzeć każdy piksel, zanim uzna region za pusty.
        var empty = new byte[FrameWidth * FrameHeight * 4];
        for (var i = 0; i < empty.Length; i += 4)
        {
            var value = (byte)(30 + (i / 4 * 7) % 9);
            empty[i] = value;
            empty[i + 1] = value;
            empty[i + 2] = value;
            empty[i + 3] = 255;
        }
        _emptyFrame = new OcrBitmap(empty, FrameWidth, FrameHeight, FrameWidth * 4);
        if (!TextPresenceProbe.IsClearlyEmpty(_emptyFrame, _box, 0xEBEBEB, 0x1E1E1E))
            throw new InvalidOperationException("Jednolity region nie został uznany za pusty — pomiar byłby fałszywy.");
    }

    [Benchmark]
    public TextRegionFingerprint? OdciskRegionu() => TextRegionFingerprint.FromBitmap(_frame, _box);

    /// <summary>
    /// <see cref="TextPresenceProbe.IsClearlyEmpty"/> na jednolitym regionie (tekst zniknął):
    /// najgorszy przypadek testu kontrastu — bez wczesnego wyjścia, każdy piksel RGB.
    /// </summary>
    [Benchmark]
    public bool PustyRegion() => TextPresenceProbe.IsClearlyEmpty(_emptyFrame, _box, 0xEBEBEB, 0x1E1E1E);
}
