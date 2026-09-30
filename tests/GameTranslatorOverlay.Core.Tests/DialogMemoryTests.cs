using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class DialogMemoryTests
{
    /// <summary>Dostawca kontekstowy z odpowiedziami ustalanymi w teście: (tekst, numer wywołania) → wynik.</summary>
    internal class ScriptedContextualProvider(Func<string, int, string> respond) : IContextualTranslationProvider
    {
        public List<TranslationContext> Contexts { get; } = [];
        public List<IReadOnlyList<string>> Requests { get; } = [];
        public int CallCount { get; private set; }
        public TranslationException? ThrowOnCall { get; set; }

        public virtual string Name => "Scripted";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) =>
            TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, TranslationContext.Empty, cancellationToken);

        public Task<IReadOnlyList<string>> TranslateWithContextAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            TranslationContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Contexts.Add(context);
            Requests.Add(texts.ToList());
            if (ThrowOnCall is not null) throw ThrowOnCall;
            var call = CallCount;
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => respond(t, call)).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    internal sealed class RetryableContextualProvider(Func<string, int, string> respond)
        : ScriptedContextualProvider(respond), IRetryableTranslationProvider;

    private static (TranslationPipeline Pipeline, InMemoryTranslationCache Cache) Create(
        ITranslationProvider provider, TranslationPipelineOptions? options = null)
    {
        var cache = new InMemoryTranslationCache();
        var pipeline = new TranslationPipeline(
            new GlossaryService(), cache, provider, new UsageTracker(), options ?? new TranslationPipelineOptions());
        return (pipeline, cache);
    }

    private static Func<string, int, string> Polish => static (text, _) => "PL " + text;

    // --- DialogMemory (czysta logika) ---

    [Fact]
    public void Pamiec_trzyma_najwyzej_zadana_liczbe_par_od_najstarszej()
    {
        var memory = new DialogMemory(maxPairs: 2, maxChars: 1000);

        memory.Remember([new("A", "a"), new("B", "b"), new("C", "c")]);

        Assert.Equal([new RecentExchange("B", "b"), new RecentExchange("C", "c")], memory.Excluding([]));
    }

    [Fact]
    public void Pamiec_odrzuca_najstarsze_pary_po_przekroczeniu_limitu_znakow()
    {
        var memory = new DialogMemory(maxPairs: 10, maxChars: 20);

        memory.Remember([new("12345", "12345"), new("abcde", "abcde"), new("xyz", "xyz")]);

        // 10 + 10 + 6 = 26 > 20 — najstarsza para odpada.
        Assert.Equal(["abcde", "xyz"], memory.Excluding([]).Select(static p => p.Source));
    }

    [Fact]
    public void Para_dluzsza_niz_caly_budzet_nie_wypycha_wczesniejszego_kontekstu()
    {
        var memory = new DialogMemory(maxPairs: 6, maxChars: 20);
        memory.Remember([new("Hi", "Cześć")]);

        memory.Remember([new(new string('x', 30), "y")]);

        Assert.Equal([new RecentExchange("Hi", "Cześć")], memory.Excluding([]));
    }

    [Fact]
    public void Powtorzona_linia_przesuwa_sie_na_koniec_z_nowym_tlumaczeniem()
    {
        var memory = new DialogMemory(6, 1000);
        memory.Remember([new("A", "a1"), new("B", "b")]);

        memory.Remember([new("A", "a2")]);

        Assert.Equal([new RecentExchange("B", "b"), new RecentExchange("A", "a2")], memory.Excluding([]));
        Assert.Equal(2, memory.Count);
    }

    [Fact]
    public void Pamiec_pomija_linie_z_biezacej_partii()
    {
        var memory = new DialogMemory(6, 1000);
        memory.Remember([new("A", "a"), new("B", "b")]);

        Assert.Equal([new RecentExchange("B", "b")], memory.Excluding(["A", "Z"]));
    }

    [Fact]
    public void Zerowy_limit_wylacza_pamiec()
    {
        var memory = new DialogMemory(0, 1500);
        memory.Remember([new("A", "a")]);

        Assert.Empty(memory.Excluding([]));
    }

    [Fact]
    public void Podmiana_tlumaczenia_zachowuje_kolejnosc_i_nie_dodaje_nowych_linii()
    {
        var memory = new DialogMemory(6, 1000);
        memory.Remember([new("A", "a"), new("B", "b")]);

        Assert.True(memory.ReplaceTranslation("A", "poprawione"));
        Assert.False(memory.ReplaceTranslation("Nieznana", "x"));
        Assert.False(memory.ReplaceTranslation("B", "  "));

        Assert.Equal([new RecentExchange("A", "poprawione"), new RecentExchange("B", "b")], memory.Excluding([]));
    }

    [Fact]
    public void Dluzsza_korekta_nie_przekracza_budzetu_znakow()
    {
        var memory = new DialogMemory(6, 12);
        memory.Remember([new("A", "a"), new("B", "b")]);

        Assert.True(memory.ReplaceTranslation("B", "bardzo dluga"));

        // A (2) + B (13) = 15 > 12 — najstarsza linia odpada, a sama korekta też się nie mieści.
        Assert.Empty(memory.Excluding([]));
    }

    [Theory]
    [InlineData("Czy jesteś\ngotowa?", "Czy jesteś gotowa?")]
    [InlineData("Czy jesteś gotowa?", "Czy jesteś gotowa?")]
    [InlineData("Czy\njesteś\ngotowa?", "Czy jesteś gotowa?")]
    public void Korekta_ukladana_w_wiersze_wraca_do_jednego_akapitu(string corrected, string expected)
    {
        var (_, plan) = TextReflow.Unwrap("Are you ready to\ngo now?");

        Assert.Equal(expected, TextReflow.ToParagraphs(corrected, plan));
    }

    [Fact]
    public void Korekta_wielu_akapitow_sklada_wiersze_wedlug_planu()
    {
        var plan = new ReflowPlan([2, 1]);

        Assert.Equal("Pierwsze zdanie\nDrugie.", TextReflow.ToParagraphs("Pierwsze\nzdanie\nDrugie.", plan));
        // Inna liczba wierszy niż w planie — wiersze zostają osobno.
        Assert.Equal("Jedno\nDrugie", TextReflow.ToParagraphs("Jedno\n\nDrugie", plan));
        Assert.Equal("A\nB\nC\nD", TextReflow.ToParagraphs("A\r\nB\nC\nD", plan));
    }

    // --- Pipeline ---

    [Fact]
    public async Task Pamiec_zapisuje_surowe_tlumaczenie_przed_przywroceniem_wierszy()
    {
        var provider = new ScriptedContextualProvider(static (text, _) =>
            text == "Are you ready to go now?" ? "Czy jesteś gotowa, żeby już iść?" : "PL " + text);
        var (pipeline, _) = Create(provider);

        var first = Assert.Single(await pipeline.TranslateAsync(["Are you ready to\ngo now?"], "en", "pl"));
        await pipeline.TranslateAsync(["Let's go."], "en", "pl");

        // Na ekranie tłumaczenie ma tyle wierszy co oryginał…
        Assert.Contains('\n', first.TranslatedText!);
        // …a w pamięci leży jeden akapit — to, co model sam zwrócił dla sklejonego tekstu.
        var pair = Assert.Single(provider.Contexts[1].RecentExchanges);
        Assert.Equal(new RecentExchange("Are you ready to go now?", "Czy jesteś gotowa, żeby już iść?"), pair);
        Assert.Equal(["Are you ready to go now?"], provider.Contexts[1].RecentTexts);
    }

    [Fact]
    public async Task Pary_maja_limit_liczby_i_znakow()
    {
        var provider = new ScriptedContextualProvider(Polish);
        var (pipeline, _) = Create(provider, new TranslationPipelineOptions { MaxRecentContextTexts = 3 });

        for (var i = 1; i <= 5; i++) await pipeline.TranslateAsync([$"Line {i}"], "en", "pl");

        Assert.Equal(["Line 2", "Line 3", "Line 4"], provider.Contexts[^1].RecentExchanges.Select(static p => p.Source));
        Assert.Equal(["PL Line 2", "PL Line 3", "PL Line 4"], provider.Contexts[^1].RecentExchanges.Select(static p => p.Translation));

        var byChars = new ScriptedContextualProvider(Polish);
        var (charPipeline, _) = Create(byChars, new TranslationPipelineOptions { MaxRecentContextChars = 40 });
        for (var i = 1; i <= 4; i++) await charPipeline.TranslateAsync([$"Line {i}"], "en", "pl");

        // Para „Line N” + „PL Line N” ma 15 znaków — w 40 mieszczą się dwie.
        Assert.Equal(["Line 2", "Line 3"], byChars.Contexts[^1].RecentExchanges.Select(static p => p.Source));
    }

    [Fact]
    public void Domyslny_budzet_pamieci_to_6_par_i_1500_znakow()
    {
        var options = new TranslationPipelineOptions();

        Assert.Equal(6, options.MaxRecentContextTexts);
        Assert.Equal(1500, options.MaxRecentContextChars);
        Assert.Equal(PlayerGender.Unknown, options.PlayerGender);
    }

    [Fact]
    public async Task Pamiec_pomija_trafienia_z_cache_i_teksty_biezacej_partii()
    {
        var provider = new ScriptedContextualProvider(Polish);
        var (pipeline, cache) = Create(provider);
        await cache.StoreAsync(new NewCacheEntry("From cache", "From cache", "en", "pl", "Z cache", "Scripted",
            Context: TextReflow.FormatVersion));

        await pipeline.TranslateAsync(["Where were you?"], "en", "pl");
        await pipeline.TranslateAsync(["From cache", "I'm ready."], "en", "pl");
        await pipeline.TranslateAsync(["Where were you?", "Let's go."], "en", "pl");

        Assert.Empty(provider.Contexts[0].RecentExchanges);
        Assert.Equal([new RecentExchange("Where were you?", "PL Where were you?")], provider.Contexts[1].RecentExchanges);
        // „Where were you?” jest już w cache — do dostawcy idzie tylko „Let's go.”.
        Assert.Equal(["Let's go."], provider.Requests[2]);
        Assert.Equal(["Where were you?", "I'm ready."], provider.Contexts[2].RecentExchanges.Select(static p => p.Source));
    }

    [Fact]
    public async Task Linia_z_biezacej_partii_nie_trafia_do_kontekstu_jako_gotowa_odpowiedz()
    {
        var provider = new ScriptedContextualProvider(Polish);
        var (pipeline, cache) = Create(provider);
        await pipeline.TranslateAsync(["Hello there", "Other line"], "en", "pl");

        // Ta sama linia znów idzie do dostawcy (np. po wyczyszczeniu cache) — pamięć nie
        // podpowiada modelowi jej starego tłumaczenia.
        await cache.ClearAsync(keepManualCorrections: false);
        await pipeline.TranslateAsync(["Next line", "Hello there"], "en", "pl");

        Assert.Equal(["Other line"], provider.Contexts[^1].RecentExchanges.Select(static p => p.Source));
        Assert.Equal(["Other line"], provider.Contexts[^1].RecentTexts);
    }

    [Fact]
    public async Task Pusty_nieprzetlumaczony_albo_rozgadany_wynik_nie_trafia_do_pamieci()
    {
        const string Echoed = "Where are you going now?";
        var provider = new ScriptedContextualProvider(static (text, _) => text switch
        {
            "Hello there" => "   ",
            Echoed => Echoed,
            "Short one" => string.Join(' ', Enumerable.Repeat("Bardzo długa dygresja modelu", 10)),
            _ => "PL " + text,
        });
        var (pipeline, _) = Create(provider);

        await pipeline.TranslateAsync(["Hello there", Echoed, "Short one", "Good line"], "en", "pl");
        await pipeline.TranslateAsync(["Next"], "en", "pl");

        Assert.Equal([new RecentExchange("Good line", "PL Good line")], provider.Contexts[^1].RecentExchanges);
    }

    [Fact]
    public async Task Zmienione_liczby_nie_wykluczaja_linii_z_pamieci()
    {
        var provider = new ScriptedContextualProvider(static (text, _) =>
            text == "Meet me at 10:30 PM." ? "Spotkajmy się o 22:30." : "PL " + text);
        var (pipeline, _) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Meet me at 10:30 PM."], "en", "pl"));
        await pipeline.TranslateAsync(["Next"], "en", "pl");

        Assert.NotNull(outcome.QualityWarning);
        Assert.Equal("Spotkajmy się o 22:30.", Assert.Single(provider.Contexts[^1].RecentExchanges).Translation);
    }

    [Fact]
    public async Task Po_ponowieniu_pamiec_zawiera_lepszy_wynik()
    {
        const string Gold = "You have 150 gold.";
        var provider = new RetryableContextualProvider(static (text, call) => text switch
        {
            Gold when call == 1 => "Masz trochę złota.",
            Gold => "Masz 150 sztuk złota.",
            _ => "PL " + text,
        });
        var (pipeline, _) = Create(provider);

        var outcome = Assert.Single(await pipeline.TranslateAsync([Gold], "en", "pl"));
        await pipeline.TranslateAsync(["Next"], "en", "pl");

        Assert.Equal("Masz 150 sztuk złota.", outcome.TranslatedText);
        Assert.Equal(new RecentExchange(Gold, "Masz 150 sztuk złota."), Assert.Single(provider.Contexts[^1].RecentExchanges));
    }

    [Fact]
    public async Task Blad_dostawcy_nie_dodaje_nic_do_pamieci()
    {
        var provider = new ScriptedContextualProvider(Polish);
        var (pipeline, _) = Create(provider);
        await pipeline.TranslateAsync(["First"], "en", "pl");

        provider.ThrowOnCall = new TranslationException(TranslationFailureKind.NetworkError, "offline");
        await pipeline.TranslateAsync(["Failed line"], "en", "pl");
        provider.ThrowOnCall = null;
        await pipeline.TranslateAsync(["Next"], "en", "pl");

        Assert.Equal(["First"], provider.Contexts[^1].RecentExchanges.Select(static p => p.Source));
    }

    [Fact]
    public async Task Reczna_korekta_zastepuje_tlumaczenie_w_pamieci()
    {
        var provider = new ScriptedContextualProvider(static (text, _) =>
            text == "Are you ready to go now?" ? "Czy jesteś gotowy, żeby już iść?" : "PL " + text);
        var (pipeline, _) = Create(provider);
        await pipeline.TranslateAsync(["Hi.", "Are you ready to\ngo now?"], "en", "pl");

        // Gracz poprawia wynik tak, jak widzi go na ekranie (w dwóch wierszach).
        Assert.True(pipeline.RememberManualCorrection("Are you ready to\ngo now?", "Czy jesteś gotowa,\nżeby już iść?"));
        await pipeline.TranslateAsync(["Let's go."], "en", "pl");

        Assert.Equal(
            [new RecentExchange("Hi.", "PL Hi."), new RecentExchange("Are you ready to go now?", "Czy jesteś gotowa, żeby już iść?")],
            provider.Contexts[^1].RecentExchanges);
    }

    [Fact]
    public async Task Korekta_tekstu_spoza_pamieci_niczego_nie_dodaje()
    {
        var provider = new ScriptedContextualProvider(Polish);
        var (pipeline, _) = Create(provider);
        await pipeline.TranslateAsync(["Hi."], "en", "pl");

        Assert.False(pipeline.RememberManualCorrection("Never translated", "Nigdy"));
        Assert.False(pipeline.RememberManualCorrection("Hi.", "   "));
        Assert.False(pipeline.RememberManualCorrection("", "x"));
        await pipeline.TranslateAsync(["Next"], "en", "pl");

        Assert.Equal([new RecentExchange("Hi.", "PL Hi.")], provider.Contexts[^1].RecentExchanges);
    }

    [Fact]
    public async Task Plec_gracza_trafia_do_kontekstu_nawet_bez_innych_wskazowek()
    {
        var provider = new ScriptedContextualProvider(Polish);
        var (pipeline, _) = Create(provider, new TranslationPipelineOptions { PlayerGender = PlayerGender.Female });

        await pipeline.TranslateAsync(["Hello there"], "en", "pl");

        var context = Assert.Single(provider.Contexts);
        Assert.Equal(PlayerGender.Female, context.PlayerGender);
        Assert.False(context.IsEmpty);
    }

    [Fact]
    public void Kontekst_z_para_lub_plcia_nie_jest_pusty()
    {
        Assert.True(TranslationContext.Empty.IsEmpty);
        Assert.False((TranslationContext.Empty with { RecentExchanges = [new("A", "a")] }).IsEmpty);
        Assert.False((TranslationContext.Empty with { PlayerGender = PlayerGender.Male }).IsEmpty);
    }
}
