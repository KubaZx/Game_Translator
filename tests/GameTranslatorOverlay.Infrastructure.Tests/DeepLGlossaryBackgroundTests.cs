using System.Diagnostics;
using System.Text.Json;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// Przygotowanie glosariusza DeepL (lista + utworzenie) działa w tle: wolne albo niedostępne
/// API glosariuszy nie może opóźnić napisu dłużej niż GlossaryWaitBudget, a równoległe
/// tłumaczenia nie czekają na siebie nawzajem.
/// </summary>
public class DeepLGlossaryBackgroundTests
{
    private static readonly GlossaryTerm[] Terms =
    [
        new("Waystone", "Kamień drogi"),
        new("Energy Shield", "Tarcza energetyczna"),
    ];

    private static TranslationContext WithTerms(params GlossaryTerm[] terms) =>
        new(null, terms) { GlossaryTerms = terms };

    /// <summary>
    /// Atrapa DeepL, w której lista glosariuszy może wisieć (do zwolnienia bramki albo
    /// anulowania zapytania) albo zwracać błąd. Tłumaczenie odpowiada od razu.
    /// </summary>
    private sealed class SlowDeepL
    {
        private readonly TaskCompletionSource _listGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listCalls;

        public bool BlockList { get; set; }
        public bool BlockOnlyFirstList { get; set; }
        public int ListStatus { get; set; } = 200;
        public TimeSpan ListDelay { get; set; } = TimeSpan.Zero;
        public FakeHttpHandler Handler { get; }

        public SlowDeepL()
        {
            Handler = new FakeHttpHandler((request, _) => RespondAsync(request));
        }

        public void ReleaseList() => _listGate.TrySetResult();

        private async Task<HttpResponseMessage> RespondAsync(CapturedRequest request)
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/v2/glossaries")
            {
                var call = Interlocked.Increment(ref _listCalls);
                if (BlockList && (!BlockOnlyFirstList || call == 1))
                    await _listGate.Task.WaitAsync(request.CancellationToken);
                if (ListDelay > TimeSpan.Zero)
                    await Task.Delay(ListDelay, request.CancellationToken);
                if (ListStatus != 200)
                {
                    var failure = FakeHttpHandler.Status(ListStatus, "{\"message\":\"busy\"}");
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return failure;
                }
                return FakeHttpHandler.Json("{\"glossaries\":[]}");
            }
            if (request.Method == HttpMethod.Post && path == "/v2/glossaries")
            {
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
            if (path == "/v2/translate")
            {
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

    private static DeepLTranslationProvider Provider(SlowDeepL fake, DeepLOptions options) =>
        new(new HttpClient(fake.Handler), static () => "key:fx", options);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Warunek nie został spełniony w czasie testu.");
    }

    [Fact]
    public async Task Wolna_lista_glosariuszy_nie_opoznia_tlumaczenia_ponad_budzet()
    {
        var fake = new SlowDeepL { BlockList = true };
        var provider = Provider(fake, new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromMilliseconds(200) });

        var stopwatch = Stopwatch.StartNew();
        var result = await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        stopwatch.Stop();

        Assert.Equal(["PL:Find a Waystone"], result);
        // Budżet 200 ms + zapas na wolny serwer CI; wcześniej tłumaczenie czekało na całą listę.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Tłumaczenie trwało {stopwatch.Elapsed}.");
        Assert.Null(SlowDeepL.GlossaryIdOf(Assert.Single(fake.Requests(HttpMethod.Post, "/v2/translate"))));
        fake.ReleaseList();
    }

    [Fact]
    public async Task Blad_503_listy_nie_jest_ponawiany_i_wlacza_przerwe()
    {
        var fake = new SlowDeepL { ListStatus = 503 };
        var provider = Provider(fake, new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromSeconds(5) });

        var stopwatch = Stopwatch.StartNew();
        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        stopwatch.Stop();
        await provider.TranslateWithContextAsync(["Energy Shield"], "en", "pl", WithTerms(Terms));

        // Bez ponowień (Retry-After: 1 s kosztowałby sekundy); po błędzie przerwa bez kolejnych prób.
        Assert.Single(fake.Requests(HttpMethod.Get, "/v2/glossaries"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Tłumaczenie trwało {stopwatch.Elapsed}.");
        Assert.All(fake.Requests(HttpMethod.Post, "/v2/translate"), static t => Assert.Null(SlowDeepL.GlossaryIdOf(t)));
    }

    [Fact]
    public async Task Lista_glosariuszy_ma_wlasny_krotki_limit_czasu()
    {
        var fake = new SlowDeepL { BlockList = true };
        var provider = Provider(fake, new DeepLOptions
        {
            GlossaryWaitBudget = TimeSpan.FromSeconds(10),
            GlossaryRequestTimeout = TimeSpan.FromMilliseconds(200),
        });

        var stopwatch = Stopwatch.StartNew();
        var result = await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        stopwatch.Stop();

        // Ogólny limit zapytania to 15 s — glosariusz rezygnuje po swoim, krótszym.
        Assert.Equal(["PL:Find a Waystone"], result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Tłumaczenie trwało {stopwatch.Elapsed}.");
        Assert.Null(SlowDeepL.GlossaryIdOf(Assert.Single(fake.Requests(HttpMethod.Post, "/v2/translate"))));
        fake.ReleaseList();
    }

    [Fact]
    public async Task Kolejne_tlumaczenie_uzywa_glosariusza_przygotowanego_w_tle()
    {
        var fake = new SlowDeepL { BlockList = true };
        var options = new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromMilliseconds(50) };
        var provider = Provider(fake, options);

        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        fake.ReleaseList();
        await WaitUntil(() => fake.Requests(HttpMethod.Post, "/v2/glossaries").Count == 1);
        options.GlossaryWaitBudget = TimeSpan.FromSeconds(10);
        await provider.TranslateWithContextAsync(["Energy Shield"], "en", "pl", WithTerms(Terms));

        var translations = fake.Requests(HttpMethod.Post, "/v2/translate");
        Assert.Null(SlowDeepL.GlossaryIdOf(translations[0]));
        Assert.Equal("new-id", SlowDeepL.GlossaryIdOf(translations[1]));
        Assert.Single(fake.Requests(HttpMethod.Get, "/v2/glossaries"));
        Assert.Single(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
    }

    [Fact]
    public async Task Rownolegle_tlumaczenia_nie_czekaja_na_przygotowanie_glosariusza()
    {
        var fake = new SlowDeepL { BlockList = true };
        var provider = Provider(fake, new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromMilliseconds(100) });

        var calls = Enumerable.Range(0, 8)
            .Select(i => provider.TranslateWithContextAsync([$"Waystone {i}"], "en", "pl", WithTerms(Terms)))
            .ToArray();
        // Lista wciąż wisi, a wszystkie tłumaczenia są gotowe — nikt nie stoi w kolejce.
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(8, results.Length);
        Assert.Single(fake.Requests(HttpMethod.Get, "/v2/glossaries"));
        Assert.Empty(fake.Requests(HttpMethod.Post, "/v2/glossaries"));

        fake.ReleaseList();
        await WaitUntil(() => fake.Requests(HttpMethod.Post, "/v2/glossaries").Count == 1);
        await Task.Delay(100);
        Assert.Single(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
    }

    [Fact]
    public async Task Wspolbiezne_wywolania_tworza_dokladnie_jeden_glosariusz()
    {
        var fake = new SlowDeepL { ListDelay = TimeSpan.FromMilliseconds(100) };
        var provider = Provider(fake, new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromSeconds(10) });

        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => provider.TranslateWithContextAsync([$"Waystone {i}"], "en", "pl", WithTerms(Terms))));

        Assert.Single(fake.Requests(HttpMethod.Get, "/v2/glossaries"));
        Assert.Single(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
        Assert.All(fake.Requests(HttpMethod.Post, "/v2/translate"), static t => Assert.Equal("new-id", SlowDeepL.GlossaryIdOf(t)));
    }

    [Fact]
    public async Task Zmiana_slownika_przerywa_stare_przygotowanie()
    {
        var fake = new SlowDeepL { BlockList = true, BlockOnlyFirstList = true };
        var options = new DeepLOptions { GlossaryWaitBudget = TimeSpan.FromMilliseconds(50) };
        var provider = Provider(fake, options);

        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl", WithTerms(Terms));
        options.GlossaryWaitBudget = TimeSpan.FromSeconds(10);
        await provider.TranslateWithContextAsync(["Find a Waystone"], "en", "pl",
            WithTerms([.. Terms, new GlossaryTerm("Rune", "Runa")]));

        var lists = fake.Requests(HttpMethod.Get, "/v2/glossaries");
        Assert.Equal(2, lists.Count);
        Assert.True(lists[0].CancellationToken.IsCancellationRequested);
        var create = Assert.Single(fake.Requests(HttpMethod.Post, "/v2/glossaries"));
        Assert.Contains("Rune", create.Body);
        Assert.Equal("new-id", SlowDeepL.GlossaryIdOf(fake.Requests(HttpMethod.Post, "/v2/translate")[^1]));
    }
}
