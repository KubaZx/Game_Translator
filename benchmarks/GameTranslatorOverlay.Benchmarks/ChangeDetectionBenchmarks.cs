using BenchmarkDotNet.Attributes;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Benchmarks;

/// <summary>
/// Koszt decyzji „czy klatka się zmieniła” w pętli live: próbkowanie siatki luminancji
/// z pełnej klatki BGRA32 i analiza zmian z odsiewaniem szumu. Syntetyczne klatki mają
/// ziarno na pikselach (poniżej progu komórki — samo ziarno detektor uśrednia i ignoruje),
/// pas „mgły” w górnej części ekranu, który co klatkę jaśnieje i ciemnieje o 14 poziomów
/// luminancji (ponad próg komórki 10, poniżej progu mocnej zmiany 25 — po kilku klatkach
/// detektor uznaje go za szum), oraz jasny prostokąt przesuwający się między klatkami (nowy
/// tekst, ruch postaci — zmiana mocna). GlobalSetup sprawdza, że obie ścieżki detektora
/// (odsiew szumu i zmiana istotna) naprawdę pracują.
/// </summary>
[MemoryDiagnoser]
public class ChangeDetectionBenchmarks
{
    private const int FrameCount = 4;

    /// <summary>Migotanie mgły: ponad domyślny próg komórki (10), poniżej progu mocnej zmiany (25).</summary>
    private const int FogFlicker = 14;

    [Params("1920x1080", "3840x2160")]
    public string Resolution { get; set; } = "1920x1080";

    private int _width;
    private int _height;
    private byte[][] _frames = [];
    private LuminanceGrid[] _grids = [];
    private NoiseAwareChangeDetector _detector = new();
    private LuminanceGrid _previous = null!;
    private int _next;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var parts = Resolution.Split('x');
        _width = int.Parse(parts[0]);
        _height = int.Parse(parts[1]);
        var random = new Random(12345);

        // Kilka klatek z góry: generowanie 8–33 MB pikseli w pomiarze zagłuszyłoby wynik.
        _frames = new byte[FrameCount][];
        for (var f = 0; f < FrameCount; f++)
        {
            var pixels = new byte[_width * _height * 4];
            random.NextBytes(pixels);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                // Ciemne, zaszumione tło: każdy kanał 40–71, zmienny klatka w klatkę.
                pixels[i] = (byte)(40 + (pixels[i] & 31));
                pixels[i + 1] = (byte)(40 + (pixels[i + 1] & 31));
                pixels[i + 2] = (byte)(40 + (pixels[i + 2] & 31));
                pixels[i + 3] = 255;
            }

            // Mgła: górne 40% ekranu na zmianę jaśnieje i ciemnieje w kolejnych klatkach
            // (parzysta liczba klatek, więc także przejście 3 → 0 zmienia luminancję).
            if (f % 2 == 1)
            {
                var fogBytes = _height * 2 / 5 * _width * 4;
                for (var i = 0; i < fogBytes; i += 4)
                {
                    pixels[i] += FogFlicker;
                    pixels[i + 1] += FogFlicker;
                    pixels[i + 2] += FogFlicker;
                }
            }

            var rectWidth = _width / 8;
            var rectHeight = _height / 10;
            var rectX = _width / 10 + f * _width / 20;
            var rectY = _height / 2;
            for (var y = rectY; y < rectY + rectHeight; y++)
            {
                pixels.AsSpan((y * _width + rectX) * 4, rectWidth * 4).Fill(230);
            }

            _frames[f] = pixels;
        }

        _grids = _frames.Select(p => LuminanceGrid.FromBgra32(p, _width, _height, _width * 4)).ToArray();
        EnsureBothDetectorPathsWork();
        _detector = new NoiseAwareChangeDetector();
        _previous = _grids[0];
        _next = 1;
    }

    /// <summary>
    /// Bez tej kontroli ziarno mogłoby po cichu wypaść poniżej progu (tak było w pierwszej
    /// wersji: szum pikseli uśredniony z 9 próbek nigdy nie przekraczał progu komórki) i pomiar
    /// modelowałby spokojną scenę zamiast mgły — wynik czasowy prawie się nie zmienia, ale opis
    /// kłamałby każdemu, kto będzie stroił ścieżkę szumu na tym benchmarku.
    /// </summary>
    private void EnsureBothDetectorPathsWork()
    {
        var detector = new NoiseAwareChangeDetector();
        NoiseAwareAnalysis? last = null;
        for (var step = 0; step < 3 * FrameCount; step++)
        {
            last = detector.Analyze(
                _grids[step % FrameCount], _grids[(step + 1) % FrameCount], _width, _height);
        }

        // Po rozgrzaniu mgła zmienia się, ale jest odsiewana (zmienione > istotne),
        // a prostokąt nadal daje zmiany mocne i istotne.
        if (last is null
            || last.ChangedFraction <= last.SignificantFraction
            || last.StrongChangedFraction <= 0
            || last.SignificantFraction <= 0)
        {
            throw new InvalidOperationException(
                $"Klatki syntetyczne nie uruchamiają obu ścieżek detektora: {last}.");
        }
    }

    [Benchmark]
    public LuminanceGrid SiatkaLuminancji()
    {
        var frame = _frames[_next++ % FrameCount];
        return LuminanceGrid.FromBgra32(frame, _width, _height, _width * 4);
    }

    [Benchmark]
    public NoiseAwareAnalysis AnalizaZmian()
    {
        var index = _next++ % FrameCount;
        return _detector.Analyze(_grids[index], _grids[(index + 1) % FrameCount], _width, _height);
    }

    /// <summary>To, co pętla live robi z każdą przechwyconą klatką przed ewentualnym OCR.</summary>
    [Benchmark]
    public NoiseAwareAnalysis KlatkaPelna()
    {
        var frame = _frames[_next++ % FrameCount];
        var current = LuminanceGrid.FromBgra32(frame, _width, _height, _width * 4);
        var analysis = _detector.Analyze(_previous, current, _width, _height);
        _previous = current;
        return analysis;
    }
}
