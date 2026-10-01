using GameTranslatorOverlay.Core.Evaluation;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Regresje po przeglądzie harnessu ewaluacji: kontrole nie mogą karać poprawnych tłumaczeń
/// fałszywymi dowodami, CSV musi się otwierać w Excelu, a czas zimnego startu nie może
/// przesuwać mediany i p90.
/// </summary>
public class EvaluationFalseAlarmTests
{
    // ---- Rodzaj ----

    [Theory]
    [InlineData("Z tym pomysłem poszłabym sama.", "f")]
    [InlineData("Uderz go skrzydłem.", "f")]
    [InlineData("Z tym tytułem nie jestem nikim.", "f")]
    [InlineData("Ogółem zebrałam trzy sztuki.", "f")]
    [InlineData("Przełam pieczęć, a potem wróć.", "m")]
    [InlineData("Złam klątwę i wołam cię potem.", "m")]
    [InlineData("Działam szybko, wysyłam posłańca.", "m")]
    [InlineData("Twoim zmysłem i szkłem kotła.", "f")]
    public void Rzeczowniki_rozkazniki_i_czas_terazniejszy_nie_sa_dowodem_rodzaju(string hypothesis, string expectGender)
    {
        Assert.Null(TranslationChecks.CheckGender(hypothesis, expectGender));
    }

    [Theory]
    [InlineData("Z tym pomysłem poszłabym sama.", 1, 0)]
    [InlineData("Zrobiłbym to, gdybym mógł.", 0, 1)]
    [InlineData("Czy poszłabyś ze mną?", 1, 0)]
    [InlineData("Mógłbyś mi pomóc?", 0, 1)]
    [InlineData("Przełam pieczęć.", 0, 0)]
    [InlineData("Poszedłem i umarłem.", 0, 2)]
    [InlineData("Mogłam i zjadłam.", 2, 0)]
    public void Tryb_przypuszczajacy_jest_dowodem_rodzaju(string text, int feminine, int masculine)
    {
        Assert.Equal((feminine, masculine), TranslationChecks.GenderEvidence(text));
    }

    [Fact]
    public void Meski_tryb_przypuszczajacy_przy_oczekiwanym_zenskim_jest_zglaszany()
    {
        var issue = TranslationChecks.CheckGender("Z tym pomysłem poszedłbym sam.", "f");

        Assert.NotNull(issue);
        Assert.Equal(TranslationCheckKind.Gender, issue.Kind);
    }

    // ---- Liczby ----

    [Theory]
    [InlineData("Buy 3 100-gold potions", "Kup 3 mikstury po 100 złota")]
    [InlineData("Collect 5 100-year-old roots", "Zbierz 5 stuletnich korzeni (100 lat)")]
    [InlineData("Level 2 500 XP", "Poziom 2, 500 PD")]
    [InlineData("Reward: 1 000 gold", "Nagroda: 1000 sztuk złota")]
    public void Spacja_miedzy_dwiema_liczbami_nie_daje_falszywego_alarmu(string source, string hypothesis)
    {
        Assert.Null(TranslationChecks.CheckNumbers(source, hypothesis));
    }

    [Fact]
    public void Zmieniona_liczba_obok_spacji_nadal_jest_zglaszana()
    {
        var issue = TranslationChecks.CheckNumbers("Buy 3 100-gold potions", "Kup 4 mikstury po 100 złota");

        Assert.NotNull(issue);
        // Komunikat z interpretacji z mniejszą liczbą różnic: 3 zamiast 4, a nie „3100”.
        Assert.Contains("brakuje 3", issue.Message);
        Assert.Contains("nadmiarowe 4", issue.Message);
        Assert.DoesNotContain("3100", issue.Message);
    }

    // ---- Forma grzecznościowa ----

    [Theory]
    [InlineData("Master the blade before you leave.", "Niech Pan opanuje ostrze, zanim Pan wyjdzie.")]
    [InlineData("Don't miss the boat.", "Niech Pan nie spóźni się na łódź.")]
    [InlineData("The lady wants to see you.", "Pani chce, żeby Pan przyszedł.")]
    public void Dwuznaczne_slowa_w_oryginale_nie_wylaczaja_kontroli(string source, string hypothesis)
    {
        var issue = TranslationChecks.CheckFormalAddress(source, hypothesis);

        Assert.NotNull(issue);
        Assert.Equal(TranslationCheckKind.FormalAddress, issue.Kind);
    }

    [Theory]
    [InlineData("Yes, my lord.", "Tak, panie.")]
    [InlineData("As you wish, my lady.", "Jak sobie życzysz, pani.")]
    [InlineData("Ladies and gentlemen, welcome!", "Panie i panowie, witajcie!")]
    [InlineData("Yes, ma'am.", "Tak, pani.")]
    [InlineData("Your Majesty, the army is ready.", "Panie, armia jest gotowa.")]
    public void Jednoznaczny_zwrot_do_rozmowcy_pozwala_na_pan_pani(string source, string hypothesis)
    {
        Assert.Null(TranslationChecks.CheckFormalAddress(source, hypothesis));
    }

    // ---- CSV ----

    [Theory]
    [InlineData("+15% increased Attack Speed", "'+15% increased Attack Speed")]
    [InlineData("-5 to Strength", "'-5 to Strength")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("@user", "'@user")]
    [InlineData("Nowa gra", "Nowa gra")]
    [InlineData("", "")]
    public void Pole_zaczynajace_sie_jak_formula_dostaje_apostrof(string value, string expected)
    {
        Assert.Equal(expected, EvalReport.ExcelSafe(value));
    }

    [Fact]
    public void Csv_nie_zawiera_pol_czytanych_przez_Excel_jako_formula()
    {
        var line = new EvalLine("item-01", "s", "item", "+15% increased Attack Speed", "+15% zwiększonej szybkości ataku", null, []);
        var run = new EvalProviderRun("Mock", null,
            [new EvalLineResult(line, "+15% szybkości", TranslationOrigin.Provider, null, 10, 50, new TranslationCheckResult(1, []))],
            50, null);

        var row = EvalReport.ToCsv([run]).Split('\n')[1];

        Assert.Contains(",'+15% increased Attack Speed,", row);
        Assert.DoesNotContain(",+15%", row);
    }

    // ---- Czas ----

    [Fact]
    public async Task Pierwsze_zapytanie_nie_wchodzi_do_mediany_i_p90()
    {
        const string jsonl = """
            {"id":"1","scene":"s","kind":"ui","source":"One","reference":"Jeden"}
            {"id":"2","scene":"s","kind":"ui","source":"Two","reference":"Dwa"}
            {"id":"3","scene":"s","kind":"ui","source":"Three","reference":"Trzy"}
            """;

        var run = await EvalRunner.RunAsync(new SlowFirstCallProvider(), EvalCorpus.Parse(jsonl).Lines);

        Assert.NotNull(run.FirstRequestMs);
        Assert.True(run.FirstRequestMs >= 250, $"Pierwsze zapytanie: {run.FirstRequestMs} ms");
        Assert.Equal(2, run.Latency!.Count);
        Assert.True(run.Latency.MaxMs < 250, $"Maksimum bez zimnego startu: {run.Latency.MaxMs} ms");
        // W CSV każda linia nadal ma swój czas, także pierwsza.
        Assert.All(run.Lines, static l => Assert.NotNull(l.LatencyMs));
        Assert.Contains("| 1. zapytanie |", EvalReport.SummaryTable([run]));
    }

    [Fact]
    public async Task Jedyne_zapytanie_jest_i_pierwszym_i_mediana()
    {
        const string jsonl = """{"id":"1","scene":"s","kind":"ui","source":"One","reference":"Jeden"}""";

        var run = await EvalRunner.RunAsync(new MockTranslationProvider(), EvalCorpus.Parse(jsonl).Lines);

        Assert.Equal(1, run.Latency!.Count);
        Assert.Equal(run.FirstRequestMs!.Value, run.Latency.MedianMs, precision: 9);
    }

    private sealed class SlowFirstCallProvider : ITranslationProvider
    {
        private int _calls;

        public string Name => "SlowFirst";
        public bool RequiresApiKey => false;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            // Jak DeepL z glosariuszem: pierwsze wywołanie tworzy glosariusz i połączenie.
            if (Interlocked.Increment(ref _calls) == 1) await Task.Delay(300, cancellationToken);
            return texts.Select(static t => "PL " + t).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }
}
