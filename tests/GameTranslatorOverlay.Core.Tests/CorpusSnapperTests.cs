using GameTranslatorOverlay.Core.Corpus;

namespace GameTranslatorOverlay.Core.Tests;

public class CorpusIndexTests
{
    private static CorpusEntry Entry(string text, CorpusEntryKind kind = CorpusEntryKind.Ui, string? speaker = null, string key = "") => new()
    {
        Key = key.Length > 0 ? key : text,
        En = text,
        Kind = kind,
        Speaker = speaker,
        Source = "synthetic",
    };

    [Fact]
    public void Build_scala_teksty_rozniace_sie_wielkoscia_liter()
    {
        var index = CorpusIndex.Build([Entry("Pick Up", key: "a"), Entry("PICK UP", key: "b"), Entry("Drop")]);

        Assert.Equal(2, index.TextCount);
        Assert.True(index.TryGetExact("pick up", out var entry));
        Assert.Equal("a", entry.Key);
        Assert.False(index.TryGetExact("throw", out _));
    }

    [Fact]
    public void Build_pomija_wpisy_bez_liter_i_cyfr_oraz_zbiera_mowcow()
    {
        var index = CorpusIndex.Build([Entry("???"), Entry("Hello", CorpusEntryKind.Subtitle, speaker: "Old Captain")]);

        Assert.Equal(1, index.TextCount);
        Assert.True(index.IsSpeaker("old captain"));
        Assert.False(index.IsSpeaker("hello"));
    }

    [Fact]
    public void Pusty_indeks_nie_przyciaga_niczego()
    {
        var snapper = new CorpusSnapper(CorpusIndex.Empty);

        Assert.True(CorpusIndex.Empty.IsEmpty);
        Assert.Null(snapper.Snap("anything at all here"));
        var block = snapper.SnapBlock("Line one\nLine two");
        Assert.Empty(block.Segments);
        Assert.Equal(2, block.LineKeys.Count);
        Assert.Same(CorpusBlockSnap.Empty, snapper.SnapBlock("   "));
    }
}

public class CorpusSnapperTests
{
    private static readonly CorpusEntry[] Synthetic =
    [
        E("Pick Up"),
        E("Inspect"),
        E("Items"),
        E("Hint"),
        E("Use"),
        E("Key"),
        E("V"),
        E("Really?"),
        E("Really!"),
        E("Open Door"),
        E("The Gardener's Workshop"),
        E("You need 3 keys to open the old vault door.", CorpusEntryKind.Dialog),
        E("The lighthouse keeper left a note under the blue lamp.", CorpusEntryKind.Dialog),
        E("Old dusty books. Nothing interesting here though.", CorpusEntryKind.Ui),
        E("There you are! My best student! We have wonderful news for you.", CorpusEntryKind.Dialog),
        E("There you are! My best students! We have wonderful news for you.", CorpusEntryKind.Dialog),
        E("Stay close to me. The tunnel is very dark tonight. Watch out for the falling rocks above.", CorpusEntryKind.Subtitle),
        E("Hey there, how are you doing on this fine morning?", CorpusEntryKind.Subtitle, "Captain"),
        E("When the bell rings we open the gates and everyone runs to the harbour.", CorpusEntryKind.Dialog),
        E("Then everyone runs to the harbour and waits for the ferry to arrive.", CorpusEntryKind.Dialog),
        E("Mind"),
    ];

    private static CorpusEntry E(string text, CorpusEntryKind kind = CorpusEntryKind.Ui, string? speaker = null) => new()
    {
        Key = text,
        En = text,
        Kind = kind,
        Speaker = speaker,
        Source = "synthetic",
    };

    private static CorpusSnapper Snapper(CorpusSnapOptions? options = null) => new(CorpusIndex.Build(Synthetic), options);

    [Fact]
    public void Dokladne_dopasowanie_nie_zalezy_od_wielkosci_liter()
    {
        var match = Snapper().Snap("PICK UP");

        Assert.NotNull(match);
        Assert.Equal(CorpusMatchKind.Exact, match.Kind);
        Assert.Equal("Pick Up", match.Entry.En);
        Assert.Equal(1.0, match.Score);
    }

    [Fact]
    public void Smieci_na_brzegach_nie_psuja_dokladnej_etykiety()
    {
        Assert.Equal("Items", Snapper().Snap("Items;")?.Entry.En);
        Assert.Equal("Inspect", Snapper().Snap("'Inspect)")?.Entry.En);
    }

    [Fact]
    public void Niejednoznaczny_luzny_klucz_nie_przyciaga()
    {
        Assert.Null(Snapper().Snap("Really"));
        Assert.Equal("Really?", Snapper().Snap("really?")?.Entry.En);
    }

    [Fact]
    public void Jednoliterowy_wpis_nigdy_nie_przyciaga()
    {
        Assert.Null(Snapper().Snap("v'"));
    }

    [Fact]
    public void Przyblizone_dopasowanie_poprawia_bledy_ocr_w_dlugim_zdaniu()
    {
        var match = Snapper().Snap("The 1ighthouse keeper Ieft a note under the bIue lamp.");

        Assert.NotNull(match);
        Assert.Equal(CorpusMatchKind.Fuzzy, match.Kind);
        Assert.Equal("The lighthouse keeper left a note under the blue lamp.", match.Entry.En);
        Assert.InRange(match.Score, 0.8, 0.99);
    }

    [Fact]
    public void Przyblizone_dopasowanie_naprawia_rozjechany_apostrof()
    {
        Assert.Equal("The Gardener's Workshop", Snapper().Snap("THE GARDENER 'SWORKSHOP")?.Entry.En);
    }

    [Fact]
    public void Liczby_musza_sie_zgadzac_jeden_do_jednego()
    {
        Assert.Null(Snapper().Snap("You need 5 keys to open the old vault door."));
        Assert.Null(Snapper().Snap("You need 3 keys to open the old vault door 2."));
        Assert.NotNull(Snapper().Snap("You need 3 keys to open the o1d vau1t door."));
    }

    [Fact]
    public void Krotkie_etykiety_bez_tolerancji_ocr_tylko_dokladnie()
    {
        var exactOnly = Snapper(new CorpusSnapOptions { AllowLabels = false });

        Assert.Null(exactOnly.Snap("Opem Door"));
        Assert.Equal("Open Door", exactOnly.Snap("open door")?.Entry.En);
        Assert.Equal("Open Door", Snapper().Snap("Opem Door")?.Entry.En);
    }

    [Fact]
    public void Dwa_bliskie_kandydaty_daja_brak_dopasowania()
    {
        Assert.Null(Snapper().Snap("There you are! My best studen! We have wonderful news for you."));
    }

    [Fact]
    public void Fragment_zawinietego_wiersza_wskazuje_jeden_wpis()
    {
        var match = Snapper().Snap("The tunnel is very dark tonight.");

        Assert.NotNull(match);
        Assert.Equal(CorpusMatchKind.Fragment, match.Kind);
        Assert.StartsWith("Stay close to me.", match.Entry.En);
    }

    [Fact]
    public void Fragment_wspolny_dla_wielu_wpisow_nie_przyciaga()
    {
        Assert.Null(Snapper().Snap("everyone runs to the harbour"));
    }

    [Fact]
    public void Fragmenty_mozna_wylaczyc()
    {
        Assert.Null(Snapper(new CorpusSnapOptions { AllowFragments = false }).Snap("The tunnel is very dark tonight."));
    }

    [Fact]
    public void Prefiks_mowcy_jest_pomijany()
    {
        var match = Snapper().Snap("Captain: Hey there, how are you doing on this fine morning?");

        Assert.Equal(CorpusMatchKind.Exact, match?.Kind);
        Assert.Equal("Captain", match?.Entry.Speaker);
        Assert.NotNull(Snapper().Snap("[CAPTAIN] Hey there, how are you doing on this fine morning?"));
    }

    [Fact]
    public void Zawiniety_blok_dopasowuje_sie_w_calosci()
    {
        var snap = Snapper().SnapBlock("The lighthouse keeper left a\nnote under the blue lamp.");

        Assert.NotNull(snap.Whole);
        Assert.Equal(CorpusMatchKind.Exact, snap.Whole.Kind);
        Assert.True(snap.IsFullyServed);
        Assert.Equal(snap.TotalLength, snap.LocallyServedLength);
    }

    [Fact]
    public void Blok_z_etykietami_przyciaga_kazda_osobno_i_pomija_glif_przycisku()
    {
        var snap = Snapper().SnapBlock("X Hint\nTab Items");

        Assert.Null(snap.Whole);
        Assert.Equal(["Hint", "Items"], snap.Segments.Select(static s => s.Match.Entry.En));
        Assert.All(snap.Segments, static s => Assert.True(s.Partial));
        Assert.True(snap.IsFullyServed);
        Assert.Equal(snap.TotalLength - 4, snap.LocallyServedLength);
        Assert.Equal(snap.TotalLength, snap.LocallyServedLength + snap.IgnorableLength);
    }

    [Fact]
    public void Wiersz_z_opisem_i_etykieta_dzieli_sie_na_dwa_wpisy()
    {
        var snap = Snapper().SnapBlock("Old dusty b0oks. Nothing interesting here though. Inspect");

        Assert.Equal(2, snap.Segments.Count);
        Assert.Equal(CorpusMatchKind.Fuzzy, snap.Segments[0].Match.Kind);
        Assert.Equal("Inspect", snap.Segments[1].Match.Entry.En);
        Assert.True(snap.IsFullyServed);
    }

    [Fact]
    public void Niedopasowane_slowo_w_srodku_blokuje_skladanie_wiersza_z_etykiet()
    {
        Assert.Empty(Snapper().SnapBlock("Use the Key").Segments);
    }

    [Theory]
    [InlineData("Inspect d", false)]
    [InlineData("d Inspect", true)]
    [InlineData("Inspect 25g", false)]
    [InlineData("Inspect ->", true)]
    public void Okruchy_na_koncu_tylko_bez_liter_a_na_poczatku_jednoznakowe(string text, bool expected)
    {
        Assert.Equal(expected, Snapper().SnapBlock(text).HasAnyMatch);
    }

    [Fact]
    public void Krotka_etykieta_w_srodku_zawinietego_zdania_nie_jest_przyciagana()
    {
        var snap = Snapper().SnapBlock("I really have to think this whole thing through before I change my\nmind.");

        Assert.DoesNotContain(snap.Segments, static s => s.Match.Entry.En == "Mind");
        Assert.Equal("Mind", Snapper().SnapBlock("Mind").Whole?.Entry.En);
    }

    [Fact]
    public void Fragmenty_z_kolejnych_wierszy_skladaja_sie_w_caly_wpis()
    {
        var snap = Snapper().SnapBlock(
            "Stay close to me.\nThe tunnel is very dark tonight.\nWatch out for the falling rocks above.\nPress the button quickly please");

        var assembled = Assert.Single(snap.Segments);
        Assert.True(assembled.Assembled);
        Assert.Equal(0, assembled.FirstLine);
        Assert.Equal(3, assembled.LineCount);
        Assert.Equal(CorpusMatchKind.Exact, assembled.Match.Kind);
        Assert.False(snap.IsFullyServed);
    }

    [Fact]
    public void Bez_skladania_zostaja_fragmenty_ktore_nie_sa_obslugiwane_lokalnie()
    {
        var snap = Snapper(new CorpusSnapOptions { AllowAssembly = false }).SnapBlock(
            "Stay close to me.\nThe tunnel is very dark tonight.\nWatch out for the falling rocks above.\nPress the button quickly please");

        Assert.NotEmpty(snap.Segments);
        Assert.All(snap.Segments, static s => Assert.Equal(CorpusMatchKind.Fragment, s.Match.Kind));
        Assert.Equal(0, snap.LocallyServedLength);
        Assert.True(snap.CoveredLength(CorpusMatchKind.Fragment) > 0);
    }

    [Fact]
    public void Wiersze_czesciowe_mozna_wylaczyc()
    {
        Assert.False(Snapper(new CorpusSnapOptions { AllowPartialLines = false }).SnapBlock("X Hint").HasAnyMatch);
    }

    [Fact]
    public void Minimalna_dlugosc_dokladnej_wylacza_krotkie_etykiety()
    {
        var strict = Snapper(new CorpusSnapOptions { MinExactLength = 8 });

        Assert.Null(strict.Snap("Inspect"));
        Assert.NotNull(strict.Snap("the gardener's workshop"));
    }

    [Fact]
    public void Tekst_spoza_korpusu_nie_daje_trafien()
    {
        var snap = Snapper().SnapBlock("Completely unrelated sentence about rockets and planets.\nAnother line of text");

        Assert.False(snap.HasAnyMatch);
        Assert.Null(snap.Whole);
        Assert.False(snap.IsFullyServed);
    }
}
