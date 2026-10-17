using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LostTextRecoveryTests
{
    private const int Width = 900;
    private const int Height = 320;
    private static readonly RectPx Frame = new(0, 0, Width, Height);

    [Fact]
    public void Jadacy_napis_zgubiony_w_ruchu_dostaje_lokalny_odczyt_nawet_bez_liter_w_starym_miejscu()
    {
        Assert.True(LostTextRecovery.ShouldRead(moving: true, sceneCut: false, hasTrack: true, screenFallback: false, travelling: true, lettersGone: true));
        Assert.True(LostTextRecovery.ShouldRead(moving: true, sceneCut: false, hasTrack: true, screenFallback: false, travelling: true, lettersGone: false));
    }

    [Fact]
    public void Stojacy_napis_w_ruchu_kamery_dostaje_odczyt_tylko_gdy_litery_zostaly_w_miejscu()
    {
        Assert.True(LostTextRecovery.ShouldRead(moving: true, sceneCut: false, hasTrack: true, screenFallback: false, travelling: false, lettersGone: false));
        Assert.False(LostTextRecovery.ShouldRead(moving: true, sceneCut: false, hasTrack: true, screenFallback: false, travelling: false, lettersGone: true));
    }

    [Fact]
    public void Ciecie_sceny_nieruchoma_scena_zrzut_ekranu_i_brak_sladu_usuwaja_napis_od_razu()
    {
        Assert.False(LostTextRecovery.ShouldRead(moving: true, sceneCut: true, hasTrack: true, screenFallback: false, travelling: true, lettersGone: false));
        Assert.False(LostTextRecovery.ShouldRead(moving: false, sceneCut: false, hasTrack: true, screenFallback: false, travelling: true, lettersGone: false));
        Assert.False(LostTextRecovery.ShouldRead(moving: true, sceneCut: false, hasTrack: true, screenFallback: true, travelling: true, lettersGone: false));
        Assert.False(LostTextRecovery.ShouldRead(moving: true, sceneCut: false, hasTrack: false, screenFallback: false, travelling: true, lettersGone: false));
    }

    [Fact]
    public void Obszar_odczytu_to_przewidziane_miejsce_z_promieniem_szerokiego_szukania_przyciete_do_klatki()
    {
        var box = new RectPx(300, 120, 200, 50);

        var area = LostTextRecovery.ReadArea(box, 30.4, -10.6, 72 * GlyphFollow.WideReach, new RectPx(0, 0, 1920, 1080));
        var clipped = LostTextRecovery.ReadArea(box, 0, 0, 216, Frame);

        Assert.Equal(new RectPx(330 - 216, 109 - 216, 200 + 432, 50 + 432).Intersect(new RectPx(0, 0, 1920, 1080)), area);
        Assert.Equal(new RectPx(84, 0, 632, 320), clipped);
    }

    [Fact]
    public void Ten_sam_tekst_to_odczyt_identyczny_albo_drobny_wariant_ocr_bez_zmiany_liczb_a_wiarygodna_zmiana_tresci_nie()
    {
        Assert.True(LiveReadingStabilizer.IsSameText("Tab Items", "tab items"));
        Assert.True(LiveReadingStabilizer.IsSameText("Tab Items", "Tab ltems"));
        Assert.False(LiveReadingStabilizer.IsSameText("Journal panel", "Journal closed"));
        Assert.False(LiveReadingStabilizer.IsSameText("Hint", "x Hint"));
        Assert.True(LiveReadingStabilizer.IsSameText("LOADING...", "L0ADING..."));
        Assert.False(LiveReadingStabilizer.IsSameText("Level 20", "Level 21"));
        Assert.False(LiveReadingStabilizer.IsSameText("Levei 20", "Level 21"));
        Assert.False(LiveReadingStabilizer.IsSameText("Tab Items", "Tab"));
        Assert.False(LiveReadingStabilizer.IsSameText("The door is locked", "Mroczna Latarnia"));
        Assert.False(LiveReadingStabilizer.IsSameText("Hint", ""));
    }

    [Fact]
    public void Odczyt_wskazuje_ten_sam_tekst_najblizej_przewidzianego_miejsca()
    {
        var previous = new RectPx(300, 120, 200, 50);
        var predicted = previous.Offset(40, 0);
        var area = LostTextRecovery.ReadArea(previous, 40, 0, 216, Frame);
        var far = Block("Open the door", new RectPx(500, 220, 200, 50));
        var near = Block("Open the d00r", new RectPx(350, 124, 196, 48));
        var other = Block("Mroczna Latarnia", new RectPx(330, 120, 200, 50));

        var found = LostTextRecovery.FindSame("Open the door", previous, [far, other, near], predicted, area, Frame);

        Assert.Same(near, found);
    }

    [Fact]
    public void Inny_tekst_ucinek_przy_krawedzi_wycinka_i_blok_innej_wielkosci_nie_potwierdzaja_napisu()
    {
        var previous = new RectPx(300, 120, 200, 50);
        var area = new RectPx(200, 60, 400, 180);

        Assert.Null(LostTextRecovery.FindSame("Open the door", previous, [Block("Mroczna Latarnia", new RectPx(300, 120, 200, 50))], previous, area, Frame));
        Assert.Null(LostTextRecovery.FindSame("Open the door", previous, [Block("Open the door", new RectPx(200, 120, 200, 50))], previous, area, Frame));
        Assert.Null(LostTextRecovery.FindSame("Open the door", previous, [Block("Open the door", new RectPx(220, 80, 360, 140))], previous, area, Frame));
        Assert.NotNull(LostTextRecovery.FindSame("Open the door", previous, [Block("Open the door", new RectPx(0, 120, 200, 50))], previous,
            new RectPx(0, 60, 400, 180), Frame));
    }

    [Fact]
    public void Linie_z_wycinka_wracaja_do_ukladu_okna_a_obszar_pomijany_w_profilu_odpada()
    {
        var image = Solid(400, 200, 0x202020);
        var area = new RectPx(500, 100, 200, 100);
        var lines = new List<OcrLine>
        {
            Line("Hill", new RectPx(20, 40, 120, 30)),
            Line("Clock", new RectPx(300, 10, 80, 20)),
        };
        var ignore = new List<RelativeRegion> { new() { X = 0.6, Y = 0.0, Width = 0.4, Height = 0.4 } };

        var blocks = LostTextRecovery.Blocks(image, lines, area, 0.5, new RectPx(0, 0, 1000, 500), ignore, detectIcons: false);

        var block = Assert.Single(blocks);
        Assert.Equal("Hill", block.Text);
        Assert.Equal(new RectPx(510, 120, 60, 15), block.Box);
    }

    [Fact]
    public void Limit_odczytow_na_sekunde_i_na_blok_oraz_jeden_odczyt_naraz_dla_bloku()
    {
        var reads = new LostTextReads<string>(maxPerSecond: 3, maxPerBlock: 2, maxWait: TimeSpan.FromMilliseconds(300));
        var t0 = TimeSpan.FromSeconds(10);

        Assert.True(reads.TryBegin("a", t0, () => "a1"));
        Assert.False(reads.TryBegin("a", t0 + TimeSpan.FromMilliseconds(10), () => "a2"));
        Assert.True(reads.Remove("a", out var first));
        Assert.Equal("a1", first);
        Assert.True(reads.TryBegin("a", t0 + TimeSpan.FromMilliseconds(150), () => "a2"));
        reads.Remove("a", out _);
        Assert.False(reads.CanBegin("a", t0 + TimeSpan.FromMilliseconds(400)));
        Assert.True(reads.TryBegin("b", t0 + TimeSpan.FromMilliseconds(420), () => "b1"));
        Assert.False(reads.CanBegin("c", t0 + TimeSpan.FromMilliseconds(430)));
        Assert.False(reads.CanBegin("c", t0 + TimeSpan.FromMilliseconds(999)));
        Assert.True(reads.CanBegin("c", t0 + TimeSpan.FromMilliseconds(1000)));
        Assert.True(reads.TryBegin("a", t0 + TimeSpan.FromMilliseconds(1100), () => "a3"));
    }

    [Fact]
    public void Odczyt_czeka_najwyzej_krotko_a_zmiana_sceny_oddaje_wszystkie_do_anulowania_bez_odnowienia_limitu()
    {
        var reads = new LostTextReads<string>(2, 2, TimeSpan.FromMilliseconds(300));
        var t0 = TimeSpan.FromSeconds(3);
        var started = 0;
        reads.TryBegin("a", t0, () => { started++; return "a"; });
        reads.TryBegin("b", t0, () => { started++; return "b"; });

        Assert.False(reads.IsExpired("a", t0 + TimeSpan.FromMilliseconds(300)));
        Assert.True(reads.IsExpired("a", t0 + TimeSpan.FromMilliseconds(301)));
        Assert.Equal(["a", "b"], reads.Clear().Order());
        Assert.Equal(0, reads.Count);
        Assert.False(reads.IsPending("a"));
        Assert.Equal(2, started);
        Assert.False(reads.TryBegin("c", t0 + TimeSpan.FromMilliseconds(50), () => { started++; return "c"; }));
        Assert.Equal(2, started);
        Assert.True(reads.TryBegin("c", t0 + TimeSpan.FromSeconds(1), () => { started++; return "c"; }));
    }

    [Fact]
    public void Napis_potwierdzony_odczytem_jest_dalej_sledzony_starym_sladem_w_nastepnej_klatce()
    {
        var before = Scene(seed: 1, x: 120, y: 120);
        var box = Glyphs(120, 120);
        var cover = GlyphCoverBuilder.Build(before, box, [box], ["Hill"])! with { Anchor = box };
        var read = Scene(seed: 2, x: 230, y: 124);
        var found = LostTextRecovery.Rebuild(read, Block("Hill", Glyphs(230, 124)), Frame, 1.0, patch: true);
        Assert.NotNull(found);
        var next = Scene(seed: 3, x: 340, y: 128);

        var resumed = LostTextRecovery.Resume(cover, box, found, area => Crop(next, area), Frame, 72, patch: true);

        Assert.NotNull(resumed);
        Assert.False(resumed.Rebuilt);
        Assert.InRange(resumed.Box.X, box.X + 218, box.X + 222);
        Assert.InRange(resumed.Box.Y, box.Y + 6, box.Y + 10);
        Assert.InRange(resumed.VelocityX * resumed.Cover.Track!.Step, 100, 120);
    }

    [Fact]
    public void Po_zatrzymaniu_kamery_napis_zostaje_w_miejscu_potwierdzonym_odczytem()
    {
        var before = Scene(seed: 4, x: 120, y: 120);
        var box = Glyphs(120, 120);
        var cover = GlyphCoverBuilder.Build(before, box, [box], ["Hill"])! with { Anchor = box };
        var read = Scene(seed: 5, x: 230, y: 120);
        var found = LostTextRecovery.Rebuild(read, Block("Hill", Glyphs(230, 120)), Frame, 1.0, patch: true)!;
        var stopped = Scene(seed: 6, x: 230, y: 120);

        var resumed = LostTextRecovery.Resume(cover, box, found, area => Crop(stopped, area), Frame, 72, patch: true);

        Assert.NotNull(resumed);
        Assert.InRange(resumed.Box.X, Glyphs(230, 120).X - 2, Glyphs(230, 120).X + 2);
    }

    [Fact]
    public void Napis_ktory_w_nastepnej_klatce_zniknal_albo_jest_daleko_od_odczytu_nie_wraca()
    {
        var before = Scene(seed: 7, x: 120, y: 120);
        var box = Glyphs(120, 120);
        var cover = GlyphCoverBuilder.Build(before, box, [box], ["Hill"])! with { Anchor = box };
        var read = Scene(seed: 8, x: 230, y: 120);
        var found = LostTextRecovery.Rebuild(read, Block("Hill", Glyphs(230, 120)), Frame, 1.0, patch: true)!;

        Assert.Null(LostTextRecovery.Resume(cover, box, found, area => Crop(Background(9), area), Frame, 72, patch: true));
        Assert.Null(LostTextRecovery.Resume(cover, box, found, area => Crop(Scene(seed: 10, x: 30, y: 230), area), Frame, 72, patch: true));
    }

    [Fact]
    public void Pewne_dopasowanie_daleko_za_miejscem_potwierdzonym_odczytem_nie_przesuwa_napisu()
    {
        var before = Scene(seed: 12, x: 120, y: 120);
        var box = Glyphs(120, 120);
        var cover = GlyphCoverBuilder.Build(before, box, [box], ["Hill"])! with { Anchor = box };
        var read = Scene(seed: 13, x: 230, y: 120);
        var found = LostTextRecovery.Rebuild(read, Block("Hill", Glyphs(230, 120)), Frame, 1.0, patch: true)!;
        var overshoot = Scene(seed: 14, x: 520, y: 120);

        Assert.Null(LostTextRecovery.Resume(cover, box, found, area => Crop(overshoot, area), Frame, 72, patch: true));
        Assert.NotNull(LostTextRecovery.Resume(cover, box, found, area => Crop(overshoot, area), Frame, 320, patch: true));
    }

    [Fact]
    public void Plan_sledzenia_swiezego_bloku_w_ruchu_nie_szuka_szeroko_a_jadacy_szuka_wokol_predkosci()
    {
        var frame = Scene(seed: 11, x: 120, y: 120);
        var box = Glyphs(120, 120);
        var track = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!.Track!;

        var fresh = GlyphFollow.Plan(track, 72, isMoving: true, fresh: true, 5, 3, moving: true);
        var travelling = GlyphFollow.Plan(track, 72, isMoving: true, fresh: false, 5, -3, moving: true);
        var still = GlyphFollow.Plan(track, 72, isMoving: false, fresh: false, 5, 3, moving: true);

        Assert.Equal((72.0, 0, 0, 0.0, false), (fresh.Radius, fresh.VelocityX, fresh.VelocityY, fresh.WideRadius, fresh.Travelling));
        Assert.True(travelling.Travelling);
        Assert.Equal(216.0, travelling.WideRadius);
        Assert.Equal(216.0 + 5 * track.Step, travelling.Reach, 6);
        Assert.Equal(Math.Max(4, 2 * track.Step), still.Radius);
        Assert.Equal((0, 0, false), (still.VelocityX, still.VelocityY, still.Travelling));
    }

    private static TextBlock Block(string text, RectPx box) => new(text, box, [Line(text, box)]);

    private static OcrLine Line(string text, RectPx box) => new(text, box, [new OcrWord(text, box)]);

    private static RectPx Glyphs(int x, int y) => new(x - 4, y - 4, 210, 50);

    private static OcrBitmap? Crop(OcrBitmap frame, RectPx area)
    {
        area = area.Intersect(new RectPx(0, 0, frame.Width, frame.Height));
        return area.IsEmpty ? null : OcrBands.Crop(frame, area);
    }

    private static OcrBitmap Scene(int seed, int x, int y)
    {
        var frame = Background(seed);
        var glyphs = Word(x, y, capHeight: 40, stroke: 6).ToList();
        Paint(frame, glyphs, 0x000000, grow: 3);
        Paint(frame, glyphs, 0xF0F0F0);
        return frame;
    }

    private static OcrBitmap Background(int seed)
    {
        var random = new Random(seed);
        var frame = new OcrBitmap(new byte[Width * Height * 4], Width, Height, Width * 4);
        var baseR = random.Next(40, 160);
        var baseG = random.Next(40, 160);
        var baseB = random.Next(40, 160);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var wave = (int)(30 * Math.Sin((x + seed * 37) / 23.0) + 20 * Math.Cos((y + seed * 11) / 17.0));
                Set(frame, x, y, Clamp(baseR + wave) << 16 | Clamp(baseG - wave / 2) << 8 | Clamp(baseB + wave / 3));
            }
        }
        return frame;
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

    private static IEnumerable<(int X, int Y)> Word(int x, int top, int capHeight, int stroke, int letters = 4)
    {
        var points = new HashSet<(int, int)>();
        var advance = capHeight * 3 / 4 + stroke * 2;
        for (var letter = 0; letter < letters; letter++)
        {
            var left = x + letter * advance;
            var right = left + capHeight * 3 / 4;
            for (var y = top; y < top + capHeight; y++)
            {
                for (var dx = 0; dx < stroke; dx++)
                {
                    points.Add((left + dx, y));
                    if (letter % 2 == 0) points.Add((right - dx, y));
                }
            }
            var mid = top + capHeight / 2 + letter * 3;
            for (var bx = left; bx <= right; bx++)
                for (var dy = 0; dy < stroke; dy++) points.Add((bx, mid + dy));
        }
        return points;
    }

    private static void Paint(OcrBitmap frame, IEnumerable<(int X, int Y)> points, int rgb, int grow = 0)
    {
        foreach (var (x, y) in points)
        {
            for (var oy = -grow; oy <= grow; oy++)
                for (var ox = -grow; ox <= grow; ox++)
                    Set(frame, x + ox, y + oy, rgb);
        }
    }

    private static OcrBitmap Solid(int width, int height, int rgb)
    {
        var frame = new OcrBitmap(new byte[width * height * 4], width, height, width * 4);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) Set(frame, x, y, rgb);
        return frame;
    }

    private static void Set(OcrBitmap frame, int x, int y, int rgb)
    {
        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height) return;
        var o = y * frame.Stride + x * 4;
        frame.PixelsBgra32[o] = (byte)(rgb & 0xFF);
        frame.PixelsBgra32[o + 1] = (byte)((rgb >> 8) & 0xFF);
        frame.PixelsBgra32[o + 2] = (byte)((rgb >> 16) & 0xFF);
        frame.PixelsBgra32[o + 3] = 255;
    }
}
