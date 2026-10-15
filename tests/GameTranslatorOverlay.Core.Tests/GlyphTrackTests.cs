using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class GlyphTrackTests
{
    private const int Width = 520;
    private const int Height = 220;

    [Fact]
    public void Napis_w_miejscu_na_nowym_tle_jest_znaleziony_bez_przesuniecia()
    {
        var first = Scene(seed: 1, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        Assert.NotNull(cover.Track);

        var second = Scene(seed: 7, x: 60, y: 70);
        var match = GlyphTracker.Locate(cover.Track, second, 0, 0, 40);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident, $"koszt {match.Value.Cost:F1}");
        Assert.True(match.Value.IsStatic);
    }

    [Fact]
    public void Przesuniety_napis_jest_znaleziony_z_dokladnym_przesunieciem()
    {
        var first = Scene(seed: 2, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;

        var moved = Scene(seed: 3, x: 83, y: 63);
        var match = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 48);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident, $"koszt {match.Value.Cost:F1}, typowy {match.Value.Typical:F1}");
        Assert.Equal(23, match.Value.WorkDx);
        Assert.Equal(-7, match.Value.WorkDy);
    }

    [Fact]
    public void Daleko_przesuniety_napis_jest_znaleziony_szerokim_wyszukiwaniem_zgrubnym()
    {
        var first = Scene(seed: 2, x: 40, y: 70);
        var box = new RectPx(36, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        var step = GlyphTracker.WideStep(cover.Track!, 180);
        Assert.True(step >= 2, $"krok {step}, kreska {cover.Track!.Stroke:F1}");

        var moved = Scene(seed: 3, x: 197, y: 61);
        var narrow = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 72);
        var wide = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 180, 0, 0, step);

        Assert.True(narrow is not { IsConfident: true });
        Assert.NotNull(wide);
        Assert.True(wide.Value.IsConfident, $"koszt {wide.Value.Cost:F1}, typowy {wide.Value.Typical:F1}");
        Assert.Equal(157, wide.Value.WorkDx);
        Assert.Equal(-9, wide.Value.WorkDy);
    }

    [Fact]
    public void Odswiezony_wzorzec_pasuje_do_nowej_klatki_lepiej_niz_stary()
    {
        var first = Scene(seed: 2, x: 60, y: 70, background: 0x203040);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        var changed = Scene(seed: 2, x: 72, y: 66, background: 0x6A5A30);
        var match = GlyphTracker.Locate(cover.Track!, changed, 0, 0, 40)!.Value;
        var moved = cover.Track!.Shifted(match.WorkDx, match.WorkDy);

        var resampled = GlyphTracker.Resample(moved, changed, 0, 0);
        var before = GlyphTracker.Locate(moved, changed, 0, 0, 0)!.Value.Cost;
        var after = GlyphTracker.Locate(resampled, changed, 0, 0, 0)!.Value.Cost;

        Assert.Equal(moved.Points, resampled.Points);
        Assert.True(after < 0.5, $"po {after:F2}");
        Assert.True(after <= before, $"przed {before:F2}, po {after:F2}");
    }

    [Fact]
    public void Brak_napisu_nie_daje_pewnego_dopasowania()
    {
        var first = Scene(seed: 4, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;

        var empty = Background(seed: 5);
        var match = GlyphTracker.Locate(cover.Track!, empty, 0, 0, 48);

        Assert.True(match is null || !match.Value.IsConfident);
    }

    [Fact]
    public void Odswiezona_latka_bierze_kolory_z_nowego_tla_i_przesuwa_kotwice()
    {
        var first = Scene(seed: 6, x: 60, y: 70, background: 0x203040);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])! with { Anchor = box };

        var moved = Scene(seed: 6, x: 70, y: 74, background: 0x804020);
        var match = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 40)!.Value;
        var refreshed = GlyphCoverBuilder.Refill(cover, moved, 0, 0, match.WorkDx, match.WorkDy)!;

        Assert.Equal(box.Offset(10, 4), refreshed.Anchor);
        Assert.Equal(cover.PatchPbgra.Length, refreshed.PatchPbgra.Length);
        var opaque = 0;
        for (var o = 0; o < refreshed.PatchPbgra.Length; o += 4)
        {
            if (refreshed.PatchPbgra[o + 3] != 255) continue;
            opaque++;
            Assert.InRange(refreshed.PatchPbgra[o + 2], 0x80 - 20, 0x80 + 20);
            Assert.InRange(refreshed.PatchPbgra[o], 0x20 - 20, 0x20 + 20);
        }
        Assert.True(opaque > 200);
    }

    [Fact]
    public void Duzy_napis_budowany_w_zmniejszeniu_jest_sledzony_w_pikselach_okna()
    {
        const int width = 900;
        const int height = 360;
        var first = Solid(width, height, 0x101820);
        var glyphs = Word(80, 100, capHeight: 110, stroke: 16);
        Paint(first, glyphs, 0xE8E8E8);
        var box = new RectPx(70, 90, 560, 130);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        Assert.True(cover.Track!.Step >= 2);

        var moved = Solid(width, height, 0x101820);
        Paint(moved, Word(80 + 30, 100 - 12, capHeight: 110, stroke: 16), 0xE8E8E8);
        var match = GlyphTracker.Locate(cover.Track, moved, 0, 0, 60);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident);
        Assert.InRange(match.Value.WindowDx, 28, 32);
        Assert.InRange(match.Value.WindowDy, -14, -10);
    }

    [Fact]
    public void Mapa_okna_przesuwa_poczatek_sledzenia()
    {
        var first = Scene(seed: 8, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"], map: new GlyphTrackMap(1000, 500, 1))!;
        var region = Scene(seed: 9, x: 60, y: 70);

        var match = GlyphTracker.Locate(cover.Track!, region, 1000, 500, 30);

        Assert.True(match!.Value.IsConfident);
        Assert.True(match.Value.IsStatic);
        Assert.True(cover.Track!.OriginX >= 1000);
    }

    [Fact]
    public void Ikona_i_slowo_jako_osobne_linie_ocr_daja_jeden_wiersz_i_wykryta_ikone()
    {
        var frame = Solid(Width, Height, 0x202830);
        Paint(frame, Word(30, 70, capHeight: 40, stroke: 6, letters: 1), 0xF0F0F0);
        Paint(frame, Word(110, 70, capHeight: 40, stroke: 6, letters: 4), 0xF0F0F0);
        var icon = new RectPx(26, 66, 40, 48);
        var word = new RectPx(106, 66, 200, 48);
        var block = new GameTranslatorOverlay.Core.Text.TextBlock("X Hint", icon.Union(word),
        [
            new OcrLine("Hint", word.Offset(0, -2), [new OcrWord("Hint", word.Offset(0, -2))]),
            new OcrLine("X", icon, [new OcrWord("X", icon)]),
        ]);

        var cover = GlyphCoverBuilder.BuildForBlock(frame, block, new RectPx(0, 0, Width, Height), 1.0);

        Assert.NotNull(cover);
        Assert.Single(cover.Lines);
        Assert.Equal("X", cover.IconToken);
        Assert.True(cover.IconSkipPx > 0);
    }

    [Fact]
    public void Ciemny_napis_bez_obrysu_nie_pasuje_do_jednolitej_czerni()
    {
        var paper = Solid(Width, Height, 0xD8D0C0);
        Paint(paper, Word(60, 70, capHeight: 40, stroke: 6), 0x202020);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(paper, box, [box], ["Hill"])!;
        Assert.True(Math.Abs(cover.Track!.TemplateContrast) > 60);

        var black = Solid(Width, Height, 0x0A0A0A);
        var match = GlyphTracker.Locate(cover.Track, black, 0, 0, 72);

        Assert.True(match is null || !match.Value.IsConfident, $"koszt {match?.Cost:F1}, kontrast {match?.LocalContrast:F1}");
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(-5, 1)]
    [InlineData(7, -3)]
    public void Cienki_maly_napis_jest_znaleziony_z_nieparzystym_przesunieciem(int dx, int dy)
    {
        var first = Solid(Width, Height, 0x283038);
        Paint(first, Word(60, 80, capHeight: 16, stroke: 2), 0x000000, grow: 1);
        Paint(first, Word(60, 80, capHeight: 16, stroke: 2), 0xF0F0F0);
        var box = new RectPx(56, 76, 90, 24);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;

        var moved = Solid(Width, Height, 0x283038);
        Paint(moved, Word(60 + dx, 80 + dy, capHeight: 16, stroke: 2), 0x000000, grow: 1);
        Paint(moved, Word(60 + dx, 80 + dy, capHeight: 16, stroke: 2), 0xF0F0F0);
        var match = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 72);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident, $"koszt {match.Value.Cost:F1}");
        Assert.Equal(dx, match.Value.WorkDx);
        Assert.Equal(dy, match.Value.WorkDy);
    }

    [Fact]
    public void Napis_bez_obrysu_na_teksturze_zostaje_po_zmianie_tekstury()
    {
        var first = Background(seed: 11);
        Paint(first, Word(60, 70, capHeight: 40, stroke: 6), 0xF4F4F4);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;

        var second = Background(seed: 12);
        Paint(second, Word(60, 70, capHeight: 40, stroke: 6), 0xF4F4F4);
        var match = GlyphTracker.Locate(cover.Track!, second, 0, 0, 8);

        Assert.True(match!.Value.IsConfident, $"koszt {match.Value.Cost:F1}, kontrast {match.Value.LocalContrast:F1}/{match.Value.TemplateContrast:F1}");
        Assert.True(match.Value.IsStatic);
    }

    [Fact]
    public void Kotwica_nie_dryfuje_przy_ulamkowym_kroku()
    {
        var frame = Scene(seed: 13, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"], map: new GlyphTrackMap(0, 0, 0.5))! with { Anchor = box };

        var current = cover;
        for (var i = 0; i < 12; i++)
            current = GlyphCoverBuilder.Refill(current, frame, 0, 0, 1, 0) ?? current;

        Assert.Equal(box.X + 6, current.Anchor.X);
    }

    [Fact]
    public void Przygaszony_albo_odwrocony_napis_to_nie_brak_liter_a_jednolite_pole_tak()
    {
        var frame = Solid(Width, Height, 0x282828);
        var glyphs = Word(60, 70, capHeight: 40, stroke: 6).ToList();
        Paint(frame, glyphs, 0xF0F0F0);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var dim = Solid(Width, Height, 0x282828);
        Paint(dim, glyphs, 0x7A7A7A);
        var inverted = Solid(Width, Height, 0xFFDC00);
        Paint(inverted, glyphs, 0x000000);
        var empty = Solid(Width, Height, 0x282828);

        Assert.False(GlyphTracker.Locate(cover.Track!, dim, 0, 0, 0)!.Value.LettersGone);
        Assert.False(GlyphTracker.Locate(cover.Track!, inverted, 0, 0, 0)!.Value.LettersGone);
        Assert.True(GlyphTracker.Locate(cover.Track!, empty, 0, 0, 0)!.Value.LettersGone);
    }

    [Theory]
    [InlineData(1.08, 14, -6)]
    [InlineData(1.12, -20, 5)]
    [InlineData(1.2, 9, 8)]
    [InlineData(0.9, -12, -4)]
    [InlineData(0.84, 22, 3)]
    public void Przeskalowany_i_przesuniety_napis_jest_znaleziony_z_wlasciwa_skala(double scale, int dx, int dy)
    {
        var glyphs = Word(150, 80, capHeight: 40, stroke: 6).ToList();
        var first = Background(seed: 21);
        Paint(first, Grow(glyphs, 3), 0x000000);
        Paint(first, glyphs, 0xF0F0F0);
        var box = new RectPx(146, 76, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        var track = cover.Track!;

        var moved = Background(seed: 22);
        Paint(moved, Transform(Grow(glyphs, 3), track.CenterX, track.CenterY, scale, dx, dy), 0x000000);
        Paint(moved, Transform(glyphs, track.CenterX, track.CenterY, scale, dx, dy), 0xF0F0F0);
        var field = new GlyphField(moved, 0, 0);
        var match = GlyphTracker.LocateScaled(track, field, 48, GlyphTracker.ScaleLadder(track, 0.82, 1.22));

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident, $"koszt {match.Value.Cost:F1}/{match.Value.AcceptLimit:F1}");
        Assert.InRange(match.Value.Scale, scale - 0.025, scale + 0.025);
        Assert.InRange(match.Value.WindowDx, dx - 3, dx + 3);
        Assert.InRange(match.Value.WindowDy, dy - 3, dy + 3);
        if (Math.Abs(scale - 1) >= 0.12)
            Assert.True(GlyphTracker.Locate(track, field, 48) is not { IsConfident: true }, "bez skali nie powinno pasować");
    }

    [Fact]
    public void Przeskalowana_okladka_przesuwa_i_powieksza_ramke_linie_i_latke_spojnie()
    {
        const double scale = 1.15;
        var glyphs = Word(150, 80, capHeight: 40, stroke: 6).ToList();
        var first = Background(seed: 23);
        Paint(first, Grow(glyphs, 3), 0x000000);
        Paint(first, glyphs, 0xF0F0F0);
        var box = new RectPx(146, 76, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])! with { Anchor = box };
        var track = cover.Track!;
        var moved = Background(seed: 24);
        Paint(moved, Transform(Grow(glyphs, 3), track.CenterX, track.CenterY, scale, 18, -7), 0x000000);
        Paint(moved, Transform(glyphs, track.CenterX, track.CenterY, scale, 18, -7), 0xF0F0F0);
        var match = GlyphTracker.LocateScaled(track, new GlyphField(moved, 0, 0), 48, GlyphTracker.ScaleLadder(track, 0.84, 1.2))!.Value;

        var refilled = GlyphCoverBuilder.Refill(cover.Rescaled(match.Scale), moved, 0, 0, match.WorkDx, match.WorkDy)!;

        double Expected(double value, double center, int shift) => center + (value - center) * scale + shift;
        Assert.InRange(refilled.Anchor.Width, box.Width * scale - 6, box.Width * scale + 6);
        Assert.InRange(refilled.Anchor.Height, box.Height * scale - 3, box.Height * scale + 3);
        Assert.InRange(refilled.Anchor.X, Expected(box.X, track.CenterX, 18) - 3, Expected(box.X, track.CenterX, 18) + 3);
        Assert.InRange(refilled.Anchor.Y, Expected(box.Y, track.CenterY, -7) - 3, Expected(box.Y, track.CenterY, -7) + 3);
        Assert.InRange(refilled.Ascent, cover.Ascent * scale - 2, cover.Ascent * scale + 2);
        Assert.InRange(refilled.PatchWidth, cover.PatchWidth * scale - 1, cover.PatchWidth * scale + 1);
        Assert.Equal(cover.PatchPixelWidth, refilled.PatchPixelWidth);
        Assert.Equal(cover.PatchPbgra.Length, refilled.PatchPbgra.Length);
        var inkLeft = refilled.Anchor.X + refilled.Lines[0].InkLeft;
        var expectedInk = Expected(box.X + cover.Lines[0].InkLeft, track.CenterX, 18);
        Assert.InRange(inkLeft, expectedInk - 3, expectedInk + 3);
        var again = GlyphTracker.Locate(refilled.Track!, moved, 0, 0, 4);
        Assert.True(again is { IsConfident: true, IsStatic: true }, $"koszt {again?.Cost:F1}");
    }

    [Fact]
    public void Skalowanie_okladki_jest_odwracalne_i_trzyma_srodek_wzorca()
    {
        var frame = Scene(seed: 25, x: 150, y: 80);
        var box = new RectPx(146, 76, 210, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])! with { Anchor = box };
        var track = cover.Track!;

        var grown = cover.Rescaled(1.25);
        var back = grown.Rescaled(1 / 1.25);

        Assert.Equal(track.CenterX, grown.Track!.CenterX, 6);
        Assert.Equal(track.CenterY, grown.Track.CenterY, 6);
        Assert.Equal(track.Step * 1.25, grown.Track.Step, 9);
        Assert.Equal(box, back.Anchor);
        Assert.Equal(cover.Lines[0].InkLeft + cover.Anchor.X, back.Lines[0].InkLeft + back.Anchor.X, 6);
        Assert.Equal(cover.Lines[0].Baseline + cover.Anchor.Y, back.Lines[0].Baseline + back.Anchor.Y, 6);
        Assert.Equal(cover.PatchX + cover.Anchor.X, back.PatchX + back.Anchor.X, 6);
        Assert.Same(cover, cover.Rescaled(1));
    }

    [Fact]
    public void Wiele_malych_krokow_skali_powieksza_ramke_jak_jeden_duzy()
    {
        var frame = Scene(seed: 25, x: 150, y: 80);
        var box = new RectPx(146, 76, 210, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])! with { Anchor = box };

        var stepped = cover;
        for (var i = 0; i < 50; i++)
            stepped = GlyphCoverBuilder.Shift(stepped.Rescaled(1.006), i % 2 == 0 ? 1 : 0, 0)!;
        var once = GlyphCoverBuilder.Shift(cover.Rescaled(Math.Pow(1.006, 50)), 25, 0)!;

        Assert.InRange(stepped.Anchor.Height, once.Anchor.Height - 1, once.Anchor.Height + 1);
        Assert.InRange(stepped.Anchor.Width, once.Anchor.Width - 1, once.Anchor.Width + 1);
        Assert.InRange(stepped.Anchor.Y, once.Anchor.Y - 1, once.Anchor.Y + 1);
        Assert.True(stepped.Anchor.Height > box.Height + 10, $"wysokość {stepped.Anchor.Height}");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.1)]
    [InlineData(0.88)]
    public void Inny_napis_w_tym_miejscu_nie_jest_znaleziony_w_zadnej_skali(double scale)
    {
        var glyphs = Word(150, 80, capHeight: 40, stroke: 6).ToList();
        var first = Background(seed: 26);
        Paint(first, Grow(glyphs, 3), 0x000000);
        Paint(first, glyphs, 0xF0F0F0);
        var box = new RectPx(146, 76, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        var track = cover.Track!;

        var other = Bars(150, 80, capHeight: 40, stroke: 6).ToList();
        var replaced = Background(seed: 27);
        Paint(replaced, Transform(Grow(other, 3), track.CenterX, track.CenterY, scale, 4, 2), 0x000000);
        Paint(replaced, Transform(other, track.CenterX, track.CenterY, scale, 4, 2), 0xF0F0F0);
        var match = GlyphTracker.LocateScaled(track, new GlyphField(replaced, 0, 0), 48, GlyphTracker.ScaleLadder(track, 0.82, 1.22));
        var empty = GlyphTracker.LocateScaled(track, new GlyphField(Background(seed: 28), 0, 0), 48, GlyphTracker.ScaleLadder(track, 0.82, 1.22));

        Assert.True(match is not { IsConfident: true }, $"skala {match?.Scale:F2}, koszt {match?.Cost:F1}");
        Assert.True(empty is not { IsConfident: true });
    }

    [Fact]
    public void Doprecyzowanie_skali_dogania_powoli_rosnacy_napis()
    {
        const double scale = 1.025;
        var glyphs = Word(150, 70, capHeight: 48, stroke: 14).ToList();
        var first = Solid(Width, Height, 0x283038);
        Paint(first, Grow(glyphs, 2), 0x000000);
        Paint(first, glyphs, 0xF0F0F0);
        var box = new RectPx(144, 64, 262, 62);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        var track = cover.Track!;
        var moved = Solid(Width, Height, 0x283038);
        Paint(moved, Transform(Grow(glyphs, 2), track.CenterX, track.CenterY, scale, 6, 0), 0x000000);
        Paint(moved, Transform(glyphs, track.CenterX, track.CenterY, scale, 6, 0), 0xF0F0F0);
        var field = new GlyphField(moved, 0, 0);
        var located = GlyphTracker.Locate(track, field, 24);
        Assert.NotNull(located);
        Assert.True(located.Value.IsConfident, $"koszt {located.Value.Cost:F1}");
        var plain = located.Value;

        var refined = GlyphTracker.RefineScale(track, field, plain);

        Assert.InRange(refined.Scale, scale - 0.02, scale + 0.02);
        Assert.True(refined.Cost < plain.Cost * 0.85, $"przed {plain.Cost:F1}, po {refined.Cost:F1}");
        Assert.InRange(refined.WindowDx, 3, 9);
    }

    [Fact]
    public void Doprecyzowanie_nie_rusza_skali_napisu_ktory_tylko_jedzie()
    {
        var first = Scene(seed: 29, x: 150, y: 80);
        var box = new RectPx(146, 76, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        var moved = Scene(seed: 30, x: 171, y: 74);
        var field = new GlyphField(moved, 0, 0);
        var plain = GlyphTracker.Locate(cover.Track!, field, 48)!.Value;

        var refined = GlyphTracker.RefineScale(cover.Track!, field, plain);

        Assert.Equal(1.0, refined.Scale);
        Assert.Equal(21, refined.WorkDx);
        Assert.Equal(-6, refined.WorkDy);
    }

    [Fact]
    public void Bardzo_duzy_napis_buduje_okladke_ze_sledzeniem()
    {
        const int width = 1800;
        const int height = 900;
        var frame = Solid(width, height, 0x8A8070);
        var glyphs = Word(300, 250, capHeight: 340, stroke: 48, letters: 3).ToList();
        Paint(frame, glyphs, 0x181818);
        var box = new RectPx(292, 244, 3 * (340 * 3 / 4 + 96), 352);

        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Esc"]);

        Assert.NotNull(cover);
        Assert.NotNull(cover.Track);
        Assert.True(cover.Track.Step >= 3);
        var match = GlyphTracker.Locate(cover.Track, frame, 0, 0, 30);
        Assert.True(match is { IsConfident: true, IsStatic: true }, $"koszt {match?.Cost:F1}");
    }

    private static IEnumerable<(int X, int Y)> Bars(int x, int top, int capHeight, int stroke)
    {
        var points = new HashSet<(int, int)>();
        for (var bar = 0; bar < 5; bar++)
        {
            var y0 = top + bar * capHeight / 5;
            for (var y = y0; y < y0 + stroke; y++)
                for (var bx = x + (bar % 2) * 30; bx < x + 150 + (bar % 2) * 20; bx++) points.Add((bx, y));
        }
        return points;
    }

    private static List<(int X, int Y)> Grow(IEnumerable<(int X, int Y)> points, int grow)
    {
        var grown = new HashSet<(int, int)>();
        foreach (var (x, y) in points)
            for (var oy = -grow; oy <= grow; oy++)
                for (var ox = -grow; ox <= grow; ox++) grown.Add((x + ox, y + oy));
        return grown.ToList();
    }

    private static List<(int X, int Y)> Transform(IEnumerable<(int X, int Y)> points, double centerX, double centerY, double scale, int dx, int dy)
    {
        var source = points.ToHashSet();
        var minX = source.Min(static p => p.X);
        var maxX = source.Max(static p => p.X);
        var minY = source.Min(static p => p.Y);
        var maxY = source.Max(static p => p.Y);
        var left = (int)Math.Floor(centerX + (minX - centerX) * scale + dx) - 2;
        var right = (int)Math.Ceiling(centerX + (maxX + 1 - centerX) * scale + dx) + 2;
        var top = (int)Math.Floor(centerY + (minY - centerY) * scale + dy) - 2;
        var bottom = (int)Math.Ceiling(centerY + (maxY + 1 - centerY) * scale + dy) + 2;
        var result = new List<(int X, int Y)>();
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var sx = (int)Math.Floor(centerX + (x + 0.5 - dx - centerX) / scale);
                var sy = (int)Math.Floor(centerY + (y + 0.5 - dy - centerY) / scale);
                if (source.Contains((sx, sy))) result.Add((x, y));
            }
        }
        return result;
    }

    private static OcrBitmap Scene(int seed, int x, int y, int background = -1)
    {
        var frame = background >= 0 ? Solid(Width, Height, background) : Background(seed);
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
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return;
        var o = y * frame.Stride + x * 4;
        frame.PixelsBgra32[o] = (byte)rgb;
        frame.PixelsBgra32[o + 1] = (byte)(rgb >> 8);
        frame.PixelsBgra32[o + 2] = (byte)(rgb >> 16);
        frame.PixelsBgra32[o + 3] = 255;
    }
}
