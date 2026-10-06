using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.CorpusTool.Translation;

namespace GameTranslatorOverlay.CorpusTool.Tests;

public class TranslateOptionsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "gto-options");
    private static readonly string CacheFile = Path.Combine(Base, "x", "cache.db");
    private static readonly string DataDir = Path.Combine(Base, "dane");

    [Fact]
    public void Minimalne_opcje_i_domyslne_sciezki()
    {
        var options = TranslateOptions.Parse(["translate", "--profile", "escape-academy", "--provider", "LLM"]);

        Assert.Equal("llm", options.Provider);
        Assert.Equal("escape-academy", options.ProfileId);
        Assert.False(options.DryRun);
        Assert.True(options.DeepLGlossary);
        Assert.True(options.LlmPreset);
        Assert.Null(options.LlmServerOptions);
        Assert.Equal(Path.GetFullPath(ExtractOptions.DefaultDataDirectory), options.ResolvedSettingsDirectory);
        Assert.Equal(Path.Combine(options.ResolvedDataDirectory, "cache.db"), options.ResolvedCachePath);
        Assert.Equal(Path.Combine(options.ResolvedDataDirectory, "corpus", "escape-academy.corpus.jsonl"), options.ResolveCorpusPath("escape-academy"));
    }

    [Fact]
    public void Wszystkie_opcje_sa_czytane()
    {
        var options = TranslateOptions.Parse([
            "translate", "--profile", "p", "--provider", "deepl", "--corpus", "c.jsonl", "--cache", CacheFile,
            "--stats", "s.json", "--limit", "10", "--batch", "5", "--parallel", "99", "--player-gender", "female",
            "--kinds", "dialog, subtitle", "--dry-run", "--force", "--skip-cached", "--no-deepl-glossary",
            "--llm-thinking", "disabled", "--llm-effort", "none", "--llm-max-tokens", "512", "--llm-json", "--llm-no-preset",
            "--price-in", "0,15", "--price-out", "0.6", "--price-chars", "20",
        ]);

        Assert.Equal("deepl", options.Provider);
        Assert.Equal(10, options.Limit);
        Assert.Equal(5, options.BatchSize);
        Assert.Equal(16, options.Parallelism);
        Assert.Equal(PlayerGender.Female, options.PlayerGender);
        Assert.Equal(new HashSet<CorpusEntryKind> { CorpusEntryKind.Dialog, CorpusEntryKind.Subtitle }, options.Kinds);
        Assert.True(options.DryRun && options.Force && options.SkipCached && !options.DeepLGlossary && options.LlmJson && !options.LlmPreset);
        Assert.Equal(0.15m, options.PriceInputPerMillion);
        Assert.Equal(0.6m, options.PriceOutputPerMillion);
        Assert.Equal(20m, options.PricePerMillionCharacters);
        Assert.Equal("thinking=disabled, reasoning_effort=none, max_tokens=512, response_format=json_object", options.LlmServerOptions!.Describe());
        Assert.Equal(Path.Combine(Base, "x"), options.ResolvedSettingsDirectory);
        Assert.Equal(Path.GetFullPath("c.jsonl"), options.ResolveCorpusPath("p"));
    }

    [Fact]
    public void Jawny_katalog_danych_wygrywa_z_folderem_bazy_przy_ustawieniach()
    {
        var options = TranslateOptions.Parse(["--profile", "p", "--provider", "mock", "--cache", CacheFile, "--data-dir", DataDir]);

        Assert.Equal(DataDir, options.ResolvedSettingsDirectory);
        Assert.Equal(CacheFile, options.ResolvedCachePath);
    }

    [Theory]
    [InlineData("--profile", "p")]
    [InlineData("--provider", "mock")]
    [InlineData("--profile", "p", "--provider", "azure")]
    [InlineData("--profile", "p", "--provider", "mock", "--limit", "0")]
    [InlineData("--profile", "p", "--provider", "mock", "--player-gender", "x")]
    [InlineData("--profile", "p", "--provider", "mock", "--kinds", "menu")]
    [InlineData("--profile", "p", "--provider", "mock", "--kinds", ",")]
    [InlineData("--profile", "p", "--provider", "mock", "--price-in", "-1")]
    [InlineData("--profile", "p", "--provider", "mock", "--llm-thinking", "a b")]
    [InlineData("--profile", "p", "--provider", "mock", "--profile", "q")]
    [InlineData("--profile", "p", "--provider", "mock", "--cache")]
    [InlineData("--profile", "p", "--provider", "mock", "--cache", "--dry-run")]
    [InlineData("--profile", "p", "--provider", "mock", "--nieznana", "x")]
    public void Bledne_opcje_daja_ArgumentException(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => TranslateOptions.Parse(args));
    }

    [Fact]
    public void Pomoc_nie_wymaga_innych_opcji()
    {
        Assert.True(TranslateOptions.Parse(["translate", "--help"]).Help);
    }
}

public class CorpusTranslationPlannerTests
{
    [Fact]
    public void Unikalne_teksty_sa_liczone_z_wielkoscia_liter_po_zdjeciu_znacznikow()
    {
        var unique = CorpusTranslationPlanner.Unique([
            Corpus.Ui("a", "Rummage"),
            Corpus.Ui("b", "<b>Rummage</b>"),
            Corpus.Ui("c", "RUMMAGE"),
            Corpus.Ui("d", "   "),
            Corpus.Ui("e", "..."),
            Corpus.Dialog("f", "Hello there", "Intro", 1),
        ]);

        Assert.Equal(6, unique.Entries);
        Assert.Equal(["Rummage", "RUMMAGE", "Hello there"], unique.Texts.Select(static t => t.Key));
        Assert.Equal(2, unique.Texts[0].Occurrences);
        Assert.Equal(1, unique.Duplicates);
        Assert.Equal(2, unique.Empty);
        Assert.Equal("Rummage", unique.Texts[0].SourceText);
    }

    [Fact]
    public void Filtr_rodzajow_ogranicza_korpus()
    {
        var unique = CorpusTranslationPlanner.Unique(
            [Corpus.Ui("a", "Rummage"), Corpus.Dialog("f", "Hello there", "Intro", 1)],
            new HashSet<CorpusEntryKind> { CorpusEntryKind.Dialog });

        Assert.Equal(1, unique.Entries);
        Assert.Equal("Hello there", Assert.Single(unique.Texts).Key);
    }

    [Fact]
    public void Partie_dialogow_ida_po_wezlach_w_kolejnosci_linii_i_tworza_lancuch()
    {
        var unique = CorpusTranslationPlanner.Unique([
            Corpus.Dialog("3", "Third line.", "Intro", 3, "Ann"),
            Corpus.Dialog("1", "First line.", "Intro", 1, "Ann"),
            Corpus.Dialog("x", "Other node.", "Outro", 1, "Bob"),
            Corpus.Dialog("2", "Second line.", "Intro", 2, "Bob"),
        ]);

        var batches = CorpusTranslationPlanner.Batches(unique.Texts, 2);

        Assert.Equal(3, batches.Count);
        Assert.Equal(["First line.", "Second line."], batches[0].Texts.Select(static t => t.Key));
        Assert.Equal(["Third line."], batches[1].Texts.Select(static t => t.Key));
        Assert.Equal(batches[0].Chain, batches[1].Chain);
        Assert.Equal([0, 1], new[] { batches[0].Sequence, batches[1].Sequence });
        Assert.NotEqual(batches[0].Chain, batches[2].Chain);
        Assert.Equal("Dialogue \"Intro\"; speakers: Ann, Bob", batches[0].Scene);
        Assert.Equal(["speaker: Ann", "speaker: Bob"], batches[0].Notes);
        Assert.All(batches, static b => Assert.Equal(CorpusEntryKind.Dialog, b.Kind));
    }

    [Fact]
    public void Napisy_sa_sortowane_po_pliku_a_UI_dzielone_w_obrebie_tabeli()
    {
        var unique = CorpusTranslationPlanner.Unique([
            Corpus.Ui("GRD_Title", "Lost Garden", "TableA", "%DRAFT"),
            Corpus.Subtitle("Zed_Laugh", "Ha ha!", speaker: "Zed"),
            Corpus.Ui("Btn_Dig", "Dig", "TableB", "Common | Button caption, verb"),
            Corpus.Subtitle("Ann_Gasp", "Oh!"),
            Corpus.Ui("GRD_Desc", "A quiet place.", "TableA", "END | %fresh tip"),
        ]);

        var batches = CorpusTranslationPlanner.Batches(unique.Texts, 50);

        Assert.Equal(3, batches.Count);
        var subtitles = batches[0];
        Assert.Equal(CorpusEntryKind.Subtitle, subtitles.Kind);
        Assert.Equal(["Oh!", "Ha ha!"], subtitles.Texts.Select(static t => t.Key));
        Assert.Equal(["clip: Ann_Gasp", "speaker: Zed; clip: Zed_Laugh"], subtitles.Notes);
        Assert.Equal("Voice-over subtitles; each line belongs to a separate audio clip; speakers: Zed", subtitles.Scene);
        Assert.Equal(["Lost Garden", "A quiet place."], batches[1].Texts.Select(static t => t.Key));
        Assert.Equal(["key: GRD_Title", "key: GRD_Desc; context: END | tip"], batches[1].Notes);
        Assert.Equal("User interface strings from the table \"TableA\"", batches[1].Scene);
        Assert.Equal(["key: Btn_Dig; context: Common | Button caption, verb"], batches[2].Notes);
        Assert.Equal(3, batches.Select(static b => b.Chain).Distinct().Count());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("%DRAFT", null)]
    [InlineData("%ITEM3; Reviewed by editor", "Reviewed by editor")]
    [InlineData("ZON | %draft", "ZON")]
    [InlineData("  Credits |  A title\nfor the credits. ", "Credits | A title for the credits.")]
    public void Kontekst_kolumny_jest_czyszczony_z_flag_lokalizacji(string? context, string? expected)
    {
        Assert.Equal(expected, CorpusTranslationPlanner.CleanContext(context));
    }

    [Fact]
    public void Notatka_jest_przycinana()
    {
        var note = CorpusTranslationPlanner.Note(Corpus.Ui("k", "Text", context: new string('c', 400)));

        Assert.Equal(CorpusTranslationPlanner.MaxNoteLength, note!.Length);
        Assert.Null(CorpusTranslationPlanner.Note(Corpus.Dialog("d", "Hi", "n", 1)));
    }

    [Theory]
    [InlineData("Press [X] to use {0}.", "Naciśnij [X], aby użyć {0}.", true)]
    [InlineData("Press [X] to use {0}.", "Naciśnij X, aby użyć {0}.", false)]
    [InlineData("Hello {UserName}!", "Cześć, {UserName}!", true)]
    [InlineData("Hello {UserName}!", "Cześć!", false)]
    [InlineData("%s found %d items", "%s znalazł %d przedmiotów", true)]
    [InlineData("%s found %d items", "znalazł przedmioty", false)]
    [InlineData("No markers [REDACTED] here", "Bez znaczników [UTAJNIONE]", true)]
    [InlineData("[B] [B]", "[B]", false)]
    public void Znaczniki_musza_przetrwac_tlumaczenie(string source, string translated, bool expected)
    {
        Assert.Equal(expected, TextMarkers.Preserved(source, translated));
    }
}

public class CorpusTranslationPlanningTests
{
    private static readonly CorpusTranslatorSettings Settings = new() { ProfileId = "game", GameName = "Game", BatchSize = 50 };

    private static GlossaryService Glossary(params string[] terms)
    {
        var glossary = new GlossaryService();
        glossary.LoadDocument(new GlossaryDocument { Name = "test", Terms = terms.Select(static t => new GlossaryTerm(t, t + " PL")).ToList() });
        return glossary;
    }

    [Fact]
    public async Task Klasyfikacja_chroni_korekty_slownik_i_gotowe_wpisy_profilu()
    {
        var store = new FakeCorpusStore();
        store.Add(Corpus.Cached("Manual text", "Korekta", manual: true));
        store.Add(Corpus.Cached("Approved text", "Zatwierdzony", profile: "game", approved: true));
        store.Add(Corpus.Cached("Done text", "Gotowy", profile: "game", context: "reflow-1;src=corpus"));
        store.Add(Corpus.Cached("Flagged text 5", "Oznaczony", profile: "game", context: "reflow-1;qa=numbers;src=corpus"));
        store.Add(Corpus.Cached("Old\nformat", "Stary", profile: "game", context: null));
        store.Add(Corpus.Cached("Global text", "Globalny"));
        store.Add(Corpus.Cached("Mock text", "[PL] Mock text", profile: "game", provider: "Mock"));
        store.Add(Corpus.Cached("Mock global", "[PL] Mock global", provider: "Mock"));
        var entries = new[]
        {
            "Manual text", "Approved text", "Done text", "Flagged text 5", "Old\nformat", "Global text", "Mock text", "Mock global",
            "Health", "Fresh text",
        }.Select((t, i) => Corpus.Ui($"k{i}", t)).ToList();

        var plan = await CorpusTranslationPlanning.PlanAsync(entries, CorpusProviderTraits.For("deepl"), Glossary("Health"), store, Settings);

        Assert.Equal(1, plan.Skipped[CorpusSkip.Glossary]);
        Assert.Equal(1, plan.Skipped[CorpusSkip.Manual]);
        Assert.Equal(1, plan.Skipped[CorpusSkip.Approved]);
        Assert.Equal(1, plan.Skipped[CorpusSkip.Translated]);
        Assert.Equal(0, plan.Skipped[CorpusSkip.Cached]);
        Assert.Equal(["Flagged text 5", "Old\nformat", "Global text", "Mock text", "Mock global", "Fresh text"],
            plan.Batches.SelectMany(static b => b.Texts).Select(static t => t.Key));
        Assert.Equal(1, plan.ShadowingGlobal);
        Assert.Equal(2, plan.ReplacingProfile);
        Assert.Equal(2, plan.ReplacingMock);
        Assert.Equal(new[] { "Flagged text 5", "Mock text", "Old\nformat" }, plan.Replacing.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(6, plan.ToTranslate);
        Assert.Equal(PlayerGender.Unknown, plan.EffectiveGender);
        Assert.Equal("Flagged text 5".Length + "Old format".Length + "Global text".Length + "Mock text".Length + "Mock global".Length + "Fresh text".Length,
            plan.Characters);
    }

    [Fact]
    public async Task Pomijanie_cache_i_wymuszenie_dzialaja_tylko_na_wpisach_automatycznych()
    {
        var store = new FakeCorpusStore();
        store.Add(Corpus.Cached("Global text", "Globalny"));
        store.Add(Corpus.Cached("Done text", "Gotowy", profile: "game", context: "reflow-1;src=corpus"));
        store.Add(Corpus.Cached("Manual text", "Korekta", profile: "game", manual: true));
        var entries = new[] { "Global text", "Done text", "Manual text" }.Select((t, i) => Corpus.Ui($"k{i}", t)).ToList();

        var skipCached = await CorpusTranslationPlanning.PlanAsync(entries, CorpusProviderTraits.For("deepl"), Glossary(), store,
            Settings with { SkipCached = true });
        var forced = await CorpusTranslationPlanning.PlanAsync(entries, CorpusProviderTraits.For("deepl"), Glossary(), store,
            Settings with { Force = true });

        Assert.Equal(0, skipCached.ToTranslate);
        Assert.Equal(1, skipCached.Skipped[CorpusSkip.Cached]);
        Assert.Equal(["Global text", "Done text"], forced.Batches.SelectMany(static b => b.Texts).Select(static t => t.Key));
        Assert.Equal(1, forced.Skipped[CorpusSkip.Manual]);
    }

    [Fact]
    public async Task Inna_plec_gracza_odswieza_wpis_tylko_u_dostawcy_swiadomego_plci()
    {
        var store = new FakeCorpusStore();
        store.Add(Corpus.Cached("Are you ready?", "Gotowy?", profile: "game", context: "reflow-1;pg=m;src=corpus"));
        store.Add(Corpus.Cached("Open the door.", "Otwórz drzwi.", profile: "game", context: "reflow-1;pg=m;src=corpus"));
        var entries = new[] { "Are you ready?", "Open the door." }.Select((t, i) => Corpus.Dialog($"k{i}", t, "n", i)).ToList();
        var female = Settings with { PlayerGender = PlayerGender.Female };

        var llm = await CorpusTranslationPlanning.PlanAsync(entries, CorpusProviderTraits.For("llm"), Glossary(), store, female);
        var deepl = await CorpusTranslationPlanning.PlanAsync(entries, CorpusProviderTraits.For("deepl"), Glossary(), store, female);

        Assert.Equal(["Are you ready?"], llm.Batches.SelectMany(static b => b.Texts).Select(static t => t.Key));
        Assert.Equal(PlayerGender.Female, llm.EffectiveGender);
        Assert.Equal(1, llm.AddressingPlayer);
        Assert.Equal(0, deepl.ToTranslate);
        Assert.Equal(PlayerGender.Unknown, deepl.EffectiveGender);
    }

    [Fact]
    public async Task Limit_przycina_partie_w_kolejnosci_dialogi_napisy_UI()
    {
        var entries = new List<CorpusEntry>
        {
            Corpus.Ui("u1", "Menu one"),
            Corpus.Ui("u2", "Menu two"),
            Corpus.Dialog("d1", "Line one", "n", 1),
            Corpus.Dialog("d2", "Line two", "n", 2),
            Corpus.Subtitle("clip", "Oh no {0}!"),
        };

        var plan = await CorpusTranslationPlanning.PlanAsync(entries, CorpusProviderTraits.For("mock"), Glossary(), cache: null,
            Settings with { Limit = 4 });

        Assert.Equal(["Line one", "Line two", "Oh no {0}!", "Menu one"], plan.Batches.SelectMany(static b => b.Texts).Select(static t => t.Key));
        Assert.Equal(1, plan.BeyondLimit);
        Assert.Equal(4, plan.ToTranslate);
        Assert.Equal(1, plan.WithMarkers);
        Assert.All(plan.Batches, static b => Assert.Equal(b.Texts.Count, b.Notes.Count));
    }

    [Fact]
    public async Task Pusty_korpus_nie_pyta_bazy()
    {
        var store = new FakeCorpusStore();

        var plan = await CorpusTranslationPlanning.PlanAsync([], CorpusProviderTraits.For("mock"), Glossary(), store, Settings);

        Assert.Equal(0, plan.ToTranslate);
        Assert.Empty(plan.Batches);
        Assert.Equal(0, store.PeekCalls);
    }
}

public class CorpusTranslatorTests
{
    private static readonly CorpusTranslatorSettings Settings = new() { ProfileId = "game", GameName = "Game", BatchSize = 2 };

    private static async Task<(CorpusPlan Plan, CorpusRunReport Report)> RunAsync(
        ITranslationProvider provider, IReadOnlyList<CorpusEntry> entries, FakeCorpusStore store, CorpusTranslatorSettings? settings = null,
        string providerId = "llm")
    {
        var effective = settings ?? Settings;
        var glossary = new GlossaryService();
        glossary.LoadDocument(new GlossaryDocument { Name = "g", Terms = [new GlossaryTerm("Vault", "Skarbiec")] });
        var traits = CorpusProviderTraits.Of(provider, CorpusProviderTraits.For(providerId).MaxBatchSize);
        var plan = await CorpusTranslationPlanning.PlanAsync(entries, traits, glossary, store, effective);
        var report = await new CorpusTranslator(provider, glossary, store, effective).RunAsync(plan);
        return (plan, report);
    }

    [Fact]
    public async Task Mock_zapisuje_wpisy_z_profilem_znacznikiem_korpusu_i_przywroconymi_wierszami()
    {
        var store = new FakeCorpusStore();
        var entries = new List<CorpusEntry> { Corpus.Ui("a", "<b>Open</b> the door"), Corpus.Ui("b", "The Silver\nOrchard") };

        var (_, report) = await RunAsync(new MockTranslationProvider(), entries, store, providerId: "mock");

        Assert.True(report.Complete);
        Assert.Equal(2, report.Stored);
        var open = store.Stored.Single(static e => e.NormalizedText == "Open the door");
        Assert.Equal("Open the door", open.SourceText);
        Assert.Equal("[PL] Open the door", open.TranslatedText);
        Assert.Equal("Mock", open.Provider);
        Assert.Equal("game", open.GameProfile);
        Assert.Equal("reflow-1;src=corpus", open.Context);
        Assert.Equal(("en", "pl"), (open.SourceLanguage, open.TargetLanguage));
        Assert.False(open.IsManual || open.IsApproved);
        Assert.Equal("The Silver\nOrchard", store.Stored.Single(static e => e.NormalizedText.StartsWith("The", StringComparison.Ordinal)).NormalizedText);
    }

    [Fact]
    public async Task Dostawca_kontekstowy_dostaje_scene_notatki_slownik_i_poprzednie_linie_wezla()
    {
        var store = new FakeCorpusStore();
        var provider = new GenderAwareRecordingProvider("LLM", static (t, _) => "PL " + t);
        var entries = new List<CorpusEntry>
        {
            Corpus.Dialog("1", "Open the Vault.", "Intro", 1, "Ann"),
            Corpus.Dialog("2", "Are you ready?", "Intro", 2, "Bob"),
            Corpus.Dialog("3", "Let's go.", "Intro", 3, "Ann"),
        };

        var (_, report) = await RunAsync(provider, entries, store, Settings with { PlayerGender = PlayerGender.Female });

        Assert.True(report.Complete);
        Assert.Equal(2, provider.Calls.Count);
        var first = provider.Calls[0].Context;
        Assert.Equal("Game", first.GameName);
        Assert.Equal("Dialogue \"Intro\"; speakers: Ann, Bob", first.Scene);
        Assert.Equal(["speaker: Ann", "speaker: Bob"], first.TextNotes);
        Assert.Equal("Vault", Assert.Single(first.Terms).Source);
        Assert.Equal(PlayerGender.Female, first.PlayerGender);
        Assert.Empty(first.RecentExchanges);
        var second = provider.Calls[1].Context;
        Assert.Equal(["Open the Vault.", "Are you ready?"], second.RecentExchanges.Select(static e => e.Source));
        Assert.Equal("PL Open the Vault.", second.RecentExchanges[0].Translation);
        Assert.Equal(["Open the Vault.", "Are you ready?"], second.RecentTexts);
        Assert.All(store.Stored, static e => Assert.Equal("reflow-1;pg=f;src=corpus", e.Context));
        Assert.All(store.Stored, static e => Assert.Equal("LLM", e.Provider));
    }

    [Fact]
    public async Task Wynik_z_problemem_jakosci_jest_ponawiany_raz_i_zapisywany_jak_w_pipeline()
    {
        var store = new FakeCorpusStore();
        var provider = new GenderAwareRecordingProvider("LLM", static (t, call) => t switch
        {
            "I have 5 apples." when call == 0 => "Mam 3 jabłka.",
            "I have 5 apples." => "Mam 5 jabłek.",
            "Wait 10 seconds." => "Poczekaj chwilę.",
            _ => "PL " + t,
        });
        var entries = new List<CorpusEntry> { Corpus.Ui("a", "I have 5 apples."), Corpus.Ui("b", "Wait 10 seconds.") };

        var (_, report) = await RunAsync(provider, entries, store);

        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(["I have 5 apples.", "Wait 10 seconds."], provider.Calls[1].Texts);
        Assert.Equal(2, report.Retried);
        Assert.Equal(1, report.RetryImproved);
        Assert.Equal(1, report.QualityFlagged);
        Assert.Equal(1, report.QualityFlags["NumbersChanged"]);
        Assert.Equal("Mam 5 jabłek.", store.Stored.Single(static e => e.NormalizedText == "I have 5 apples.").TranslatedText);
        Assert.Equal("reflow-1;src=corpus", store.Stored.Single(static e => e.NormalizedText == "I have 5 apples.").Context);
        Assert.Equal("reflow-1;qa=numbers;src=corpus", store.Stored.Single(static e => e.NormalizedText == "Wait 10 seconds.").Context);
    }

    [Fact]
    public async Task Ponowne_tlumaczenie_oznaczonego_wpisu_bez_poprawy_jest_ostateczne_i_bez_dodatkowego_ponowienia()
    {
        var store = new FakeCorpusStore();
        store.Add(Corpus.Cached("Wait 10 seconds.", "Poczekaj.", profile: "game", context: "reflow-1;qa=numbers;src=corpus"));
        var provider = new GenderAwareRecordingProvider("LLM", static (_, _) => "Poczekaj chwilę.");

        var (_, report) = await RunAsync(provider, [Corpus.Ui("b", "Wait 10 seconds.")], store);

        Assert.Single(provider.Calls);
        Assert.Equal(0, report.Retried);
        Assert.Equal("reflow-1;qa=numbers;qa-final;src=corpus", Assert.Single(store.Stored).Context);
    }

    [Fact]
    public async Task Pusty_wynik_i_zgubione_znaczniki_nie_trafiaja_do_bazy()
    {
        var store = new FakeCorpusStore();
        var provider = new RecordingProvider("DeepL", static (t, _) => t switch
        {
            "Press [X] to jump." => "Naciśnij, aby skoczyć.",
            "Empty one" => "  ",
            _ => "PL " + t,
        });
        var entries = new List<CorpusEntry> { Corpus.Ui("a", "Press [X] to jump."), Corpus.Ui("b", "Empty one"), Corpus.Ui("c", "Fine text") };

        var (_, report) = await RunAsync(provider, entries, store, providerId: "deepl");

        Assert.Equal(1, report.MarkerFailures);
        Assert.Equal(1, report.EmptyResults);
        Assert.Equal(0, report.Retried);
        Assert.Equal("Fine text", Assert.Single(store.Stored).NormalizedText);
        Assert.DoesNotContain("pg=", store.Stored[0].Context);
    }

    [Fact]
    public async Task Blad_klucza_przerywa_caly_przebieg()
    {
        var store = new FakeCorpusStore();
        var provider = new RecordingProvider("DeepL", static (t, _) => t)
        {
            Failure = static _ => new TranslationException(TranslationFailureKind.InvalidApiKey, "DeepL odrzucił klucz API (HTTP 403)."),
        };
        var entries = Enumerable.Range(0, 6).Select(static i => Corpus.Ui($"k{i}", $"Text number {i}", source: $"T{i}")).ToList();

        var (_, report) = await RunAsync(provider, entries, store, Settings with { Parallelism = 1 }, providerId: "deepl");

        Assert.Single(provider.Calls);
        Assert.StartsWith("InvalidApiKey", report.FatalError);
        Assert.False(report.Complete);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Trzy_kolejne_nieudane_partie_przerywaja_a_pojedyncza_nie()
    {
        var entries = Enumerable.Range(0, 6).Select(static i => Corpus.Ui($"k{i}", $"Text number {i}", source: $"T{i}")).ToList();
        var failing = new RecordingProvider("DeepL", static (t, _) => t)
        {
            Failure = static _ => new TranslationException(TranslationFailureKind.ServiceUnavailable, "HTTP 503"),
        };
        var once = new RecordingProvider("DeepL", static (t, _) => "PL " + t)
        {
            Failure = static call => call == 0 ? new TranslationException(TranslationFailureKind.NetworkError, "sieć") : null,
        };

        var (_, aborted) = await RunAsync(failing, entries, new FakeCorpusStore(), Settings with { Parallelism = 1 }, "deepl");
        var store = new FakeCorpusStore();
        var (_, partial) = await RunAsync(once, entries, store, Settings with { Parallelism = 1 }, "deepl");

        Assert.Equal(3, failing.Calls.Count);
        Assert.Contains("3 kolejne partie", aborted.FatalError);
        Assert.Null(partial.FatalError);
        Assert.Equal(1, partial.BatchesFailed);
        Assert.Equal(1, partial.FailedTexts);
        Assert.Equal(5, partial.Stored);
        Assert.False(partial.Complete);
    }

    [Fact]
    public async Task Zla_liczba_tlumaczen_dzieli_partie_a_chronione_wpisy_sa_liczone()
    {
        var store = new FakeCorpusStore { WrittenOverride = static entries => entries.Count - 1 };
        var provider = new CountingMismatchProvider();
        var entries = new List<CorpusEntry> { Corpus.Ui("a", "One text"), Corpus.Ui("b", "Two text"), Corpus.Ui("c", "Three text", source: "Other") };

        var (_, report) = await RunAsync(provider, entries, store, Settings with { Parallelism = 1 }, "mock");

        Assert.Equal(4, provider.Calls);
        Assert.Equal(1, report.Splits);
        Assert.Equal(0, report.BatchesFailed);
        Assert.Equal(0, report.FailedTexts);
        Assert.Equal(3, store.Stored.Count);
        Assert.Equal(1, report.Stored);
        Assert.Equal(2, report.Protected);
    }

    [Fact]
    public async Task Odmowa_dla_jednego_tekstu_traci_tylko_ten_tekst()
    {
        var store = new FakeCorpusStore();
        var refusing = new RefusingProvider("Bad text");
        var entries = new[] { "Good one", "Good two", "Bad text", "Good three" }.Select((t, i) => Corpus.Ui($"k{i}", t)).ToList();

        var (_, report) = await RunAsync(refusing, entries, store, Settings with { BatchSize = 4 }, "llm");

        Assert.Equal(0, report.BatchesFailed);
        Assert.Equal(1, report.FailedTexts);
        Assert.Equal(2, report.Splits);
        Assert.Equal(["Good one", "Good two", "Good three"], store.Stored.Select(static e => e.NormalizedText));
        Assert.Null(report.FatalError);
        Assert.Equal(5, refusing.Calls);
    }

    [Fact]
    public async Task Partia_odrzucona_w_calosci_po_podzialach_jest_bledem_partii()
    {
        var store = new FakeCorpusStore();
        var refusing = new RefusingProvider("text");
        var entries = new[] { "First text", "Second text" }.Select((t, i) => Corpus.Ui($"k{i}", t)).ToList();

        var (_, report) = await RunAsync(refusing, entries, store, Settings with { BatchSize = 2 }, "llm");

        Assert.Equal(3, refusing.Calls);
        Assert.Equal(1, report.BatchesFailed);
        Assert.Equal(2, report.FailedTexts);
        Assert.Empty(store.Stored);
        Assert.Null(report.FatalError);
    }

    private sealed class RefusingProvider(string refused) : ITranslationProvider
    {
        private int _calls;

        public int Calls => _calls;
        public string Name => "LLM";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (texts.Any(t => t.Contains(refused, StringComparison.Ordinal)))
            {
                return Task.FromException<IReadOnlyList<string>>(
                    new TranslationException(TranslationFailureKind.ContentRefused, "Model odmówił przetłumaczenia tekstu."));
            }
            IReadOnlyList<string> result = texts.Select(static t => "PL " + t).ToList();
            return Task.FromResult(result);
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class CountingMismatchProvider : ITranslationProvider
    {
        private int _calls;

        public int Calls => _calls;
        public string Name => "Mock";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            IReadOnlyList<string> result = call == 1 ? texts.Take(1).ToList() : texts.Select(static t => "PL " + t).ToList();
            return Task.FromResult(result);
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }
}

public class CorpusCostEstimateTests
{
    private static readonly CorpusTranslatorSettings Settings = new() { ProfileId = "game", GameName = "Game", BatchSize = 25 };

    private static Task<CorpusPlan> Plan(string provider) => CorpusTranslationPlanning.PlanAsync(
        [Corpus.Dialog("1", "Are you ready to go?", "Intro", 1, "Ann"), Corpus.Dialog("2", "Yes, let us go now.", "Intro", 2, "Bob"), Corpus.Ui("u", "Open the door")],
        CorpusProviderTraits.For(provider), new GlossaryService(), cache: null, Settings);

    [Fact]
    public async Task DeepL_liczy_znaki_i_limit_Free_albo_podana_stawke()
    {
        var plan = await Plan("deepl");

        var free = CorpusCostEstimate.ForDeepL(plan, null);
        var priced = CorpusCostEstimate.ForDeepL(plan, 20m);

        Assert.Equal(plan.Characters, free.Characters);
        Assert.Equal(2, free.Requests);
        Assert.Equal(0m, free.LowUsd);
        Assert.Equal(Math.Round(plan.Characters * 27.50m / 1_000_000m, 4), free.HighUsd);
        Assert.Contains("DeepL API Free", free.Basis);
        Assert.Equal(Math.Round(plan.Characters * 20m / 1_000_000m, 4), priced.LowUsd);
        Assert.Equal(priced.LowUsd, priced.HighUsd);
        Assert.Contains("--price-chars", priced.Basis);
        Assert.Equal(0m, CorpusCostEstimate.ForMock(plan).HighUsd);
    }

    [Fact]
    public async Task LLM_szacuje_tokeny_i_koszt_wedlug_serwera()
    {
        var plan = await Plan("llm");
        var glossary = new GlossaryService();

        var deepSeek = CorpusCostEstimate.ForLlm(plan, Settings, glossary, "api.deepseek.com", local: false, null, null);
        var priced = CorpusCostEstimate.ForLlm(plan, Settings, glossary, "api.openai.com", local: false, 1m, 2m);
        var local = CorpusCostEstimate.ForLlm(plan, Settings, glossary, "localhost:11434", local: true, null, null);
        var unknown = CorpusCostEstimate.ForLlm(plan, Settings, glossary, "openrouter.ai", local: false, null, null);

        Assert.True(deepSeek.InputTokens > 100);
        Assert.True(deepSeek.OutputTokens > 10);
        Assert.True(deepSeek.HighUsd > deepSeek.LowUsd);
        Assert.Contains("deepseek-flash", deepSeek.Basis);
        Assert.Equal(Math.Round(priced.InputTokens!.Value * 1m / 1_000_000m, 4) + Math.Round(priced.OutputTokens!.Value * 2m / 1_000_000m, 4), priced.LowUsd);
        Assert.Equal(0m, local.HighUsd);
        Assert.Null(unknown.LowUsd);
        Assert.Contains("--price-in", unknown.Basis);
        Assert.Equal(deepSeek.InputTokens, unknown.InputTokens);
    }
}
