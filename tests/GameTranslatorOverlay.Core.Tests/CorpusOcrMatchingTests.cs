using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class OcrEditDistanceTests
{
    [Theory]
    [InlineData("ihspect", "inspect", 40)]
    [InlineData("inspecti", "inspect", 30)]
    [InlineData("use 'item", "use item", 30)]
    [InlineData("larnp", "lamp", 30)]
    [InlineData("cloor", "door", 30)]
    [InlineData("vvater", "water", 30)]
    [InlineData("level racks", "level packs", 100)]
    [InlineData("itcm", "item", 50)]
    [InlineData("itam", "item", 100)]
    [InlineData("inspect", "inspect", 0)]
    public void Pomylki_ocr_kosztuja_mniej_niz_zwykla_zmiana(string reading, string corpus, int expected)
    {
        Assert.Equal(expected, OcrEditDistance.Distance(reading, corpus, 500));
    }

    [Fact]
    public void Cyfra_korpusu_musi_sie_zgadzac_a_cyfra_odczytu_moze_byc_tylko_podobna_litera()
    {
        Assert.True(OcrEditDistance.Distance("level 3", "level 2", 500) > 500);
        Assert.True(OcrEditDistance.Distance("leve1", "level", 500) <= OcrEditDistance.DigitConfusion);
        Assert.True(OcrEditDistance.Distance("leve7", "level", 500) > 500);
    }

    [Fact]
    public void Scisle_krawedzie_blokuja_ucinanie_i_doklejanie_zwyklych_liter()
    {
        Assert.Equal(100, OcrEditDistance.Distance("the fir", "the fire", 500));
        Assert.Equal(200, OcrEditDistance.Distance("the fir", "the fire", 500, strictEdges: true));
        Assert.Equal(30, OcrEditDistance.Distance("quit leve", "quit level", 500, strictEdges: true));
    }

    [Fact]
    public void Prefiks_wskazuje_koniec_dopasowanego_poczatku()
    {
        var exact = OcrEditDistance.Prefix("apologies for t", "apologies for talking to you.", 100);
        Assert.Equal(new OcrPrefixResult(0, 15), exact);

        var noisy = OcrEditDistance.Prefix("ild guess you have", "i'd guess you have about twelve minutes.", 100);
        Assert.NotNull(noisy);
        Assert.Equal(OcrEditDistance.StrokeConfusion, noisy.Value.Cost);

        Assert.Null(OcrEditDistance.Prefix("completely different", "i'd guess you have about twelve minutes.", 100));
    }

    [Fact]
    public void Postac_kanoniczna_scala_pomylki_ocr()
    {
        Assert.Equal(OcrEditDistance.Canonical("ihspect"), OcrEditDistance.Canonical("inspect"));
        Assert.Equal(OcrEditDistance.Canonical("larnp"), OcrEditDistance.Canonical("lamp"));
        Assert.True(OcrEditDistance.BagGap(OcrEditDistance.SortedCanonical("use 'item"), OcrEditDistance.SortedCanonical("use item")) <= 1);
        Assert.Equal(0, OcrEditDistance.BagGap("abc", "abc"));
        Assert.Equal(2, OcrEditDistance.BagGap("abcd", "ab"));
    }
}

public class CorpusOcrMatchingTests
{
    internal const string Sorry = "Apologies for talking to you through this speaker, but I had to keep myself out of danger.";
    internal const string Fifteen = "I'd guess you have about twelve minutes before the lamps fade and you forget the island.";
    internal const string Gas = "Look, this hall is slowly filling with a sleepy fog... a way to guard our secrets if you fail.";

    internal static readonly CorpusEntry[] Synthetic =
    [
        E("Inspect"),
        E("Use Item"),
        E("Quit Level"),
        E("Puzzle Packs"),
        E("The Warden"),
        E("Visit"),
        E("Last Played:"),
        E("Red Door"),
        E("Bed Door"),
        E("Edit"),
        E("Level 2"),
        E("The Fire"),
        E("Connect"),
        E("Connecting..."),
        E("Use the brass key to open the cabinet."),
        E("Over here!", CorpusEntryKind.Subtitle),
        E("Take the rod from the shelf.", CorpusEntryKind.Dialog),
        E("The fire is spreading fast, run to the exit!", CorpusEntryKind.Dialog),
        E(Sorry, CorpusEntryKind.Dialog, "Warden"),
        E(Fifteen, CorpusEntryKind.Dialog, "Warden"),
        E(Gas, CorpusEntryKind.Dialog, "Warden"),
        E("Oh, you came! Wonderful. It's rare that I'm this thrilled about a promising pupil from the northern valley.", CorpusEntryKind.Dialog),
        E("Oh, you came! Wonderful. It's rare that I'm this thrilled about a pair of promising pupils from the northern valley.", CorpusEntryKind.Dialog),
        E("You will need 150 keys to open the vault at the end of the hall.", CorpusEntryKind.Dialog),
    ];

    private static CorpusEntry E(string text, CorpusEntryKind kind = CorpusEntryKind.Ui, string? speaker = null) => new()
    {
        Key = text,
        En = text,
        Kind = kind,
        Speaker = speaker,
        Source = "synthetic",
    };

    internal static CorpusSnapper Snapper(CorpusSnapOptions? options = null) => new(CorpusIndex.Build(Synthetic), options);

    [Theory]
    [InlineData("Ihspect", "Inspect")]
    [InlineData("Inspecti", "Inspect")]
    [InlineData("-Visiti", "Visit")]
    [InlineData("Userltem", "Use Item")]
    [InlineData("Use 'Item", "Use Item")]
    [InlineData("Quit Leve", "Quit Level")]
    [InlineData("Puzzle Racks", "Puzzle Packs")]
    [InlineData("Puzzleracks", "Puzzle Packs")]
    [InlineData("Puzzle P,acks", "Puzzle Packs")]
    [InlineData("The Wardem__", "The Warden")]
    [InlineData("TneWarden", "The Warden")]
    [InlineData("Rcd Door", "Red Door")]
    public void Krotka_etykieta_z_pomylka_ocr_trafia_w_etykiete_korpusu(string reading, string expected)
    {
        var match = Snapper().Snap(reading);

        Assert.NotNull(match);
        Assert.Equal(expected, match.Entry.En);
        Assert.Equal(CorpusMatchKind.Fuzzy, match.Kind);
        Assert.True(match.IsLabel);
        Assert.False(match.IsPrefix);
    }

    [Theory]
    [InlineData("Ted Door")]
    [InlineData("Exit")]
    [InlineData("Rod Door")]
    [InlineData("The fir")]
    [InlineData("Connecti")]
    [InlineData("Inspect li")]
    [InlineData("Ovcr here")]
    [InlineData("Uze")]
    [InlineData("Level 3")]
    public void Niejednoznaczna_albo_ryzykowna_etykieta_nie_przyciaga(string reading)
    {
        Assert.Null(Snapper().Snap(reading));
    }

    [Fact]
    public void Cyfra_przyklejona_do_slowa_moze_byc_litera_ale_liczba_nie()
    {
        Assert.Equal("Level 2", Snapper().Snap("Leve1 2")?.Entry.En);
        Assert.Null(Snapper().Snap("Leve1 3"));
    }

    [Fact]
    public void Etykiety_mozna_wylaczyc()
    {
        Assert.Null(Snapper(new CorpusSnapOptions { AllowLabels = false }).Snap("Ihspect"));
    }

    [Theory]
    [InlineData("Apologies for talking to y", Sorry)]
    [InlineData("Ild guess you have about twel", Fifteen)]
    [InlineData("Oh, you came! Wonderful. Itls rare that 11m this thrilled about a promi",
        "Oh, you came! Wonderful. It's rare that I'm this thrilled about a promising pupil from the northern valley.")]
    [InlineData("You will need 150 ke", "You will need 150 keys to open the vault at the end of the hall.")]
    public void Poczatek_linii_dialogu_trafia_w_cala_linie(string reading, string expected)
    {
        var match = Snapper().Snap(reading);

        Assert.NotNull(match);
        Assert.Equal(expected, match.Entry.En);
        Assert.True(match.IsPrefix);
        Assert.Equal(CorpusMatchKind.Fuzzy, match.Kind);
    }

    [Theory]
    [InlineData("Oh, you came! Wonderful. Itls rare that")]
    [InlineData("Apologies for")]
    [InlineData("Use the brass key to op")]
    [InlineData("You will need 15")]
    public void Poczatek_wspolny_krotki_albo_spoza_dialogu_nie_przyciaga(string reading)
    {
        Assert.Null(Snapper().Snap(reading));
    }

    [Fact]
    public void Wieloliniowy_poczatek_dialogu_przyciaga_caly_blok()
    {
        var snap = Snapper().SnapBlock("Look, this hall is slowly filling with a\nsleepy fog.");

        Assert.NotNull(snap.Whole);
        Assert.Equal(Gas, snap.Whole.Entry.En);
        Assert.True(snap.Whole.IsPrefix);
        Assert.True(snap.IsFullyServed);
    }

    [Fact]
    public void Prefiks_tylko_w_ostatnim_wierszu_bloku()
    {
        var snap = Snapper().SnapBlock("Apologies for talking to y\nInspect");

        Assert.DoesNotContain(snap.Segments, static s => s.Match.IsPrefix);
        Assert.Contains(snap.Segments, static s => s.Match.Entry.En == "Inspect");
    }

    [Fact]
    public void Prefiksy_mozna_wylaczyc()
    {
        var withoutPrefixes = Snapper(new CorpusSnapOptions { AllowPrefixes = false });

        Assert.NotEqual(true, withoutPrefixes.Snap("Apologies for talking to y")?.IsPrefix);
        Assert.Equal(0, withoutPrefixes.SnapBlock("Apologies for talking to y").LocallyServedLength);
    }

    [Theory]
    [InlineData("QXTtq WzfYkkk", true)]
    [InlineData("uffuu", true)]
    [InlineData("oxo\nvwvxvnmrzZkkk", true)]
    [InlineData("11/11", true)]
    [InlineData("ZOBRU", true)]
    [InlineData("Inspect", false)]
    [InlineData("Ihspect", false)]
    [InlineData("Last Played: 06.10.2026", false)]
    [InlineData("Completely unrelated sentence about rockets and planets", false)]
    [InlineData("Apologies for talking to y", false)]
    public void Szum_to_krotki_odczyt_bez_slow_korpusu_i_bez_podobienstwa(string text, bool expected)
    {
        Assert.Equal(expected, Snapper().LooksLikeNoise(text));
    }

    [Fact]
    public void Odrzucanie_szumu_mozna_wylaczyc_a_pusty_korpus_nic_nie_odrzuca()
    {
        Assert.False(Snapper(new CorpusSnapOptions { RejectNoise = false }).LooksLikeNoise("QXTtq WzfYkkk"));
        Assert.False(new CorpusSnapper(CorpusIndex.Empty).LooksLikeNoise("QXTtq WzfYkkk"));
    }

    [Fact]
    public void Slownik_korpusu_zna_slowa_z_apostrofem()
    {
        var index = CorpusIndex.Build(Synthetic);

        Assert.True(index.IsWord("i'd"));
        Assert.True(index.IsWord("warden"));
        Assert.False(index.IsWord("wardem"));
        Assert.Equal(["i'd", "guess", "twelve"], CorpusText.Words("I'd guess: TWELVE!").ToArray());
    }
}

public class TranslationPipelineOcrMatchingTests
{
    private const string Profile = "synthetic-game";

    private sealed class RecordingProvider : ITranslationProvider
    {
        public List<string> Sent { get; } = [];
        public string Name => "Recording";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            lock (Sent) Sent.AddRange(texts);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed record Setup(TranslationPipeline Pipeline, RecordingProvider Provider, InMemoryTranslationCache Cache, GlossaryService Glossary);

    private static Setup Create(bool corpus = true)
    {
        var cache = new InMemoryTranslationCache();
        var provider = new RecordingProvider();
        var glossary = new GlossaryService();
        var pipeline = new TranslationPipeline(glossary, cache, provider, new UsageTracker(), new TranslationPipelineOptions
        {
            GameProfile = Profile,
            Corpus = corpus ? CorpusOcrMatchingTests.Snapper() : null,
            SplitParagraphs = corpus,
        });
        return new Setup(pipeline, provider, cache, glossary);
    }

    private static Task StoreCorpus(InMemoryTranslationCache cache, string text, string translation) =>
        cache.StoreAsync(new NewCacheEntry(text, CorpusTranslationKey.Normalize(text), "en", "pl", translation, "Recording",
            GameProfile: Profile, Context: TranslationCacheContext.Build(TranslationQualityFlags.None, source: TranslationCacheContext.CorpusSource)));

    [Fact]
    public async Task Etykieta_z_pomylka_ocr_pokazuje_tlumaczenie_z_korpusu_bez_dostawcy()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, "Inspect", "Zbadaj");

        var local = await setup.Pipeline.TranslateLocalAsync(["Ihspect"], "en", "pl");

        Assert.Equal("Zbadaj", local[0]?.TranslatedText);
        Assert.Empty(setup.Provider.Sent);
    }

    [Fact]
    public async Task Kolejne_odczyty_pisanej_linii_trafiaja_w_ten_sam_klucz_bez_nowych_zapytan()
    {
        var setup = Create();
        var readings = new[]
        {
            "Apologies for talking to y",
            "Apologies for talking to you through this",
            "Apologies for talking to you through this speaker, but I\nhad to ke",
            "Apologies for talking to you through this speaker, but I\nhad to keep myself out of danger.",
        };

        var outcomes = new List<TranslationOutcome>();
        foreach (var reading in readings)
        {
            outcomes.Add(Assert.Single(await setup.Pipeline.TranslateAsync([reading], "en", "pl")));
        }

        Assert.Equal([CorpusOcrMatchingTests.Sorry], setup.Provider.Sent);
        Assert.All(outcomes, static o => Assert.Equal("PL:" + CorpusOcrMatchingTests.Sorry, o.TranslatedText!.Replace('\n', ' ')));
        Assert.Equal(TranslationOrigin.Provider, outcomes[0].Origin);
        Assert.All(outcomes.Skip(1), static o => Assert.Equal(TranslationOrigin.Cache, o.Origin));
        var identities = readings.Select(setup.Pipeline.CorpusIdentity).ToList();
        Assert.NotNull(identities[0]);
        Assert.All(identities, identity => Assert.Equal(identities[0], identity));
        Assert.Equal(outcomes[2].TranslatedText, outcomes[3].TranslatedText);
    }

    [Fact]
    public async Task Poczatek_linii_z_tlumaczeniem_z_wyprzedzeniem_jest_lokalny()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, CorpusOcrMatchingTests.Fifteen, "Masz jakieś dwanaście minut.");

        var local = await setup.Pipeline.TranslateLocalAsync(["Ild guess you have about twel"], "en", "pl");

        Assert.Equal("Masz jakieś dwanaście minut.", local[0]?.TranslatedText);
        Assert.Empty(setup.Provider.Sent);
    }

    [Fact]
    public void Bramka_live_odrzuca_szum_tylko_przy_korpusie()
    {
        var withCorpus = Create();
        var withoutCorpus = Create(corpus: false);

        Assert.False(withCorpus.Pipeline.ShouldTranslateLive("QXTtq WzfYkkk"));
        Assert.False(withCorpus.Pipeline.ShouldTranslateLive("11/11"));
        Assert.True(withCorpus.Pipeline.ShouldTranslateLive("Ihspect"));
        Assert.True(withCorpus.Pipeline.ShouldTranslateLive("Completely unrelated sentence about rockets and planets"));
        Assert.True(withoutCorpus.Pipeline.ShouldTranslateLive("QXTtq WzfYkkk"));
        Assert.True(withoutCorpus.Pipeline.ShouldTranslateLive("11/11"));
        Assert.False(withoutCorpus.Pipeline.IsCorpusNoise("uffuu"));
    }

    [Fact]
    public void Termin_slownika_nie_jest_szumem()
    {
        var setup = Create();
        setup.Glossary.AddTerm(new GlossaryTerm("Zorbo", "Zorbo"));

        Assert.True(setup.Pipeline.ShouldTranslateLive("Zorbo"));
        Assert.False(setup.Pipeline.ShouldTranslateLive("Qworb"));
    }

    [Fact]
    public async Task Linia_szumu_w_bloku_z_etykieta_nie_idzie_do_dostawcy()
    {
        var setup = Create();
        await StoreCorpus(setup.Cache, "Inspect", "Zbadaj");

        var outcome = Assert.Single(await setup.Pipeline.TranslateAsync(["Inspect\nQXTtq WzfYkkk"], "en", "pl"));

        Assert.Empty(setup.Provider.Sent);
        Assert.Equal("Zbadaj\nQXTtq WzfYkkk", outcome.TranslatedText);
    }

    [Fact]
    public void Tozsamosc_korpusu_laczy_rozne_odczyty_tej_samej_etykiety()
    {
        var setup = Create();
        var withoutCorpus = Create(corpus: false);

        var exact = setup.Pipeline.CorpusIdentity("Inspect");
        Assert.NotNull(exact);
        Assert.Equal(exact, setup.Pipeline.CorpusIdentity("Ihspect"));
        Assert.Equal(exact, setup.Pipeline.CorpusIdentity("INSPECT"));
        Assert.NotEqual(exact, setup.Pipeline.CorpusIdentity("Use Item"));
        Assert.Null(setup.Pipeline.CorpusIdentity("Completely unrelated sentence about rockets and planets"));
        Assert.Null(withoutCorpus.Pipeline.CorpusIdentity("Inspect"));
    }

    [Fact]
    public void Klucz_nakladki_jest_wspolny_dla_odczytow_o_tej_samej_tozsamosci()
    {
        var setup = Create();
        var shorter = new TextBlock("Apologies for talking to y", new RectPx(10, 10, 300, 30), []);
        var longer = new TextBlock("Apologies for talking to you through this speaker, but I\nhad to ke", new RectPx(10, 10, 600, 70), []);
        var other = new TextBlock("Ihspect", new RectPx(10, 300, 100, 30), []);

        var first = LiveBlockKeyer.AssignKeys([shorter, other], setup.Pipeline.CorpusIdentity);
        var second = LiveBlockKeyer.AssignKeys([longer, other], setup.Pipeline.CorpusIdentity);
        var plain = LiveBlockKeyer.AssignKeys([shorter]);

        Assert.Equal(first[0].Key, second[0].Key);
        Assert.Equal(first[1].Key, second[1].Key);
        Assert.NotEqual(first[0].Key, plain[0].Key);
        Assert.Equal("Apologies for talking to y", first[0].NormalizedText);
    }

    [Fact]
    public void Niedokonczona_kwestia_pisana_literami_jest_rozpoznawana_przed_przetlumaczeniem()
    {
        var setup = Create();
        var bare = Create(corpus: false);

        Assert.True(setup.Pipeline.IsCorpusPrefix("Take the rod"));
        Assert.True(setup.Pipeline.IsCorpusPrefix("The fire is spreading"));
        Assert.True(setup.Pipeline.IsCorpusPrefix("The fire is spreading fast, run to the"));
        Assert.False(setup.Pipeline.IsCorpusPrefix("Take the rod from the shelf."));
        Assert.False(setup.Pipeline.IsCorpusPrefix("The fire is spreading fast, run to the exit!"));
        Assert.False(setup.Pipeline.IsCorpusPrefix("Inspect"));
        Assert.False(setup.Pipeline.IsCorpusPrefix("Completely unrelated sentence about rockets"));
        Assert.False(bare.Pipeline.IsCorpusPrefix("Take the rod"));
    }
}

public class PrefixLayoutTests
{
    [Fact]
    public void Poczatek_dialogu_w_dwoch_wierszach_dostaje_przewidywana_liczbe_wierszy()
    {
        var laidOut = TranslationUnitPlanner.ToPrefixLayout(
            "Patrz, ta sala powoli wypełnia się sennym dymem... tak strzeżemy naszych tajemnic, gdy ktoś zawiedzie.",
            CorpusOcrMatchingTests.Gas,
            "Look, this hall is slowly filling with a\nsleepy fog.");

        Assert.Equal(3, laidOut.Split('\n').Length);
    }

    [Fact]
    public void Jeden_wiersz_poczatku_zostaje_w_ukladzie_odczytu_a_pelny_odczyt_daje_ten_sam_uklad()
    {
        const string translation = "Wybacz, że mówię do ciebie przez ten głośnik, ale musiałem trzymać się z dala od niebezpieczeństwa.";
        var single = TranslationUnitPlanner.ToPrefixLayout(translation, CorpusOcrMatchingTests.Sorry, "Apologies for talking to y");
        var growing = TranslationUnitPlanner.ToPrefixLayout(translation, CorpusOcrMatchingTests.Sorry,
            "Apologies for talking to you through this speaker, but I\nhad to ke");
        var complete = TranslationUnitPlanner.ToScreenLayout(translation, CorpusOcrMatchingTests.Sorry,
            "Apologies for talking to you through this speaker, but I\nhad to keep myself out of danger.");

        Assert.Equal(translation, single);
        Assert.Equal(complete, growing);
    }

    [Fact]
    public void Jednostka_prefiksu_uzywa_ukladu_prefiksu()
    {
        var unit = new TranslationUnit(CorpusOcrMatchingTests.Gas, "Look, this hall is slowly filling with a\nsleepy fog.",
            string.Empty, Literal: false, FromCorpus: true, Prefix: true);

        Assert.Equal(3, TranslationUnitPlanner.Display(unit, "Patrz, ta sala powoli wypełnia się sennym dymem... tak strzeżemy naszych tajemnic, gdy ktoś zawiedzie.").Split('\n').Length);
    }
}
