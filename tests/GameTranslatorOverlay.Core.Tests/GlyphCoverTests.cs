using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class GlyphCoverTests
{
    private const int Width = 420;
    private const int Height = 160;

    [Fact]
    public void Napis_z_ikony_klawisza_jest_nizszy_od_slow_obok_a_zwykle_slowo_nie()
    {
        OcrWord W(string text, int x, int height) => new(text, new RectPx(x, 100 - height, text.Length * 20, height));

        IReadOnlyList<OcrWord> hud = [W("Tab", 0, 18), W("Items", 80, 52)];
        IReadOnlyList<OcrWord> sentence = [W("Hold", 0, 50), W("Shift", 100, 50), W("to", 220, 38), W("sprint", 280, 62)];
        IReadOnlyList<OcrWord> alone = [W("Tab", 0, 30)];

        Assert.True(GlyphCoverBuilder.IsIconLabel(hud[0], hud));
        Assert.False(GlyphCoverBuilder.IsIconLabel(sentence[1], sentence));
        Assert.True(GlyphCoverBuilder.IsIconLabel(alone[0], alone));
        Assert.False(GlyphCoverBuilder.IsIconLabel(hud[1], hud));
    }

    [Fact]
    public void Jasny_napis_z_konturem_i_cieniem_daje_kolor_kontur_i_cien()
    {
        var frame = Solid(Width, Height, 0x1A111B);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x000000, grow: 4, dx: 4, dy: 4);
        Paint(frame, glyphs, 0x000000, grow: 4);
        Paint(frame, glyphs, 0xA6A6A6);

        var cover = GlyphCoverBuilder.Build(frame, new RectPx(36, 46, 200, 50), [new RectPx(36, 46, 200, 50)], ["Hill"]);

        Assert.NotNull(cover);
        Assert.False(cover.Soft);
        AssertColor(0xA6A6A6, cover.TextRgb, 12);
        AssertColor(0x000000, cover.OutlineRgb, 12);
        Assert.InRange(cover.OutlinePx, 2, 6);
        Assert.True(cover.ShadowDy > 0);
        Assert.True(cover.ShadowDx > 0);
    }

    [Fact]
    public void Latka_wypelnia_litery_kolorem_tla_a_poza_maska_jest_przezroczysta()
    {
        var frame = Solid(Width, Height, 0x2B3A4C);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xF5F5F5);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var patchOrigin = (X: box.X + (int)cover.PatchX, Y: box.Y + (int)cover.PatchY);
        var covered = 0;
        foreach (var (x, y) in glyphs)
        {
            var px = x - patchOrigin.X;
            var py = y - patchOrigin.Y;
            var o = (py * cover.PatchPixelWidth + px) * 4;
            Assert.Equal(255, cover.PatchPbgra[o + 3]);
            Assert.InRange(cover.PatchPbgra[o + 2], 0x2B - 6, 0x2B + 6);
            Assert.InRange(cover.PatchPbgra[o + 1], 0x3A - 6, 0x3A + 6);
            Assert.InRange(cover.PatchPbgra[o], 0x4C - 6, 0x4C + 6);
            covered++;
        }
        Assert.True(covered > 200);
        Assert.Equal(0, cover.PatchPbgra[3]);
        Assert.Equal(-1, cover.OutlineRgb);
    }

    [Fact]
    public void Gradient_tla_jest_odtworzony_pod_literami()
    {
        var frame = new OcrBitmap(new byte[Width * Height * 4], Width, Height, Width * 4);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                Set(frame, x, y, (x / 3) << 16 | 0x30 << 8 | (y / 2));
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xFFFFFF);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var worst = 0;
        foreach (var (x, y) in glyphs)
        {
            var o = ((y - box.Y - (int)cover.PatchY) * cover.PatchPixelWidth + (x - box.X - (int)cover.PatchX)) * 4;
            worst = Math.Max(worst, Math.Abs(cover.PatchPbgra[o + 2] - x / 3));
            worst = Math.Max(worst, Math.Abs(cover.PatchPbgra[o] - y / 2));
        }
        Assert.InRange(worst, 0, 8);
    }

    [Fact]
    public void Ciemny_tekst_na_jasnym_oknie_ma_ciemny_kolor()
    {
        var frame = Solid(Width, Height, 0xEDE6D6);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x202020);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"]);

        Assert.NotNull(cover);
        AssertColor(0x202020, cover.TextRgb, 12);
    }

    [Fact]
    public void Linia_bazowa_i_wysokosc_wersalikow_sa_mierzone_z_pikseli()
    {
        var frame = Solid(Width, Height, 0x101010);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xE0E0E0);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var line = Assert.Single(cover.Lines);
        Assert.InRange(box.Y + line.InkTop, 49, 51);
        Assert.InRange(box.Y + line.Baseline, 89, 91);
        Assert.InRange(cover.Ascent, 38, 42);
        Assert.InRange(box.X + line.InkLeft, 39, 41);
        Assert.Equal("Hill", line.Text);
        Assert.InRange(line.Density, 0.05, 1);
    }

    [Fact]
    public void Wyrownanie_do_srodka_rozpoznane_z_dwoch_linii()
    {
        var frame = Solid(Width, 200, 0x101010);
        Paint(frame, Word(60, 30, capHeight: 30, stroke: 5, letters: 6), 0xE0E0E0);
        Paint(frame, Word(110, 90, capHeight: 30, stroke: 5, letters: 2), 0xE0E0E0);

        var lines = new[] { new RectPx(56, 26, 230, 40), new RectPx(106, 86, 130, 40) };
        var cover = GlyphCoverBuilder.Build(frame, new RectPx(56, 26, 230, 100), lines, ["Hello there", "Hi"]);

        Assert.NotNull(cover);
        Assert.Equal(TextAlignHint.Center, cover.Align);
        Assert.Equal(2, cover.Lines.Count);
        Assert.InRange(cover.LinePitch, 58, 62);
    }

    [Fact]
    public void Ikona_klawisza_przed_tekstem_nie_jest_zamazywana()
    {
        var frame = Solid(Width, Height, 0x0A0A0A);
        var key = Rect(40, 50, 40, 40);
        Paint(frame, key, 0x968CA6);
        var glyphs = Word(120, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xFAFAFA);

        var box = new RectPx(40, 50, 260, 42);
        var words = new[] { new OcrWord("X", new RectPx(40, 50, 40, 40)), new OcrWord("Hint", new RectPx(120, 50, 170, 40)) };
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["X Hint"], firstLineWords: words)!;

        Assert.Equal("X", cover.IconToken);
        Assert.True(cover.IconSkipPx > 40);
        foreach (var (x, y) in key)
        {
            var o = ((y - box.Y - (int)cover.PatchY) * cover.PatchPixelWidth + (x - box.X - (int)cover.PatchX)) * 4;
            Assert.Equal(0, cover.PatchPbgra[o + 3]);
        }
    }

    [Fact]
    public void Wysoka_ikona_za_tekstem_nie_jest_zamazywana()
    {
        var frame = Solid(Width, Height, 0x2A2A30);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xA5A5A5);
        var icon = Rect(260, 47, 40, 46);
        Paint(frame, icon, 0xFFF080);

        var box = new RectPx(40, 47, 262, 48);
        var words = new[] { new OcrWord("Hill", new RectPx(40, 50, 205, 40)), new OcrWord("to", new RectPx(260, 47, 40, 46)) };
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill to"], firstLineWords: words)!;

        Assert.Equal("to", cover.TailToken);
        Assert.True(cover.TailSkipPx >= 40);
        AssertColor(0xA5A5A5, cover.TextRgb, 12);
        foreach (var (x, y) in icon)
        {
            var o = ((y - box.Y - (int)cover.PatchY) * cover.PatchPixelWidth + (x - box.X - (int)cover.PatchX)) * 4;
            Assert.Equal(0, cover.PatchPbgra[o + 3]);
        }
    }

    [Fact]
    public void Zwykle_krotkie_slowo_na_koncu_nie_jest_ikona()
    {
        var frame = Solid(Width, Height, 0x2A2A30);
        Paint(frame, Word(40, 50, capHeight: 40, stroke: 6), 0xA5A5A5);
        Paint(frame, Word(258, 50, capHeight: 40, stroke: 6, letters: 1), 0xA5A5A5);

        var box = new RectPx(40, 50, 260, 40);
        var words = new[] { new OcrWord("Hill", new RectPx(40, 50, 205, 40)), new OcrWord("A", new RectPx(258, 50, 30, 40)) };
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill A"], firstLineWords: words)!;

        Assert.Equal(string.Empty, cover.TailToken);
        Assert.Equal(0, cover.TailSkipPx);
    }

    [Fact]
    public void Duzy_napis_liczony_w_polowie_rozdzielczosci_ma_wspolrzedne_natywne()
    {
        var frame = Solid(900, 300, 0x1A1A2A);
        var glyphs = Word(60, 80, capHeight: 100, stroke: 14);
        Paint(frame, glyphs, 0xF0F0F0);

        var box = new RectPx(52, 72, 520, 116);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        Assert.True(cover.PatchPixelWidth < cover.PatchWidth * 0.6);
        Assert.InRange(cover.Ascent, 96, 104);
        Assert.InRange(box.Y + cover.Lines[0].Baseline, 178, 182);
    }

    [Fact]
    public void Brak_tekstu_nie_daje_latki()
    {
        var frame = Solid(Width, Height, 0x334455);
        Assert.Null(GlyphCoverBuilder.Build(frame, new RectPx(36, 46, 200, 50)));
    }

    [Fact]
    public void Podpis_pola_odroznia_zmiane_tla_od_szumu()
    {
        var frame = Solid(Width, Height, 0x334455);
        var region = new RectPx(30, 40, 220, 70);
        var first = GlyphCoverBuilder.Signature(frame, region);
        Assert.Equal(0, GlyphCover.SignatureDifference(first, GlyphCoverBuilder.Signature(frame, region)));

        var changed = Solid(Width, Height, 0x8899AA);
        Assert.True(GlyphCover.SignatureDifference(first, GlyphCoverBuilder.Signature(changed, region)) > GlyphCoverBuilder.StaticSignatureTolerance);
        Assert.Equal(double.PositiveInfinity, GlyphCover.SignatureDifference(first, []));
    }

    [Fact]
    public void Kotwica_przesuwa_wspolrzedne_wzgledem_nowego_pola()
    {
        var frame = Solid(Width, Height, 0x101010);
        Paint(frame, Word(40, 50, capHeight: 40, stroke: 6), 0xE0E0E0);
        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])! with { Anchor = box };

        var moved = cover.AnchorTo(new RectPx(34, 47, 200, 50));

        Assert.Equal(cover.PatchX + 2, moved.PatchX);
        Assert.Equal(cover.PatchY - 1, moved.PatchY);
        Assert.Equal(cover.Lines[0].Baseline - 1, moved.Lines[0].Baseline);
        Assert.Equal(new RectPx(34, 47, 200, 50), moved.Anchor);
    }

    [Fact]
    public void Profil_tuszu_pomija_kropke_i_ogonek_przy_linii_bazowej()
    {
        const int w = 60;
        const int h = 60;
        var mask = new bool[w * h];
        for (var y = 10; y < 40; y++)
            for (var x = 5; x < 11; x++) mask[y * w + x] = true;
        for (var y = 20; y < 40; y++)
            for (var x = 15; x < 45; x++) mask[y * w + x] = true;
        for (var y = 40; y < 50; y++)
            for (var x = 40; x < 44; x++) mask[y * w + x] = true;
        for (var y = 2; y < 6; y++)
            for (var x = 50; x < 54; x++) mask[y * w + x] = true;

        var profile = InkProfile.Measure(mask, w, new RectPx(0, 0, w, h))!;

        Assert.Equal(10, profile.Top);
        Assert.Equal(40, profile.Baseline);
        Assert.Equal(50, profile.Bottom);
        Assert.Equal(5, profile.Left);
        Assert.Equal(54, profile.Right);
    }

    [Fact]
    public void Cienka_odizolowana_kreska_w_kolorze_tuszu_jest_podkresleniem()
    {
        var (fill, area) = RuleCanvas();
        foreach (var (x, y) in Rect(10, 30, 180, 3)) fill[y * area.Width + x] = true;

        var rule = GlyphCoverBuilder.DetectRules(fill, area.Width, area, 20, _ => true);

        Assert.NotNull(rule);
        Assert.True(rule[31 * area.Width + 100]);
        Assert.False(rule[20 * area.Width + 100]);
    }

    [Fact]
    public void Pasek_dotykajacy_krawedzi_obszaru_nie_jest_podkresleniem()
    {
        var (fill, area) = RuleCanvas();
        foreach (var (x, y) in Rect(0, 37, 200, 3)) fill[y * area.Width + x] = true;

        Assert.Null(GlyphCoverBuilder.DetectRules(fill, area.Width, area, 20, _ => true));
    }

    [Fact]
    public void Kreska_innego_koloru_niz_tusz_nie_jest_podkresleniem()
    {
        var (fill, area) = RuleCanvas();
        foreach (var (x, y) in Rect(10, 30, 180, 3)) fill[y * area.Width + x] = true;

        Assert.Null(GlyphCoverBuilder.DetectRules(fill, area.Width, area, 20, _ => false));
    }

    [Fact]
    public void Ukosne_podkreslenie_nie_zabiera_ogonka_litery_nad_nizszym_koncem()
    {
        var (fill, area) = RuleCanvas();
        for (var x = 10; x < 190; x++)
        {
            var top = 30 + (x - 10) * 2 / 180.0;
            for (var y = (int)Math.Ceiling(top); y < top + 4; y++) fill[y * area.Width + x] = true;
        }
        foreach (var (x, y) in Rect(170, 28, 10, 4)) fill[y * area.Width + x] = true;

        var rule = GlyphCoverBuilder.DetectRules(fill, area.Width, area, 20, _ => true);

        Assert.NotNull(rule);
        Assert.True(rule[33 * area.Width + 100]);
        Assert.False(rule[31 * area.Width + 175]);
        Assert.False(rule[29 * area.Width + 175]);
    }

    [Fact]
    public void Cien_ogonka_nad_ciemnym_paskiem_jest_zakryty_kolorem_sciany()
    {
        const int wall = 0x464646;
        var frame = Solid(Width, Height, wall);
        foreach (var (x, y) in Rect(0, 112, Width, Height - 112)) Set(frame, x, y, 0x111111);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6).Concat(Rect(82, 90, 6, 14)).ToList();
        Paint(frame, glyphs, 0x050505, grow: 3, dx: 3, dy: 3);
        Paint(frame, glyphs, 0x050505, grow: 3);
        Paint(frame, glyphs, 0xF0F07A);

        var box = new RectPx(36, 46, 200, 62);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        Assert.NotNull(cover);
        var patchOrigin = (X: box.X + (int)cover.PatchX, Y: box.Y + (int)cover.PatchY);
        foreach (var (x, y) in new[] { (86, 106), (88, 108), (90, 109) })
        {
            var o = ((y - patchOrigin.Y) * cover.PatchPixelWidth + x - patchOrigin.X) * 4;
            Assert.Equal(255, cover.PatchPbgra[o + 3]);
            Assert.InRange(cover.PatchPbgra[o + 1], 0x46 - 14, 0x46 + 14);
        }
    }

    [Fact]
    public void Lekka_latka_ma_ten_sam_wzorzec_sledzenia_bez_pikseli()
    {
        var frame = Solid(Width, Height, 0x2B3A4C);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x000000, grow: 3);
        Paint(frame, glyphs, 0xF5F5F5);
        var box = new RectPx(36, 46, 200, 50);

        var full = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;
        var light = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"], patch: false)!;

        Assert.True(full.HasPatch);
        Assert.False(light.HasPatch);
        Assert.NotNull(light.Track);
        Assert.Equal(full.Track!.Points, light.Track.Points);
        Assert.Equal(full.Track.Values, light.Track.Values);
        Assert.Equal(full.Track.Mask, light.Track.Mask);
        Assert.Equal(full.Lines.Count, light.Lines.Count);
        Assert.Equal(full.MaskFraction, light.MaskFraction);
    }

    [Fact]
    public void Przesuniecie_lekkiej_latki_przesuwa_kotwice_i_wzorzec()
    {
        var frame = Solid(Width, Height, 0x2B3A4C);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xF5F5F5);
        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"], patch: false)! with { Anchor = box };

        var moved = GlyphCoverBuilder.Shift(cover, 7, -3)!;

        Assert.False(moved.HasPatch);
        Assert.Equal(box.Offset(7, -3), moved.Anchor);
        Assert.Equal(cover.Track!.OriginX + 7, moved.Track!.OriginX, 6);
        Assert.Equal(cover.Track.OriginY - 3, moved.Track.OriginY, 6);
    }

    [Fact]
    public void Ikona_ze_znacznikiem_trafia_do_lapki_z_przezroczystym_tlem_a_oryginal_jest_zakryty()
    {
        var frame = Solid(Width, Height, 0xE6E1D8);
        Paint(frame, Word(40, 50, capHeight: 40, stroke: 6, letters: 2), 0x202020);
        foreach (var (x, y) in Rect(134, 50, 52, 42))
        {
            var edge = x < 137 || x >= 183 || y < 53 || y >= 89;
            Set(frame, x, y, edge ? 0x2A2830 : 0x8A86A0);
        }
        var text = new OcrWord("Hi", new RectPx(36, 46, 80, 50));
        var icon = new OcrWord(InlineIcons.Marker, new RectPx(130, 46, 60, 50));
        var line = new OcrLine("Hi [X]", new RectPx(36, 46, 154, 50), [text, icon]);
        var block = new GameTranslatorOverlay.Core.Text.TextBlock("Hi [X]", line.Box, [line]);

        var cover = GlyphCoverBuilder.BuildForBlock(frame, block, default, 1.0)!;

        var glyphIcon = Assert.Single(cover.Icons);
        Assert.Equal(60, glyphIcon.PixelWidth);
        Assert.Equal(50, glyphIcon.PixelHeight);
        Assert.Equal(130 - block.Box.X, glyphIcon.X);
        Assert.Equal(0, glyphIcon.Pbgra[3]);
        Assert.Equal(255, glyphIcon.Pbgra[((25 * 60) + 30) * 4 + 3]);
        var patchOrigin = (X: block.Box.X + (int)cover.PatchX, Y: block.Box.Y + (int)cover.PatchY);
        var o = ((70 - patchOrigin.Y) * cover.PatchPixelWidth + (160 - patchOrigin.X)) * 4;
        Assert.Equal(255, cover.PatchPbgra[o + 3]);
        Assert.InRange(cover.PatchPbgra[o + 2], 0xE6 - 12, 0xE6 + 12);
    }

    [Fact]
    public void Pochylona_linia_ma_wysokosc_liter_jak_prosta()
    {
        double Ascent(int slant)
        {
            var frame = Solid(Width, Height, 0x2B3A4C);
            for (var i = 0; i < 12; i++)
                Paint(frame, Word(10 + i * 28, 40 + i * slant, capHeight: 24, stroke: 4, letters: 1), 0xF5F5F5);
            var box = new RectPx(6, 36, 340, 32 + 11 * slant);
            var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;
            var measured = Assert.Single(cover.Lines);
            return measured.Baseline - measured.InkTop;
        }

        var straight = Ascent(0);
        var slanted = Ascent(2);

        Assert.InRange(straight, 22, 27);
        Assert.InRange(slanted, straight * 0.85, straight * 1.15);
    }

    [Fact]
    public void Ciemny_napis_na_jasnej_tabliczce_z_ciemna_ramka_ma_ciemny_wzorzec_i_pelny_kontrast()
    {
        var box = new RectPx(36, 46, 200, 50);
        var frame = Plate(wall: 0x4E5C58, rim: 0x2E1A20, plate: 0x908274, box);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x151714);

        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"]);

        Assert.NotNull(cover);
        AssertColor(0x151714, cover.TextRgb, 16);
        Assert.NotNull(cover.Track);
        Assert.True(cover.Track.TemplateContrast < -80, $"kontrast wzorca {cover.Track.TemplateContrast}");
        Assert.True(cover.Track.Contrast > 80, $"kontrast {cover.Track.Contrast}");
        AssertOnGlyphs(cover, glyphs);
        var origin = (X: box.X + (int)cover.PatchX, Y: box.Y + (int)cover.PatchY);
        foreach (var (x, y) in glyphs.Where(static (_, i) => i % 7 == 0))
        {
            var o = ((y - origin.Y) * cover.PatchPixelWidth + (x - origin.X)) * 4;
            Assert.Equal(255, cover.PatchPbgra[o + 3]);
            Assert.InRange(cover.PatchPbgra[o + 2], 0x90 - 14, 0x90 + 14);
        }
    }

    [Fact]
    public void Jasny_napis_na_ciemnej_tabliczce_z_jasna_ramka_ma_jasny_wzorzec()
    {
        var box = new RectPx(36, 46, 200, 50);
        var frame = Plate(wall: 0x8A8E80, rim: 0x6C8088, plate: 0x022E20, box);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x6C8088);

        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"]);

        Assert.NotNull(cover);
        AssertColor(0x6C8088, cover.TextRgb, 16);
        Assert.True(cover.Track!.TemplateContrast > 40, $"kontrast wzorca {cover.Track.TemplateContrast}");
        AssertOnGlyphs(cover, glyphs);
    }

    [Fact]
    public void Zwykly_jasny_napis_na_ciemnym_tle_i_ciemny_na_jasnym_zachowuja_biegunowosc()
    {
        var box = new RectPx(36, 46, 200, 50);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        var dark = Solid(Width, Height, 0x1A1F24);
        Paint(dark, glyphs, 0xF0F0F0);
        var light = Solid(Width, Height, 0xE8E2D4);
        Paint(light, glyphs, 0x202020);

        var bright = GlyphCoverBuilder.Build(dark, box, [box], ["Hill"])!;
        var ink = GlyphCoverBuilder.Build(light, box, [box], ["Hill"])!;

        AssertColor(0xF0F0F0, bright.TextRgb, 12);
        Assert.True(bright.Track!.TemplateContrast > 150);
        AssertOnGlyphs(bright, glyphs);
        AssertColor(0x202020, ink.TextRgb, 12);
        Assert.True(ink.Track!.TemplateContrast < -150);
        AssertOnGlyphs(ink, glyphs);
    }

    [Fact]
    public void Jasny_napis_na_ciemnym_panelu_z_jasnym_brzegiem_w_pierscieniu_tla_ma_jasny_wzorzec()
    {
        var box = new RectPx(36, 46, 200, 50);
        var frame = Plate(wall: 0x1A1F24, rim: 0x6A6A6A, plate: 0x1A1F24, box);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xF0F0F0);

        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        AssertColor(0xF0F0F0, cover.TextRgb, 12);
        Assert.True(cover.Track!.TemplateContrast > 100);
        AssertOnGlyphs(cover, glyphs);
    }

    [Fact]
    public void Duza_tabliczka_liczona_w_zmniejszonej_rozdzielczosci_tez_ma_ciemny_wzorzec()
    {
        var box = new RectPx(56, 96, 632, 168);
        var frame = Plate(wall: 0x4E5C58, rim: 0x2E1A20, plate: 0x908274, box, width: 800, height: 400, inner: 18, outer: 60);
        var glyphs = Word(60, 100, capHeight: 160, stroke: 24);
        Paint(frame, glyphs, 0x151714);

        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"]);

        Assert.NotNull(cover);
        Assert.Equal(3, cover.Track!.Step);
        AssertColor(0x151714, cover.TextRgb, 16);
        Assert.True(cover.Track.TemplateContrast < -80, $"kontrast wzorca {cover.Track.TemplateContrast}");
        AssertOnGlyphs(cover, glyphs);
    }

    private static OcrBitmap Plate(int wall, int rim, int plate, RectPx box, int width = Width, int height = Height, int inner = 5, int outer = 16)
    {
        var frame = Solid(width, height, wall);
        Paint(frame, Rect(box.X - outer, box.Y - outer, box.Width + 2 * outer, box.Height + 2 * outer), rim);
        Paint(frame, Rect(box.X - inner, box.Y - inner, box.Width + 2 * inner, box.Height + 2 * inner), plate);
        return frame;
    }

    private static void AssertOnGlyphs(GlyphCover cover, IEnumerable<(int X, int Y)> glyphs)
    {
        var track = cover.Track!;
        var ink = glyphs.ToHashSet();
        var on = track.GlyphPoints.Count(p => ink.Contains(
            ((int)Math.Floor(track.OriginX + (p % track.Width) * track.Step), (int)Math.Floor(track.OriginY + (p / track.Width) * track.Step))));
        Assert.True(track.GlyphPoints.Length >= 100 && on >= track.GlyphPoints.Length * 0.9, $"punkty wzorca na literach: {on}/{track.GlyphPoints.Length}");
    }

    private static (bool[] Fill, RectPx Area) RuleCanvas() => (new bool[200 * 40], new RectPx(0, 0, 200, 40));

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
            if (letter % 2 == 0)
            {
                var middle = top + capHeight / 2 - stroke / 2;
                for (var y = middle; y < middle + stroke; y++)
                    for (var px = left; px <= right; px++) points.Add((px, y));
            }
        }
        return points.ToList();
    }

    private static IEnumerable<(int X, int Y)> Rect(int x, int y, int w, int h)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++) yield return (xx, yy);
    }

    private static void Paint(OcrBitmap frame, IEnumerable<(int X, int Y)> points, int rgb, int grow = 0, int dx = 0, int dy = 0)
    {
        foreach (var (x, y) in points)
        {
            for (var oy = -grow; oy <= grow; oy++)
                for (var ox = -grow; ox <= grow; ox++)
                    Set(frame, x + ox + dx, y + oy + dy, rgb);
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

    private static void AssertColor(int expected, int actual, int tolerance)
    {
        Assert.True(actual >= 0, "brak koloru");
        for (var shift = 0; shift <= 16; shift += 8)
        {
            Assert.InRange((actual >> shift) & 0xFF, ((expected >> shift) & 0xFF) - tolerance, ((expected >> shift) & 0xFF) + tolerance);
        }
    }
}
