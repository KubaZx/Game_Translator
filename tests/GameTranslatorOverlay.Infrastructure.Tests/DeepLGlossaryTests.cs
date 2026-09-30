using System.Text.Json;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class DeepLGlossaryTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly GlossaryTerm[] Terms =
    [
        new("Waystone", "Kamień drogi"),
        new("Energy Shield", "Tarcza energetyczna"),
    ];

    private static TranslationContext WithTerms(params GlossaryTerm[] terms) =>
        new(null, terms) { GlossaryTerms = terms };

    /// <summary>
    /// Atrapa API DeepL: lista glosariuszy, tworzenie, usuwanie i tłumaczenie (echo „PL:”).
    /// </summary>
    private sealed class FakeDeepL
    {
        public List<object> Existing { get; } = [];
        public int CreateStatus { get; set; } = 201;
        public Func<CapturedRequest, bool> RejectTranslate { get; set; } = static _ => false;
        public FakeHttpHandler Handler { get; }

        public FakeDeepL()
        {
            Handler = new FakeHttpHandler((request, _) => Task.FromResult(Respond(request)));
        }

        private HttpResponseMessage Respond(CapturedRequest request)
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/v2/glossaries")
                return FakeHttpHandler.Json(JsonSerializer.Serialize(new { glossaries = Existing }));
            if (request.Method == HttpMethod.Post && path == "/v2/glossaries")
            {
                if (CreateStatus != 201) return FakeHttpHandler.Status(CreateStatus, "{\"message\":\"nope\"}");
                using var body = JsonDocument.Parse(request.Body);
                return FakeHttpHandler.Json(JsonSerializer.Serialize(new
                {
                    glossary_id = "new-id",
                    name = body.RootElement.GetProperty("name").GetString(),
                    ready = true,
                    source_lang = "en",
                    target_lang = "pl",
                }), System.Net.HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Delete) return FakeHttpHandler.Status(204);
            if (path == "/v2/translate")
            {
                if (RejectTranslate(request)) return FakeHttpHandler.Status(400, "{\"message\":\"bad glossary\"}");
                using var body = JsonDocument.Parse(request.Body);
                var texts = body.RootElement.GetProperty("text").EnumerateArray()
                    .Select(static e => new { detected_source_language = "EN", text = "PL:" + e.GetString() })
                    .ToArray();
                return FakeHttpHandler.Json(JsonSerializer.Serialize(new { translations = texts }));
            }
            return FakeHttpHandler.Status(404);
        }

        public IReadOnlyList<CapturedRequest> Requests(HttpMethod method, string path) =>
            Handler.Requests.Where(r => r.Method == method && new Uri(r.Url).AbsolutePath == path).ToList();

        public static string? GlossaryIdOf(CapturedRequest translate)
        {
            using var body = JsonDocument.Parse(translate.Body);
            return body.RootElement.TryGetProperty("glossary_id", out var id) ? id.GetString() : null;
        }
    }

    // Glosariusz powstaje w tle, a tłumaczenie czeka na niego najwyżej GlossaryWaitBudget.
    // Te testy sprawdzają treść glosariusza i jego użycie od pierwszej partii, więc dają
    // szybkiej atrapie zapas czasu, żeby wolny serwer CI nie zmieniał wyniku (zachowanie
    // przy wolnym API sprawdza DeepLGlossaryBackgroundTests).
    private static DeepLTranslationProvider Provider(FakeDeepL fake, TimeProvider? clock = null, DeepLOptions? options = null) =>
        new(new HttpClient(fake.Handler), static () => "key:fx",
            options ?? new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromSeconds(10) }, timeProvider: clock);

    [Fact]
    public async Task Bez_terminow_w_partii_nie_ma_glosariusza()
    {
        var fake = new FakeDeepL();

        await Provider(fake).TranslateWithContextAsync(["Hello"], "en", "pl", TranslationContext.Empty);

        var translate = Assert.Single(fake.Handler.Requests);
        Assert.EndsWith("/v2/translate", translate.Url);
        Assert.Null(FakeDeepL.GlossaryIdOf(translate));
    }

    [Fact]
    public async Task Tworzy_glosariusz_raz_i_uzywa_go_w_tlumaczeniach()
    {
        var fake = new FakeDeepL();
        var provider = Provider(fake);

        var result = await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        await provider.TranslateWithContextAsync(["Energy Shield up"], "en", "pl", WithTerms(Terms));

        Assert.Equal(["PL:Find a Waystone"], result);
        Assert.Single(fake.Requests(HttpMethod.Get, "/v2/glossaries"));
        var create = Assert.Single(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
        using (var body = JsonDocument.Parse(create.Body))
        {
            var root = body.RootElement;
            Assert.StartsWith("GameTranslatorOverlay ", root.GetProperty("name").GetString());
            Assert.Equal("en", root.GetProperty("source_lang").GetString());
            Assert.Equal("pl", root.GetProperty("target_lang").GetString());
            Assert.Equal("tsv", root.GetProperty("entries_format").GetString());
            // Kolejność stała (alfabetyczna), niezależna od kolejności w słowniku.
            Assert.Equal("Energy Shield\tTarcza energetyczna\nWaystone\tKamień drogi", root.GetProperty("entries").GetString());
        }
        Assert.Contains("DeepL-Auth-Key", create.Header("Authorization"));

        var translations = fake.Requests(HttpMethod.Post, "/v2/translate");
        Assert.Equal(2, translations.Count);
        Assert.All(translations, t => Assert.Equal("new-id", FakeDeepL.GlossaryIdOf(t)));
    }

    [Fact]
    public async Task Istniejacy_glosariusz_jest_ponownie_uzywany_a_stare_wersje_usuwane()
    {
        var entries = DeepLGlossaryManager.ToTsv(DeepLGlossaryManager.BuildEntries(Terms));
        var name = "GameTranslatorOverlay " + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entries)))[..16];
        var fake = new FakeDeepL();
        fake.Existing.Add(new { glossary_id = "current", name, ready = true, source_lang = "en", target_lang = "pl" });
        fake.Existing.Add(new { glossary_id = "old", name = "GameTranslatorOverlay 0000000000000000", ready = true, source_lang = "en", target_lang = "pl" });
        fake.Existing.Add(new { glossary_id = "foreign", name = "Moje terminy", ready = true, source_lang = "en", target_lang = "pl" });
        fake.Existing.Add(new { glossary_id = "other-pair", name = "GameTranslatorOverlay 1111111111111111", ready = true, source_lang = "en", target_lang = "de" });

        await Provider(fake).TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));

        Assert.Empty(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
        // Sprzątanie działa w tle, po zwróceniu tłumaczenia.
        for (var i = 0; i < 200 && !fake.Handler.Requests.Any(r => r.Method == HttpMethod.Delete); i++)
            await Task.Delay(10);
        var delete = Assert.Single(fake.Handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.EndsWith("/v2/glossaries/old", delete.Url);
        Assert.Equal("current", FakeDeepL.GlossaryIdOf(Assert.Single(fake.Requests(HttpMethod.Post, "/v2/translate"))));
    }

    [Fact]
    public async Task Zmiana_slownika_tworzy_nowy_glosariusz()
    {
        var fake = new FakeDeepL();
        var provider = Provider(fake);

        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl",
            WithTerms([.. Terms, new GlossaryTerm("Rune", "Runa")]));

        Assert.Equal(2, fake.Requests(HttpMethod.Post, "/v2/glossaries").Count);
    }

    [Fact]
    public async Task Blad_glosariusza_nie_blokuje_tlumaczenia_i_wraca_po_przerwie()
    {
        var fake = new FakeDeepL { CreateStatus = 400 };
        var clock = new ManualClock();
        var provider = Provider(fake, clock);

        var result = await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));

        Assert.Equal(["PL:Find a Waystone"], result);
        Assert.Single(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
        Assert.All(fake.Requests(HttpMethod.Post, "/v2/translate"), t => Assert.Null(FakeDeepL.GlossaryIdOf(t)));

        clock.Now += DeepLGlossaryManager.FailureCooldown + TimeSpan.FromSeconds(1);
        fake.CreateStatus = 201;
        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));

        Assert.Equal(2, fake.Requests(HttpMethod.Post, "/v2/glossaries").Count);
        Assert.Equal("new-id", FakeDeepL.GlossaryIdOf(fake.Requests(HttpMethod.Post, "/v2/translate")[^1]));
    }

    [Fact]
    public async Task Odrzucone_tlumaczenie_z_glosariuszem_jest_ponawiane_bez_niego()
    {
        var fake = new FakeDeepL { RejectTranslate = static r => FakeDeepL.GlossaryIdOf(r) is not null };
        var provider = Provider(fake);

        var result = await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));

        Assert.Equal(["PL:Find a Waystone"], result);
        var translations = fake.Requests(HttpMethod.Post, "/v2/translate");
        Assert.Equal(2, translations.Count);
        Assert.Equal("new-id", FakeDeepL.GlossaryIdOf(translations[0]));
        Assert.Null(FakeDeepL.GlossaryIdOf(translations[1]));

        // Po odrzuceniu glosariusz jest chwilowo wyłączony — bez kolejnych nieudanych prób.
        await provider.TranslateWithContextAsync(["Energy Shield"], "en", "pl", WithTerms(Terms));
        Assert.Null(FakeDeepL.GlossaryIdOf(fake.Requests(HttpMethod.Post, "/v2/translate")[^1]));
    }

    [Fact]
    public async Task Wylaczony_glosariusz_nie_wysyla_terminow()
    {
        var fake = new FakeDeepL();

        await Provider(fake, options: new DeepLOptions { UseGlossary = false })
            .TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));

        var translate = Assert.Single(fake.Handler.Requests);
        Assert.Null(FakeDeepL.GlossaryIdOf(translate));
    }

    [Fact]
    public void Wpisy_sa_oczyszczone_bez_duplikatow_i_z_wygrywajacym_priorytetem()
    {
        var entries = DeepLGlossaryManager.BuildEntries(
        [
            new GlossaryTerm("Armour", "Pancerz", Priority: 0),
            new GlossaryTerm("armour", "Zbroja", Priority: 5),
            new GlossaryTerm("  Life\tFlask\n", " Flaszka\tżycia "),
            new GlossaryTerm("   ", "puste"),
            new GlossaryTerm("Mana", ""),
        ]);

        Assert.Equal(
            [("Life Flask", "Flaszka życia"), ("armour", "Zbroja")],
            entries.OrderBy(static e => e.Source, StringComparer.Ordinal).ToList());
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en")]
    [InlineData(" pt-BR ", "pt")]
    [InlineData("pl", "pl")]
    public void Jezyk_glosariusza_bez_wariantu_regionalnego(string input, string expected) =>
        Assert.Equal(expected, DeepLGlossaryManager.GlossaryLanguage(input));
}
