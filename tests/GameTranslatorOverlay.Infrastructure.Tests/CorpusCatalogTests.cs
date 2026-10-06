using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Settings;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class CorpusCatalogTests : IDisposable
{
    private const string Profile = "synthetic-game";
    private const string Sentence = "The lighthouse keeper left a note under the blue lamp.";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static CorpusEntry Entry(string text, CorpusEntryKind kind = CorpusEntryKind.Ui) => new()
    {
        Key = text,
        En = text,
        Kind = kind,
        Source = "synthetic",
    };

    private AppPaths Paths()
    {
        var paths = new AppPaths(_temp.Path);
        paths.EnsureCreated();
        return paths;
    }

    private static void WriteCorpus(AppPaths paths, string profile, params CorpusEntry[] entries)
    {
        Directory.CreateDirectory(paths.CorpusDirectory);
        using var writer = new StreamWriter(Path.Combine(paths.CorpusDirectory, profile + CorpusCatalog.FileSuffix));
        CorpusJsonl.Write(writer, entries);
    }

    [Fact]
    public void Korpus_lezy_w_katalogu_danych_aplikacji()
    {
        var paths = Paths();
        var catalog = CorpusCatalog.CreateDefault(paths);

        Assert.Equal(Path.Combine(_temp.Path, "corpus"), paths.CorpusDirectory);
        Assert.Equal(Path.Combine(_temp.Path, "corpus", "escape-academy.corpus.jsonl"), catalog.PathFor("escape-academy"));
        Assert.Null(catalog.PathFor(null));
        Assert.Null(catalog.PathFor(" "));
        Assert.Null(catalog.PathFor(".."));
        Assert.Null(catalog.PathFor("a/b"));
        Assert.Null(catalog.PathFor(@"..\settings"));
    }

    [Fact]
    public void Brak_pliku_korpusu_to_brak_korpusu_bez_ostrzezenia()
    {
        var result = CorpusCatalog.CreateDefault(Paths()).Load(Profile);

        Assert.Same(CorpusLoadResult.None, result);
        Assert.False(result.IsLoaded);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void Korpus_jest_wczytywany_raz_i_odswiezany_po_zmianie_pliku()
    {
        var paths = Paths();
        WriteCorpus(paths, Profile, Entry("Inspect"), Entry("INSPECT"), Entry(Sentence, CorpusEntryKind.Dialog));
        var catalog = CorpusCatalog.CreateDefault(paths);

        var first = catalog.Load(Profile);
        var again = catalog.Load(Profile);

        Assert.True(first.IsLoaded);
        Assert.Equal(3, first.Entries);
        Assert.Equal(2, first.Texts);
        Assert.Same(first, again);

        WriteCorpus(paths, Profile, Entry("Inspect"), Entry("Pick Up"), Entry("Items"));
        File.SetLastWriteTimeUtc(Path.Combine(paths.CorpusDirectory, Profile + CorpusCatalog.FileSuffix), DateTime.UtcNow.AddMinutes(1));
        var changed = catalog.Load(Profile);

        Assert.NotSame(first, changed);
        Assert.Equal(3, changed.Texts);
    }

    [Fact]
    public void Uszkodzony_korpus_daje_ostrzezenie_bez_tekstow_gry()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.CorpusDirectory);
        File.WriteAllText(Path.Combine(paths.CorpusDirectory, Profile + CorpusCatalog.FileSuffix), "{\"key\":\"a\",\"en\":\"Secret line\"\nnot json");

        var result = CorpusCatalog.CreateDefault(paths).Load(Profile);

        Assert.False(result.IsLoaded);
        Assert.NotNull(result.Issue);
        Assert.DoesNotContain("Secret", result.Issue);
    }

    [Fact]
    public async Task Wpis_z_wyprzedzeniem_w_bazie_trafia_przez_przyciagniety_odczyt()
    {
        var paths = Paths();
        WriteCorpus(paths, Profile, Entry(Sentence, CorpusEntryKind.Dialog), Entry("Items"));
        var corpus = CorpusCatalog.CreateDefault(paths).Load(Profile);
        var cache = new SqliteTranslationCache(paths.DatabasePath);
        cache.Initialize();
        await cache.StoreAsync(new NewCacheEntry(Sentence, CorpusTranslationKey.Normalize(Sentence), "en", "pl",
            "Latarnik zostawił notatkę pod niebieską lampą.", "LLM", GameProfile: Profile,
            Context: TranslationCacheContext.Build(TranslationQualityFlags.None, source: TranslationCacheContext.CorpusSource)));
        var provider = new MockTranslationProvider();
        var usage = new UsageTracker();
        var pipeline = new TranslationPipeline(new GlossaryService(), cache, provider, usage, new TranslationPipelineOptions
        {
            GameProfile = Profile,
            Corpus = corpus.Snapper,
            SplitParagraphs = corpus.IsLoaded,
        });

        var local = await pipeline.TranslateLocalAsync(["The Iighthouse keeper left a note\nunder the blue Iamp."], "en", "pl");

        var outcome = Assert.Single(local);
        Assert.NotNull(outcome);
        Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
        Assert.Equal("Latarnik zostawił notatkę pod niebieską lampą.", outcome.TranslatedText!.Replace('\n', ' '));
        Assert.Equal(2, outcome.TranslatedText.Split('\n').Length);
        Assert.Equal(0, usage.ApiRequests);
    }

    [Fact]
    public void Ustawienie_kluczy_po_akapitach_jest_domyslnie_wylaczone_i_przebudowuje_pipeline()
    {
        var store = new JsonSettingsStore(Paths());
        var settings = new AppSettings();
        var before = settings.PipelineSnapshot();

        Assert.False(settings.ParagraphCacheKeys);
        settings.ParagraphCacheKeys = true;
        Assert.NotEqual(before, settings.PipelineSnapshot());

        store.Save(settings);
        Assert.True(store.Load().ParagraphCacheKeys);
    }
}
