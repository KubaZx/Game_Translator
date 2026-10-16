using System.Text.Encodings.Web;
using System.Text.Json;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class LlmTranslationStreamParserTests
{
    private static (List<(int Index, string Text)> Items, LlmTranslationStreamParser Parser) Create(int expected)
    {
        var items = new List<(int Index, string Text)>();
        return (items, new LlmTranslationStreamParser(expected, (index, text) => items.Add((index, text))));
    }

    private static List<string> Feed(string content, int expected, IEnumerable<int> cuts)
    {
        var (items, parser) = Create(expected);
        var previous = 0;
        foreach (var cut in cuts.Append(content.Length))
        {
            parser.Append(content[previous..cut]);
            previous = cut;
        }
        Assert.Equal(Enumerable.Range(0, items.Count), items.Select(static i => i.Index));
        return items.Select(static i => i.Text).ToList();
    }

    [Fact]
    public void Element_jest_zglaszany_dokladnie_przy_domknieciu_cudzyslowu()
    {
        const string content = """{"translations": ["Raz", "Dwa"]}""";
        var (items, parser) = Create(2);
        var firstClose = content.IndexOf("Raz\"", StringComparison.Ordinal) + 3;

        for (var i = 0; i < content.Length; i++)
        {
            parser.Append(content[i].ToString());
            var expected = i < firstClose ? 0 : i < content.IndexOf("Dwa\"", StringComparison.Ordinal) + 3 ? 1 : 2;
            Assert.Equal(expected, items.Count);
        }
        Assert.Equal(new (int, string)[] { (0, "Raz"), (1, "Dwa") }, items);
        Assert.True(parser.IsStopped);
    }

    public static TheoryData<string> EscapedAnswers => new()
    {
        """{"translations": ["Cytat: \"Mroczna Latarnia\"", "Ukośnik \\ i \/ łamanie\nwiersza\ttab"]}""",
        """{"translations":["Łódź 🏮","\b\f\r koniec"]}""",
        "```json\n{\"translations\": [\"W płocie\", \"Drugi\"]}\n```",
        """<think>{"translations": ["nie to", "ani to"]}</think>{"translations": ["Po namyśle", "Dobrze"]}""",
        """{"notes": "translations", "translation": "x", "translations": ["Klucz", "Szukany"]}""",
        "Sure! Here you go:\n{\n  \"translations\" :\n  [\n    \"Odstępy\" ,\n    \"Wszędzie\"\n  ]\n}",
    };

    [Theory]
    [MemberData(nameof(EscapedAnswers))]
    public void Kazde_ciecie_w_dwoch_miejscach_daje_wynik_jak_pelny_parser(string content)
    {
        var expected = LlmTranslationPrompt.ParseTranslations(content, 2);
        Assert.NotNull(expected);

        for (var cut = 0; cut <= content.Length; cut++)
        {
            Assert.Equal(expected, Feed(content, 2, [cut]));
        }
        Assert.Equal(expected, Feed(content, 2, Enumerable.Range(1, content.Length - 1)));
    }

    [Fact]
    public void Losowe_kawalki_i_losowe_teksty_daja_wynik_jak_pelny_parser()
    {
        var random = new Random(20261016);
        string[] alphabet =
        [
            "a", "b", "X", "Z", " ", "ą", "ę", "ł", "ó", "ś", "ż", "„", "”", "\"", "\\", "/", "\n", "\t",
            "{", "}", "[", "]", ",", ":", "😀", "🏮", "0", "9", "<", ">", "&", "'",
        ];
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        for (var round = 0; round < 300; round++)
        {
            var count = random.Next(1, 6);
            var texts = Enumerable.Range(0, count)
                .Select(_ => "x" + string.Concat(Enumerable.Range(0, random.Next(1, 20)).Select(_ => alphabet[random.Next(alphabet.Length)])))
                .ToArray();
            var content = JsonSerializer.Serialize(new { translations = texts }, round % 2 == 0 ? options : JsonSerializerOptions.Default);
            var cuts = new SortedSet<int>();
            for (var k = random.Next(0, 12); k > 0; k--) cuts.Add(random.Next(0, content.Length + 1));

            var expected = LlmTranslationPrompt.ParseTranslations(content, count);
            Assert.Equal(expected, Feed(content, count, cuts));
        }
    }

    [Theory]
    [InlineData("""{"translations": ["Dobry", "Zła \q ucieczka", "Trzeci"]}""", 1)]
    [InlineData("{\"translations\": [\"Dobry\", \"Surowy\nznak\", \"Trzeci\"]}", 1)]
    [InlineData("""{"translations": ["Dobry", 42, "Trzeci"]}""", 1)]
    [InlineData("""{"translations": ["Dobry", "   ", "Trzeci"]}""", 1)]
    [InlineData("""{"translations": ["Dobry", null, "Trzeci"]}""", 1)]
    [InlineData("""{"translations": ["Dobry", "Drugi", "Trzeci", "Nadmiarowy"]}""", 3)]
    [InlineData("""{"translations": ["Dobry", "\u00zz", "Trzeci"]}""", 1)]
    public void Element_ktorego_nie_przyjalby_JSON_konczy_zglaszanie(string content, int emitted)
    {
        var (items, parser) = Create(3);

        parser.Append(content);

        Assert.Equal(emitted, items.Count);
        Assert.True(parser.IsStopped);
    }

    [Fact]
    public void Stop_wstrzymuje_kolejne_elementy()
    {
        var (items, parser) = Create(3);

        parser.Append("""{"translations": ["Raz", "Dw""");
        parser.Stop();
        parser.Append("""a", "Trzy"]}""");

        Assert.Equal(new (int, string)[] { (0, "Raz") }, items);
    }

    [Fact]
    public void Tekst_bez_JSON_nie_zglasza_niczego()
    {
        var (items, parser) = Create(1);

        parser.Append("Po prostu tłumaczenie bez formatu");

        Assert.Empty(items);
        Assert.False(parser.IsStopped);
    }

    [Fact]
    public void Klucz_bez_tablicy_wraca_do_szukania()
    {
        var (items, parser) = Create(1);

        parser.Append("""{"result": "ok", "translations": ["Znaleziono"]}""");

        Assert.Equal(new (int, string)[] { (0, "Znaleziono") }, items);
    }
}
