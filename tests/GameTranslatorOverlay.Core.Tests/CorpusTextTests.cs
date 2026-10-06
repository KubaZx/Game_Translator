using GameTranslatorOverlay.Core.Corpus;

namespace GameTranslatorOverlay.Core.Tests;

public class CorpusTextTests
{
    [Fact]
    public void MatchKey_ignoruje_wielkosc_liter_cudzyslowy_myslniki_i_lamania()
    {
        var key = CorpusText.MatchKey("  It’s  “LATE”… — go\n home ");

        Assert.Equal("it's \"late\"... - go home", key);
    }

    [Fact]
    public void MatchKey_usuwa_znaczniki_rich_text_i_znaki_zerowej_szerokosci()
    {
        Assert.Equal("we <3 so much", CorpusText.MatchKey("<i>We</i> <3 <color=#ff0000>so</color>​ much"));
    }

    [Fact]
    public void StripRichText_zamienia_br_na_nowy_wiersz_i_zostawia_zwykle_nawiasy()
    {
        Assert.Equal("Top\nBottom 2 < 3", CorpusText.StripRichText("<b>Top</b><br/>Bottom 2 < 3"));
        Assert.Equal("bez znaczników", CorpusText.StripRichText("bez znaczników"));
    }

    [Theory]
    [InlineData("items;", "items")]
    [InlineData("'inspect)", "inspect")]
    [InlineData("...", "")]
    [InlineData("ok", "ok")]
    public void LooseKey_obcina_znaki_nieliterowe_na_brzegach(string key, string expected)
    {
        Assert.Equal(expected, CorpusText.LooseKey(key));
    }

    [Theory]
    [InlineData("+10%", "+10%")]
    [InlineData("(-5)", "-5")]
    [InlineData("$25.", "$25")]
    [InlineData("€ 40!", "€ 40")]
    [InlineData("25 %", "25 %")]
    [InlineData("#1:", "#1")]
    [InlineData("- go", "go")]
    [InlineData("score: 10 -", "score: 10")]
    public void LooseKey_zostawia_znak_waluty_i_procent_przy_liczbie(string key, string expected)
    {
        Assert.Equal(expected, CorpusText.LooseKey(key));
    }

    [Theory]
    [InlineData("you need 3 keys", "you need 3 keys", 0)]
    [InlineData("the o1d vau1t", "the old vault", 2)]
    [InlineData("th1s 0ld 5ign 8ox", "this old sign box", 4)]
    [InlineData("level 4", "level 3", -1)]
    [InlineData("5kg", "6kg", -1)]
    [InlineData("10am", "ioam", 2)]
    [InlineData("ioam", "10am", -1)]
    [InlineData("x3", "x", -1)]
    [InlineData("x", "x3", -1)]
    [InlineData("-10%", "+10%", -1)]
    [InlineData("10", "10%", -1)]
    [InlineData("$5", "s5", -1)]
    [InlineData("a-b", "a+b", 1)]
    [InlineData("11m sure", "i'm sure", 2)]
    [InlineData("it's", "it1s", -1)]
    public void Odleglosc_z_ochrona_liczb(string reading, string corpus, int expected)
    {
        var distance = EditDistance.BoundedGuarded(reading, corpus, 10);

        Assert.Equal(expected < 0 ? 11 : expected, distance);
    }

    [Theory]
    [InlineData("level 3 of 10", "3|10")]
    [InlineData("no digits", "")]
    [InlineData("+25% and 3/5", "25|3|5")]
    [InlineData("x2", "")]
    [InlineData("the 1ighthouse b0oks", "")]
    public void DigitSignature_zbiera_samodzielne_liczby_a_pomija_cyfry_sklejone_z_literami(string text, string expected)
    {
        Assert.Equal(expected, CorpusText.DigitSignature(text));
    }

    [Fact]
    public void WordCount_i_LetterOrDigitCount_licza_poprawnie()
    {
        Assert.Equal(3, CorpusText.WordCount("one two  three"));
        Assert.Equal(0, CorpusText.WordCount(""));
        Assert.Equal(6, CorpusText.LetterOrDigitCount("ab, 12 c!d"));
    }
}

public class EditDistanceTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    public void Bounded_liczy_odleglosc_levenshteina(string a, string b, int expected)
    {
        Assert.Equal(expected, EditDistance.Bounded(a, b, 10));
    }

    [Fact]
    public void Bounded_przerywa_po_przekroczeniu_limitu()
    {
        Assert.Equal(2, EditDistance.Bounded("abcdef", "uvwxyz", 1));
        Assert.Equal(3, EditDistance.Bounded("a", "abcd", 2));
    }

    [Fact]
    public void Ratio_zwraca_zero_ponizej_minimum()
    {
        Assert.Equal(1.0, EditDistance.Ratio("same", "same", 0.9));
        Assert.InRange(EditDistance.Ratio("the old lantern", "the o1d lantern", 0.8), 0.93, 0.94);
        Assert.Equal(0.0, EditDistance.Ratio("abc", "xyz", 0.5));
    }

    [Fact]
    public void FindWithin_znajduje_przyblizone_wystapienie_i_jego_granice()
    {
        const string text = "first part. the quick brown fox jumps. last part.";

        var found = EditDistance.FindWithin("the qu1ck brown fox", text, 2);

        Assert.NotNull(found);
        Assert.Equal(1, found.Value.Distance);
        Assert.Equal(text.IndexOf("the quick", StringComparison.Ordinal), found.Value.Start);
        Assert.Equal(text.IndexOf(" jumps", StringComparison.Ordinal), found.Value.End);
    }

    [Fact]
    public void FindWithin_zwraca_null_gdy_wzorzec_jest_za_daleko()
    {
        Assert.Null(EditDistance.FindWithin("completely different", "nothing alike here", 2));
        Assert.Null(EditDistance.FindWithin("", "text", 2));
    }
}

public class CorpusJsonlTests
{
    [Fact]
    public void Zapis_i_odczyt_zachowuja_pola_i_pomijaja_puste()
    {
        var entry = new CorpusEntry
        {
            Key = "line:1",
            En = "Hello there.",
            Kind = CorpusEntryKind.Subtitle,
            Speaker = "Narrator",
            Order = 3,
            DurationMs = 1250,
            Source = "synthetic_file",
        };

        var line = CorpusJsonl.Serialize(entry);
        var back = CorpusJsonl.Deserialize(line);

        Assert.Contains("\"kind\":\"subtitle\"", line);
        Assert.DoesNotContain("context", line);
        Assert.Equal(entry, back);
    }

    [Fact]
    public void Read_pomija_puste_wiersze_i_zglasza_zly_wiersz_z_numerem()
    {
        var good = CorpusJsonl.Serialize(new CorpusEntry { Key = "k", En = "Text", Kind = CorpusEntryKind.Ui, Source = "s" });

        var entries = CorpusJsonl.Read(new StringReader(good + "\n\n" + good + "\n"));
        var error = Assert.Throws<FormatException>(() => CorpusJsonl.Read(new StringReader(good + "\n{zly")));

        Assert.Equal(2, entries.Count);
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void Write_i_ReadFile_dzialaja_na_pliku()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gto-corpus-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = new StreamWriter(path))
            {
                CorpusJsonl.Write(writer, [new CorpusEntry { Key = "a", En = "Alpha", Kind = CorpusEntryKind.Dialog, Source = "s" }]);
            }

            var entries = CorpusJsonl.ReadFile(path);

            Assert.Equal("Alpha", Assert.Single(entries).En);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("UI", true, CorpusEntryKind.Ui)]
    [InlineData("dialog", true, CorpusEntryKind.Dialog)]
    [InlineData(" subtitle ", true, CorpusEntryKind.Subtitle)]
    [InlineData("menu", false, CorpusEntryKind.Ui)]
    [InlineData(null, false, CorpusEntryKind.Ui)]
    public void TryParseKind_rozpoznaje_rodzaje(string? text, bool ok, CorpusEntryKind expected)
    {
        Assert.Equal(ok, CorpusEntry.TryParseKind(text, out var kind));
        if (ok) Assert.Equal(expected, kind);
    }
}
