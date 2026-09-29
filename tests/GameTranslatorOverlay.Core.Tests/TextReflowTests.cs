using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TextReflowTests
{
    [Fact]
    public void Zdanie_dialogu_zawiniete_na_wiersze_jest_sklejane()
    {
        var (text, plan) = TextReflow.Unwrap("You should talk to the\nblacksmith before you\nleave the village.");

        Assert.Equal("You should talk to the blacksmith before you leave the village.", text);
        Assert.Equal([3], plan.ParagraphLineCounts);
    }

    [Fact]
    public void Nazwa_wlasna_po_slowie_laczacym_nie_przerywa_zdania()
    {
        var (text, _) = TextReflow.Unwrap("Bring this letter to the\nArchmage in the tower.");

        Assert.Equal("Bring this letter to the Archmage in the tower.", text);
    }

    [Fact]
    public void Pozycje_menu_i_osobne_zdania_zostaja_rozdzielone()
    {
        var (menu, menuPlan) = TextReflow.Unwrap("New Game\nContinue\nOptions\nQuit");
        var (sentences, _) = TextReflow.Unwrap("The door is locked.\nFind the key.");

        Assert.Equal("New Game\nContinue\nOptions\nQuit", menu);
        Assert.False(menuPlan.ChangesLayout);
        Assert.Equal("The door is locked.\nFind the key.", sentences);
    }

    [Theory]
    [InlineData("You need\n3 more keys to open it.", "You need 3 more keys to open it.")]
    [InlineData("+25% increased Armour\nAdds 5 to 10 Fire Damage\n+40 to maximum Life", "+25% increased Armour\nAdds 5 to 10 Fire Damage\n+40 to maximum Life")]
    [InlineData("Level 20\nRequires 45 Str", "Level 20\nRequires 45 Str")]
    [InlineData("Hey! Over here,\nstranger.", "Hey! Over here, stranger.")]
    public void Typowe_teksty_z_gier_sa_sklejane_tylko_przy_zawinieciu(string input, string expected)
    {
        Assert.Equal(expected, TextReflow.Unwrap(input).Text);
    }

    [Fact]
    public void Przeniesienie_wyrazu_jest_scalane_bez_myslnika()
    {
        var (text, _) = TextReflow.Unwrap("Your equip-\nment is broken.");

        Assert.Equal("Your equipment is broken.", text);
    }

    [Fact]
    public void Tlumaczenie_wraca_na_tyle_samo_wierszy_i_zachowuje_akapity()
    {
        var (_, plan) = TextReflow.Unwrap("You should talk to the\nblacksmith before you leave.\nGood luck.");

        var rewrapped = TextReflow.Rewrap("Porozmawiaj z kowalem, zanim wyruszysz w drogę.\nPowodzenia.", plan);

        var lines = rewrapped.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Powodzenia.", lines[2]);
        Assert.Equal("Porozmawiaj z kowalem, zanim wyruszysz w drogę.", $"{lines[0]} {lines[1]}");
    }

    [Fact]
    public void Niezgodna_liczba_akapitow_zostawia_tlumaczenie_bez_zmian()
    {
        var (_, plan) = TextReflow.Unwrap("Talk to the\nsmith.\nGo.");

        Assert.Equal("Jedno zdanie bez podziału.", TextReflow.Rewrap("Jedno zdanie bez podziału.", plan));
    }

    [Theory]
    [InlineData("jeden dwa trzy cztery pięć sześć", 2)]
    [InlineData("Powinieneś porozmawiać z kowalem zanim wyruszysz w daleką drogę na północ", 3)]
    public void WrapBalanced_daje_wiersze_o_podobnej_dlugosci_bez_dzielenia_wyrazow(string text, int lines)
    {
        var wrapped = TextReflow.WrapBalanced(text, lines).Split('\n');

        Assert.Equal(lines, wrapped.Length);
        Assert.Equal(text, string.Join(' ', wrapped));
        Assert.True(wrapped.Max(static l => l.Length) - wrapped.Min(static l => l.Length) <= 12,
            string.Join(" | ", wrapped));
    }

    [Theory]
    [InlineData("l'm ready.", "I'm ready.")]
    [InlineData("Maybe l'll go. l've seen it.", "Maybe I'll go. I've seen it.")]
    [InlineData("| have no idea.", "I have no idea.")]
    [InlineData("Level | 2 | 3", "Level | 2 | 3")]
    [InlineData("lamp l'mao", "lamp l'mao")]
    public void OcrTextRepair_poprawia_tylko_jednoznaczne_pomylki(string input, string expected)
    {
        Assert.Equal(expected, OcrTextRepair.Repair(input));
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public List<string> Sent { get; } = [];
        public Func<string, string> Translate { get; init; } = static t => "PL:" + t;
        public string Name => "Recording";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(texts);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(Translate).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    [Fact]
    public async Task Pipeline_wysyla_sklejone_zdanie_i_zwraca_tlumaczenie_w_wierszach_oryginalu()
    {
        var provider = new RecordingProvider { Translate = static _ => "Porozmawiaj z kowalem, zanim wyruszysz." };
        var cache = new InMemoryTranslationCache();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(), new TranslationPipelineOptions());

        var outcome = (await pipeline.TranslateAsync(["You should talk to the\nblacksmith before you leave."], "en", "pl"))[0];

        Assert.Equal(["You should talk to the blacksmith before you leave."], provider.Sent);
        Assert.Equal(2, outcome.TranslatedText!.Split('\n').Length);
        var cached = await cache.LookupAsync(outcome.NormalizedText, "en", "pl", "");
        Assert.Equal(TextReflow.FormatVersion, cached!.Context);
    }

    [Fact]
    public async Task Stare_wieloliniowe_tlumaczenie_z_cache_jest_tlumaczone_ponownie_a_reczne_zostaje()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Talk to the\nsmith now.", "Talk to the\nsmith now.", "en", "pl", "Porozmawiaj z\nkowal teraz.", "DeepL"));
        await cache.SaveManualCorrectionAsync(new NewCacheEntry("Open the\ngate now.", "Open the\ngate now.", "en", "pl", "Otwórz\nbramę.", "manual"));
        await cache.StoreAsync(new NewCacheEntry("Quit", "Quit", "en", "pl", "Wyjdź", "DeepL"));
        var provider = new RecordingProvider();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, new UsageTracker(), new TranslationPipelineOptions());

        var outcomes = await pipeline.TranslateAsync(["Talk to the\nsmith now.", "Open the\ngate now.", "Quit"], "en", "pl");

        Assert.Equal(["Talk to the smith now."], provider.Sent);
        Assert.Equal("Otwórz\nbramę.", outcomes[1].TranslatedText);
        Assert.Equal("Wyjdź", outcomes[2].TranslatedText);
    }

    [Fact]
    public async Task Stare_tlumaczenie_zostaje_gdy_dostawca_zawiedzie_albo_jest_Cache_only()
    {
        var cache = new InMemoryTranslationCache();
        await cache.StoreAsync(new NewCacheEntry("Talk to the\nsmith now.", "Talk to the\nsmith now.", "en", "pl", "Porozmawiaj z\nkowal.", "DeepL"));
        var failing = new RecordingProvider
        {
            Translate = static _ => throw new TranslationException(TranslationFailureKind.NetworkError, "offline"),
        };
        var online = new TranslationPipeline(new GlossaryService(), cache, failing, new UsageTracker(), new TranslationPipelineOptions());
        var cacheOnly = new TranslationPipeline(new GlossaryService(), cache, failing, new UsageTracker(),
            new TranslationPipelineOptions { CacheOnlyMode = true });

        var afterFailure = (await online.TranslateAsync(["Talk to the\nsmith now."], "en", "pl"))[0];
        var inCacheOnly = (await cacheOnly.TranslateAsync(["Talk to the\nsmith now."], "en", "pl"))[0];

        Assert.Equal("Porozmawiaj z\nkowal.", afterFailure.TranslatedText);
        Assert.Equal(TranslationOrigin.Cache, afterFailure.Origin);
        Assert.Equal("Porozmawiaj z\nkowal.", inCacheOnly.TranslatedText);
        Assert.Single(failing.Sent);
    }
}
