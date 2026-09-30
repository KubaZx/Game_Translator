using GameTranslatorOverlay.Core.Evaluation;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class EvaluationCorpusTests
{
    private const string TwoScenes = """
        {"id":"a1","scene":"A","kind":"dialog","source":"I was ready.","reference":"Byłam gotowa.","expect_gender":"f"}

        {"id":"b1","scene":"B","kind":"ui","source":"New Game","reference":"Nowa gra"}
        {"id":"a2","scene":"A","kind":"quest","source":"Find\nthe Waystone.","reference":"Znajdź\nKamień Drogi.","terms":[{"source":"Waystone","target":"Kamień Drogi"}]}
        {"id":"b2","scene":"B","kind":"item","source":"+15% increased Attack Speed","reference":"+15% zwiększonej szybkości ataku"}
        """;

    [Fact]
    public void Parsuje_pola_i_pomija_puste_wiersze()
    {
        var corpus = EvalCorpus.Parse(TwoScenes);

        Assert.Equal(4, corpus.Lines.Count);
        var first = corpus.Lines[0];
        Assert.Equal("a1", first.Id);
        Assert.Equal("dialog", first.Kind);
        Assert.Equal("f", first.ExpectGender);
        var quest = corpus.Lines[2];
        Assert.Equal("Find\nthe Waystone.", quest.Source);
        Assert.Equal(new GlossaryTerm("Waystone", "Kamień Drogi"), Assert.Single(quest.Terms));
        Assert.Single(corpus.AllTerms);
    }

    [Fact]
    public void Kolejnosc_odtwarzania_grupuje_sceny_i_zachowuje_kolejnosc_w_scenie()
    {
        var corpus = EvalCorpus.Parse(TwoScenes);

        Assert.Equal(["a1", "a2", "b1", "b2"], corpus.InReplayOrder().Select(static l => l.Id));
        Assert.Equal(["a1", "a2", "b1"], corpus.InReplayOrder(limit: 3).Select(static l => l.Id));
    }

    [Theory]
    [InlineData("""{"id":"x","scene":"s","kind":"dialog","source":"Hi"}""", "reference")]
    [InlineData("""{"id":"x","scene":"s","kind":"cutscene","source":"Hi","reference":"Cześć"}""", "kind")]
    [InlineData("""{"id":"x","scene":"s","kind":"dialog","source":"Hi","reference":"Cześć","expect_gender":"x"}""", "expect_gender")]
    [InlineData("""{"id":"x","scene":"s","kind":"dialog","source":"Hi","reference":"Cześć","terms":[{"source":"Hi"}]}""", "termin")]
    [InlineData("""{"id":"x", "scene":""", "JSON")]
    public void Bledny_wiersz_podaje_numer_i_przyczyne(string row, string expectedFragment)
    {
        var ex = Assert.Throws<FormatException>(() => EvalCorpus.Parse("\n" + row));

        Assert.Contains("Wiersz 2", ex.Message);
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Powtorzone_id_i_pusty_korpus_sa_bledem()
    {
        const string row = """{"id":"x","scene":"s","kind":"ui","source":"Hi","reference":"Cześć"}""";
        Assert.Contains("powtórzone id", Assert.Throws<FormatException>(() => EvalCorpus.Parse(row + "\n" + row)).Message);
        Assert.Throws<FormatException>(() => EvalCorpus.Parse("\n\n"));
    }

    [Fact]
    public void Przykladowy_korpus_ma_okolo_40_linii_wszystkich_rodzajow()
    {
        var corpus = EvalCorpus.Parse(File.ReadAllText(EvaluationTestPaths.SampleCorpus));

        Assert.InRange(corpus.Lines.Count, 35, 60);
        Assert.All(EvalCorpus.Kinds, kind => Assert.Contains(corpus.Lines, l => l.Kind == kind));
        Assert.Contains(corpus.Lines, static l => l.ExpectGender == "f");
        Assert.Contains(corpus.Lines, static l => l.ExpectGender == "m");
        Assert.Contains(corpus.Lines, static l => l.Source.Contains('\n'));
        Assert.Contains(corpus.Lines, static l => l.Terms.Count > 0);
    }

    [Fact]
    public async Task Runner_z_Mockiem_tlumaczy_wszystko_i_liczy_czas_tylko_dla_dostawcy()
    {
        var corpus = EvalCorpus.Parse(TwoScenes);

        var run = await EvalRunner.RunAsync(new MockTranslationProvider(), corpus.InReplayOrder(),
            new EvalRunnerOptions { Variant = "test" });

        Assert.Equal("Mock", run.Provider);
        Assert.Equal("test", run.Variant);
        Assert.Equal(4, run.TranslatedCount);
        Assert.Equal(0, run.FailedCount);
        // Mock dodaje „[PL] ” do angielskiego tekstu, więc chrF jest niski, ale nie zerowy.
        Assert.InRange(run.CorpusChrF, 0.1, 60);
        Assert.Equal("[PL] New Game", run.Lines.Single(static l => l.Line.Id == "b1").Hypothesis);
        // Sklejanie wierszy i przywracanie ich liczby działa jak w aplikacji.
        var wrapped = run.Lines.Single(static l => l.Line.Id == "a2");
        Assert.Equal(2, TranslationChecks.ParagraphCount(wrapped.Hypothesis));
        Assert.Equal("[PL] Find the Waystone.", wrapped.Hypothesis!.Replace('\n', ' '));
        // Pierwsze zapytanie (zimny start) jest osobno — mediana i p90 liczą pozostałe trzy.
        Assert.Equal(3, run.Latency!.Count);
        Assert.NotNull(run.FirstRequestMs);
        Assert.All(run.Lines, static l => Assert.NotNull(l.LatencyMs));
        // Mock nie używa terminu słownika — kontrola to wykrywa.
        Assert.Contains(run.Lines.Single(static l => l.Line.Id == "a2").Checks!.Issues,
            static i => i.Kind == TranslationCheckKind.Glossary);
    }

    [Fact]
    public async Task Linia_rowna_terminowi_slownika_nie_idzie_do_dostawcy()
    {
        const string jsonl = """
            {"id":"t","scene":"s","kind":"ui","source":"Waystone","reference":"Kamień Drogi","terms":[{"source":"Waystone","target":"Kamień Drogi"}]}
            """;

        var run = await EvalRunner.RunAsync(new MockTranslationProvider(), EvalCorpus.Parse(jsonl).Lines);

        var line = Assert.Single(run.Lines);
        Assert.Equal(TranslationOrigin.Glossary, line.Origin);
        Assert.Equal("Kamień Drogi", line.Hypothesis);
        Assert.Null(line.LatencyMs);
        Assert.Null(run.Latency);
        Assert.Equal(100, run.CorpusChrF, precision: 9);
    }

    [Fact]
    public async Task Blad_dostawcy_liczy_sie_jak_pusta_hipoteza()
    {
        var corpus = EvalCorpus.Parse(TwoScenes);

        var run = await EvalRunner.RunAsync(new FailingProvider(), corpus.Lines);

        Assert.Equal(0, run.TranslatedCount);
        Assert.Equal(4, run.FailedCount);
        Assert.Equal(0, run.CorpusChrF);
        Assert.All(run.Lines, static l => Assert.False(string.IsNullOrEmpty(l.Error)));
        Assert.Null(run.Latency);
    }

    private sealed class FailingProvider : ITranslationProvider
    {
        public string Name => "Failing";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            throw new TranslationException(TranslationFailureKind.NetworkError, "offline");

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(false, "offline"));
    }
}
