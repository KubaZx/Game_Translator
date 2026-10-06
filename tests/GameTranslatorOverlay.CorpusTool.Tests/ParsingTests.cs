using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.CorpusTool.Parsing;
using GameTranslatorOverlay.CorpusTool.Unity;

namespace GameTranslatorOverlay.CorpusTool.Tests;

public class TolerantTextTests
{
    [Fact]
    public void Czysty_utf8_z_bom_dekoduje_sie_bez_zastepstw()
    {
        var decoded = TolerantText.Decode([0xEF, 0xBB, 0xBF, .. "Zażółć"u8]);

        Assert.Equal("Zażółć", decoded.Text);
        Assert.Equal(0, decoded.FallbackBytes);
    }

    [Fact]
    public void Niepoprawne_bajty_czyta_jak_windows_1252_a_poprawne_sekwencje_jak_utf8()
    {
        byte[] bytes = [.. "Wait"u8, 0x85, .. " café "u8, 0x93, (byte)'x', 0x94, 0xE9];

        var decoded = TolerantText.Decode(bytes);

        Assert.Equal("Wait… café “x”é", decoded.Text);
        Assert.Equal(4, decoded.FallbackBytes);
    }

    [Fact]
    public void Ucieta_sekwencja_i_nadmiarowe_kodowanie_sa_zastepowane()
    {
        var decoded = TolerantText.Decode([(byte)'a', 0xC0, 0x80, 0xE2, 0x82]);

        Assert.Equal(4, decoded.FallbackBytes);
        Assert.StartsWith("a", decoded.Text);
    }
}

public class CsvTableTests
{
    [Fact]
    public void Czyta_pola_w_cudzyslowach_przecinki_nowe_wiersze_i_podwojne_cudzyslowy()
    {
        const string text = "Key,Value-En,Context\r\nk1,\"Hello, \"\"friend\"\"\",note\r\nk2,\"Two\nlines\",\r\n\r\nk3,Plain\n";

        var table = CsvTable.Parse(text);

        Assert.Equal(["Key", "Value-En", "Context"], table.Header);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Hello, \"friend\"", table.Rows[0][1]);
        Assert.Equal("Two\nlines", table.Rows[1][1]);
        Assert.Equal("", table.Rows[1][2]);
        Assert.Equal("", CsvTable.Cell(table.Rows[2], 2));
        Assert.Equal(1, table.ColumnIndex(" value-en "));
        Assert.Equal(-1, table.ColumnIndex("French"));
        Assert.Equal(-1, table.ColumnIndex(null));
    }

    [Fact]
    public void Pusty_tekst_daje_pusta_tabele_a_ostatni_wiersz_bez_konca_linii_jest_czytany()
    {
        Assert.Empty(CsvTable.Parse("").Header);
        var table = CsvTable.Parse("a,b\n1,\"2\"");
        Assert.Equal("2", table.Rows[0][1]);
    }
}

public class SrtParserTests
{
    [Fact]
    public void Czyta_wiele_napisow_z_czasem_i_odpornie_na_bledy()
    {
        const string srt = "1\r\n00:00:00,000 --> 00:00:02,500\r\n[GUIDE] Welcome aboard!\r\n\r\n" +
                           "garbage line\r\n\r\n" +
                           "2\n00:00:03.1 --> 00:00:004,250\nSecond line\ncontinues here\n\n" +
                           "3\nnot a timing\n";

        Assert.True(SrtParser.LooksLikeSrt(srt));
        var cues = SrtParser.Parse(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal(2500, cues[0].DurationMs);
        Assert.Equal("[GUIDE] Welcome aboard!", cues[0].Text);
        Assert.Equal(3100, cues[1].StartMs);
        Assert.Equal(4250, cues[1].EndMs);
        Assert.Equal("Second line\ncontinues here", cues[1].Text);
    }

    [Theory]
    [InlineData("Key,Value\nx,y")]
    [InlineData("1\nno timing")]
    [InlineData("")]
    public void Rozpoznaje_tylko_prawdziwe_pliki_srt(string text)
    {
        Assert.False(SrtParser.LooksLikeSrt(text));
    }
}

public class NamePatternTests
{
    [Theory]
    [InlineData("GameplayStrings*", "GameplayStrings_Island", true)]
    [InlineData("GameplayStrings*", "gameplaystrings", true)]
    [InlineData("* (en-US)", "Intro (en-US)", true)]
    [InlineData("* (en-US)", "Intro (fr)", false)]
    [InlineData("*_fr", "Bob_Hello_fr", true)]
    [InlineData("Line?", "Line1", true)]
    [InlineData("Line?", "Line12", false)]
    [InlineData("a.b", "aXb", false)]
    public void Wzorzec_dziala_jak_glob_bez_wielkosci_liter(string glob, string name, bool expected)
    {
        Assert.Equal(expected, new NamePattern(glob).IsMatch(name));
    }

    [Fact]
    public void Wykluczenia_maja_pierwszenstwo()
    {
        var include = new[] { new NamePattern("*") };
        var exclude = new[] { new NamePattern("*_de") };

        Assert.True(NamePattern.Matches("Hello", include, exclude));
        Assert.False(NamePattern.Matches("Hello_DE", include, exclude));
        Assert.False(NamePattern.Matches("Hello", [], exclude));
    }
}

public class RecipeRunnerTests
{
    internal static CorpusRecipe Recipe() => new()
    {
        Format = "unity-textasset",
        Container = "Game_Data/data.unity3d",
        File = "resources.assets",
        Sources =
        [
            new CorpusSourceRecipe
            {
                Id = "strings", Parser = "csv", Kind = "ui", Include = ["Strings*"],
                KeyColumn = "Key", TextColumn = "Value-En", ContextColumns = ["Area", "Context", "Loc Context"],
            },
            new CorpusSourceRecipe
            {
                Id = "dialog", Parser = "csv", Kind = "dialog", Include = ["* (en-US)"],
                KeyColumn = "id", TextColumn = "text", NodeColumns = ["file", "node"], OrderColumn = "lineNumber",
                SpeakerPattern = @"^(?<speaker>[^:\r\n]{0,40}):\s*",
            },
            new CorpusSourceRecipe
            {
                Id = "subs", Parser = "srt", Kind = "subtitle", Include = ["*"], Exclude = ["*_fr"],
                SpeakerPattern = @"^\[(?<speaker>[^\]\r\n]{1,40})\]\s*", InheritSpeaker = true,
            },
        ],
    };

    internal static TextAssetData[] Assets() =>
    [
        new("Strings_Main", SyntheticUnity.Utf8(
            "Key,Value-En,Area,Context,French\nlblOpen,Open the <b>door</b>,HUB,Button label,Ouvrir\nlblEmpty,,HUB,,\nlblSym,???,HUB,,\n,No key here,,,\n")),
        new("Strings_Broken", [.. "Key,Value-En\nk,Wait"u8, 0x85, .. "\n"u8]),
        new("Talk (en-US)", SyntheticUnity.Utf8(
            "id,text,file,node,lineNumber\nline:1,Mira:Hi there <i>friend</i>!,Talk,Start,4\nline:2,:*CLANG*,Talk,Start,5\nline:3,Plain narration,Talk,End,x\n")),
        new("Talk (fr)", SyntheticUnity.Utf8("id,text,file,node,lineNumber\nline:1,Mira:Salut,Talk,Start,4\n")),
        new("Mira_Hello", SyntheticUnity.Utf8("1\n00:00:00,000 --> 00:00:01,200\n[MIRA] Hello!\n\n2\n00:00:01,300 --> 00:00:02,000\nAnd goodbye.\n\n3\n00:00:02,100 --> 00:00:02,500\n<size=1>♪</size>\n")),
        new("Mira_Hello_fr", SyntheticUnity.Utf8("1\n00:00:00,000 --> 00:00:01,200\n[MIRA] Bonjour !\n")),
        new("Config", SyntheticUnity.Utf8("{\"setting\": true}")),
    ];

    [Fact]
    public void Recepta_buduje_wpisy_z_kontekstem_mowca_wezlem_i_czasem()
    {
        var result = RecipeRunner.Run(Recipe(), Assets());
        var byKey = result.Entries.ToDictionary(static e => e.Key);

        Assert.Equal("Open the door", byKey["lblOpen"].En);
        Assert.Equal("HUB | Button label", byKey["lblOpen"].Context);
        Assert.Equal(CorpusEntryKind.Ui, byKey["lblOpen"].Kind);
        Assert.Equal("Strings_Main", byKey["lblOpen"].Source);
        Assert.Equal("No key here", byKey["Strings_Main#4"].En);
        Assert.DoesNotContain("lblEmpty", byKey.Keys);
        Assert.DoesNotContain("lblSym", byKey.Keys);
        Assert.Equal("Wait…", byKey["k"].En);

        Assert.Equal("Hi there friend!", byKey["line:1"].En);
        Assert.Equal("Mira", byKey["line:1"].Speaker);
        Assert.Equal("Talk/Start", byKey["line:1"].Node);
        Assert.Equal(4, byKey["line:1"].Order);
        Assert.Null(byKey["line:2"].Speaker);
        Assert.Equal("*CLANG*", byKey["line:2"].En);
        Assert.Null(byKey["line:3"].Order);

        Assert.Equal("Hello!", byKey["Mira_Hello#1"].En);
        Assert.Equal("MIRA", byKey["Mira_Hello#2"].Speaker);
        Assert.Equal(700, byKey["Mira_Hello#2"].DurationMs);
        Assert.DoesNotContain("Mira_Hello#3", byKey.Keys);
        Assert.DoesNotContain(result.Entries, static e => e.En.Contains("Bonjour") || e.En.Contains("Salut"));

        var strings = result.Sources.Single(static s => s.Id == "strings");
        Assert.Equal(2, strings.Assets);
        Assert.Equal(1, strings.AssetsWithFallback);
        Assert.Equal(1, strings.FallbackBytes);
        var subs = result.Sources.Single(static s => s.Id == "subs");
        Assert.Equal(1, subs.Assets);
        Assert.Equal(5, subs.SkippedAssets);
    }

    [Fact]
    public void Bez_dziedziczenia_mowcy_kolejny_napis_nie_ma_mowcy_a_rich_text_mozna_zostawic()
    {
        var recipe = Recipe();
        recipe.Sources[2].InheritSpeaker = false;
        recipe.Sources[0].StripRichText = false;

        var byKey = RecipeRunner.Run(recipe, Assets()).Entries.ToDictionary(static e => e.Key);

        Assert.Null(byKey["Mira_Hello#2"].Speaker);
        Assert.Equal("Open the <b>door</b>", byKey["lblOpen"].En);
    }

    [Fact]
    public void Nieznany_rodzaj_lub_parser_przerywa_z_bledem()
    {
        var recipe = Recipe();
        recipe.Sources[0].Kind = "menu";
        Assert.Throws<FormatException>(() => RecipeRunner.Run(recipe, Assets()));

        recipe = Recipe();
        recipe.Sources[0].Parser = "xml";
        Assert.Throws<FormatException>(() => RecipeRunner.Run(recipe, Assets()));
    }

    [Fact]
    public void Tabela_bez_kolumny_tekstu_jest_pomijana()
    {
        var recipe = Recipe();
        recipe.Sources[0].TextColumn = "Value-Pl";

        var result = RecipeRunner.Run(recipe, Assets());

        Assert.Equal(2, result.Sources[0].SkippedAssets);
        Assert.Equal(0, result.Sources[0].Entries);
    }
}
