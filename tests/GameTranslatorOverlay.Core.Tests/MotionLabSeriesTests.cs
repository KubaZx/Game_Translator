using System.Text.Json;
using GameTranslatorOverlay.MotionLab.Series;

namespace GameTranslatorOverlay.Core.Tests;

public class MotionLabSeriesMathTests
{
    [Fact]
    public void Fazy_auto_sa_rozne_w_okresie_a_dluzsza_seria_zaczyna_sie_od_tych_samych()
    {
        var five = SeriesMath.GoldenPhases(5, 600);
        var eight = SeriesMath.GoldenPhases(8, 600);

        Assert.Equal([0, 371, 142, 512, 283], five);
        Assert.Equal(five, eight.Take(5));
        Assert.All(eight, p => Assert.InRange(p, 0, 599));
        Assert.Equal(8, eight.Distinct().Count());
        Assert.Equal(5, five.Select(static p => p % 100).Distinct().Count());
    }

    [Fact]
    public void Odosobnione_inne_odczyty_to_tylko_krotkie_odcinki_otoczone_zgodnymi()
    {
        var readings = "AAxxAyAAzzzzA".Select(static c => c == 'A').ToArray();

        var runs = SeriesMath.IsolatedRuns(readings.Length, k => readings[k], 3);

        Assert.Equal([(2, 4), (5, 6)], runs);
        Assert.Empty(SeriesMath.IsolatedRuns(4, k => k >= 2, 3));
        Assert.Empty(SeriesMath.IsolatedRuns(4, k => k <= 1, 3));
        Assert.Equal([(1, 5)], SeriesMath.IsolatedRuns(6, k => k is 0 or 5, 4));
    }

    [Fact]
    public void Podsumowanie_pomija_brakujace_przebiegi_i_liczy_kwartyle_jak_raport()
    {
        var summary = SeriesAggregator.Summarize([3, 1, null, 2, 10]);

        Assert.Equal(4, summary.N);
        Assert.Equal(2.5, summary.Median);
        Assert.Equal(1.75, summary.Q1);
        Assert.Equal(4.75, summary.Q3);
        Assert.Equal(3, summary.Iqr);
        Assert.Equal(1, summary.Min);
        Assert.Equal(10, summary.Max);
        Assert.Equal(4, summary.Mean);
        Assert.Equal(5, summary.Values.Count);
        Assert.Null(summary.Values[2]);
    }

    [Fact]
    public void Mann_Whitney_przy_pelnym_rozdzieleniu_piatek_daje_dokladne_p_i_rozmiar_testu()
    {
        var test = SeriesMath.MannWhitney([1, 2, 3, 4, 5], [6, 7, 8, 9, 10], 0.05);

        Assert.Equal(2.0 / 252, test.P, 9);
        Assert.Equal(8.0 / 252, test.Size, 9);
        Assert.Equal(25, test.U);
    }

    [Fact]
    public void Mann_Whitney_dla_przeplatanych_wartosci_nie_widzi_roznicy()
    {
        var test = SeriesMath.MannWhitney([1, 3, 5, 7, 9], [2, 4, 6, 8, 10], 0.05);

        Assert.Equal(174.0 / 252, test.P, 9);
        Assert.Equal(15, test.U);
    }

    [Fact]
    public void Mann_Whitney_z_remisami_nie_moze_byc_istotny_przy_jednym_odstajacym_przebiegu()
    {
        var test = SeriesMath.MannWhitney([1, 1, 1, 1, 1], [1, 1, 1, 1, 2], 0.05);

        Assert.Equal(1.0, test.P, 9);
        Assert.Equal(0.0, test.Size, 9);
    }

    [Fact]
    public void Mann_Whitney_jest_symetryczny_wzgledem_kolejnosci_serii()
    {
        double[] a = [12, 15, 11, 19, 14, 13];
        double[] b = [18, 21, 16, 22, 17];

        Assert.Equal(SeriesMath.MannWhitney(a, b, 0.05).P, SeriesMath.MannWhitney(b, a, 0.05).P, 12);
    }

    [Fact]
    public void Przesuniecie_Hodgesa_Lehmanna_i_delta_Cliffa_licza_sie_z_par()
    {
        Assert.Equal(1, SeriesMath.HodgesLehmann([1, 2, 3], [2, 3, 4]));
        Assert.Equal(5.0 / 9, SeriesMath.CliffDelta([1, 2, 3], [2, 3, 4]), 9);
        Assert.Equal(-1, SeriesMath.CliffDelta([5, 6], [1, 2]));
    }

    [Fact]
    public void Wilcoxon_dla_samych_dodatnich_roznic_i_pomija_zera()
    {
        var positive = SeriesMath.WilcoxonSignedRank([1, 2, 3, 4, 5, 6]);
        var mixed = SeriesMath.WilcoxonSignedRank([-1, 2, -3, 4, -5, 6]);
        var zeros = SeriesMath.WilcoxonSignedRank([0, 0, 1]);

        Assert.Equal(2.0 / 64, positive.P, 9);
        Assert.Equal(6, positive.Positive);
        Assert.True(mixed.P > 0.5);
        Assert.Equal(1, zeros.N);
        Assert.Equal(2, zeros.Zero);
        Assert.Equal(1.0, zeros.P, 9);
    }

    [Fact]
    public void Przy_braku_roznic_odsetek_falszywych_alarmow_nie_przekracza_rozmiaru_testu()
    {
        var random = new Random(20261016);
        var alarms = 0;
        var expected = 0.0;
        for (var metric = 0; metric < 400; metric++)
        {
            var a = Enumerable.Range(0, 5).Select(_ => random.NextDouble() * 20 + 60).ToList();
            var b = Enumerable.Range(0, 5).Select(_ => random.NextDouble() * 20 + 60).ToList();
            var test = SeriesMath.MannWhitney(a, b, 0.05);
            expected += test.Size;
            if (test.P <= 0.05) alarms++;
        }

        Assert.Equal(400 * 8.0 / 252, expected, 6);
        Assert.InRange(alarms, 0, 25);
    }
}

public class MotionLabSeriesAggregationTests
{
    private static JsonElement Metrics(double coverage, double stale, double? latencyMedian, string list) =>
        JsonDocument.Parse($$"""
        {
          "coverage": { "all": { "percent": {{coverage.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "seconds": 100 } },
          "stale": { "seconds": {{stale.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "longest": [ { "durationMs": 5 } ] },
          "tracks": { "all": 40 },
          "latency": {
            "all": { "median": {{(latencyMedian is { } l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : "\"NaN\"")}}, "count": 3 },
            "list": [{{list}}]
          },
          "recording": "mroczna-latarnia",
          "truth": "prawda.jsonl",
          "worst": [ { "type": "MIGANIE" } ],
          "display": { "fidelity": { "fallback": false } }
        }
        """).RootElement.Clone();

    private const string RunOneLatency = """
        { "trackId": 1, "text": "Latarnia gaśnie", "static": true, "typing": false, "startedInMotion": true, "atStart": false, "startFrame": 40, "latencyMs": 50 },
        { "trackId": 2, "text": "Wyjście", "static": false, "typing": false, "startedInMotion": false, "atStart": false, "startFrame": 90, "latencyMs": 2500 },
        { "trackId": 3, "text": "Start", "static": true, "typing": false, "startedInMotion": false, "atStart": true, "startFrame": 0, "latencyMs": 900 }
        """;

    private const string RunTwoLatency = """
        { "trackId": 1, "text": "Latarnia gaśnie", "static": true, "typing": false, "startedInMotion": true, "atStart": false, "startFrame": 40, "latencyMs": 150 },
        { "trackId": 2, "text": "Wyjście", "static": false, "typing": false, "startedInMotion": false, "atStart": false, "startFrame": 90 }
        """;

    private static SeriesDocument Series(params (int Phase, JsonElement? Metrics)[] runs) =>
        SeriesAggregator.Build("mroczna-latarnia", new DateTime(2026, 10, 16, 12, 0, 0), "test", 600, "auto", ["--placement", "cover"],
            runs.Select((r, i) => (new SeriesRun($"run{i + 1:D2}", r.Phase, r.Metrics is not null, r.Metrics is null ? 4 : 0, 90, null), r.Metrics)).ToList(),
            270);

    [Fact]
    public void Splaszczanie_bierze_liczby_z_obiektow_a_pomija_listy_teksty_i_flagi()
    {
        var flat = MetricValues.Flatten(Metrics(76.5, 8, null, RunOneLatency));

        Assert.Equal(76.5, flat["coverage.all.percent"]);
        Assert.Equal(8, flat["stale.seconds"]);
        Assert.Null(flat["latency.all.median"]);
        Assert.Equal(3, flat["latency.all.count"]);
        Assert.DoesNotContain(flat.Keys, static k => k.StartsWith("worst", StringComparison.Ordinal) || k.StartsWith("truth", StringComparison.Ordinal));
        Assert.DoesNotContain("stale.longest", flat.Keys);
        Assert.DoesNotContain("display.fidelity.fallback", flat.Keys);
    }

    [Theory]
    [InlineData("coverage.cameraMotion.percent", 1)]
    [InlineData("coverage.cameraMotion.seconds", 0)]
    [InlineData("hud.coverage", 1)]
    [InlineData("hud.staticTracks", 0)]
    [InlineData("hud.losses", -1)]
    [InlineData("flicker.holeSeconds", -1)]
    [InlineData("stale.seconds", -1)]
    [InlineData("latency.all.median", -1)]
    [InlineData("latency.all.count", 1)]
    [InlineData("latency.episodes", 0)]
    [InlineData("position.covered.count", 0)]
    [InlineData("position.covered.p90", -1)]
    [InlineData("patches.patchSecondsMotion", 0)]
    [InlineData("cost.ocrCalls", 0)]
    [InlineData("tracks.all", 0)]
    public void Kierunek_lepiej_gorzej_wynika_z_rodzaju_metryki(string path, int direction)
    {
        Assert.Equal(direction, MetricValues.Direction(path));
    }

    [Fact]
    public void Seria_wyrownuje_wartosci_do_przebiegow_i_liczy_skupiska_opoznien_bez_startu_sesji()
    {
        var series = Series((0, Metrics(70, 9, 1275, RunOneLatency)), (371, null), (142, Metrics(74, 7, 150, RunTwoLatency)));

        var coverage = series.Metrics["coverage.all.percent"];
        Assert.Equal(new double?[] { 70, null, 74 }, coverage.Values);
        Assert.Equal(72, coverage.Median);
        Assert.Equal(2, coverage.N);
        Assert.Equal("prawda.jsonl", series.Truth);
        Assert.Equal(270, series.TotalSeconds);

        var all = series.LatencyClusters["all"];
        Assert.Equal(3, all.Events);
        Assert.Equal(1, all.NeverShown);
        Assert.Equal([2, 1], all.EventsByRun);
        Assert.Equal(150, all.Median);
        Assert.Equal(1.5, all.EventsPerRun);
        var motion = series.LatencyClusters["startedInMotion"];
        Assert.Equal([50.0, 150.0], motion.Values);
        var rest = series.LatencyClusters["startedAtRest"];
        Assert.Equal([2500.0], rest.Values);
        Assert.Equal(1, rest.NeverShown);

        Assert.Equal(2, series.LatencyEpisodes.Count);
        var lantern = series.LatencyEpisodes[0];
        Assert.Equal("40|Latarnia gaśnie", lantern.Key);
        Assert.Equal([true, false, true], lantern.Present);
        Assert.Equal(100, lantern.Median);
        var exit = series.LatencyEpisodes[1];
        Assert.Equal(1, exit.NeverShown);
        Assert.Equal(2500, exit.Median);
    }

    [Fact]
    public void Brak_licznika_slownikowego_w_przebiegu_to_zero_a_nie_brak_przebiegu()
    {
        var withPatch = JsonDocument.Parse("""{ "coverage": { "all": { "percent": 80 } }, "problemsByType": { "ŁATKA": { "count": 3 } } }""").RootElement.Clone();
        var without = JsonDocument.Parse("""{ "coverage": { "all": { "percent": 81 } } }""").RootElement.Clone();

        var series = Series((0, without), (371, withPatch), (142, null));
        var patch = series.Metrics["problemsByType.ŁATKA.count"];
        var empty = SeriesAggregator.Build("mroczna-latarnia", new DateTime(2026, 10, 16, 12, 0, 0), "test", 600, "auto", ["--placement", "cover"],
            [(new SeriesRun("run01", 0, true, 0, 90, null), without), (new SeriesRun("run02", 371, true, 0, 90, null), without)], 180);
        var comparison = SeriesComparison.Compare(empty, series, "A", "B", 0.05);

        Assert.Equal(2, patch.N);
        Assert.Equal([0.0, 3.0], patch.Present.Order());
        Assert.NotEqual(SeriesComparison.Missing, comparison.Metrics.Single(static m => m.Path == "problemsByType.ŁATKA.count").Verdict);
    }

    [Fact]
    public void Plik_serii_wraca_z_jsona_bez_strat()
    {
        var series = Series((0, Metrics(70, 9, 1275, RunOneLatency)), (371, Metrics(74, 7, 150, RunTwoLatency)));

        var json = JsonSerializer.Serialize(series, SeriesJson.Options);
        var back = JsonSerializer.Deserialize<SeriesDocument>(json, SeriesJson.Options)!;

        Assert.Equal(series.Metrics.Keys, back.Metrics.Keys);
        Assert.Equal(series.Metrics["stale.seconds"].Values, back.Metrics["stale.seconds"].Values);
        Assert.Equal(series.LatencyEpisodes.Select(static e => e.Key), back.LatencyEpisodes.Select(static e => e.Key));
        Assert.Equal(series.Runs, back.Runs);
        Assert.Contains("\"type\": \"motionlab-series\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Raport_serii_pokazuje_kluczowe_metryki_skupiska_i_epizody()
    {
        var series = Series((0, Metrics(70, 9, 1275, RunOneLatency)), (371, Metrics(74, 7, 150, RunTwoLatency)));

        var markdown = SeriesAggregator.Markdown(series);

        Assert.Contains("| Pokrycie [%] | 72 | 2 (71–73) | 70–74 | 70, 74 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| start w ruchu |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 40 | Latarnia gaśnie |", markdown, StringComparison.Ordinal);
        Assert.Contains("| run02 | 371 | 90 | ok |", markdown, StringComparison.Ordinal);
    }
}

public class MotionLabSeriesComparisonTests
{
    private static SeriesDocument Series(Dictionary<string, double[]> metrics, IReadOnlyList<int>? phases = null, string? truth = "prawda.jsonl",
        IReadOnlyList<LatencyEpisode>? episodes = null)
    {
        var count = metrics.Values.First().Length;
        var runs = Enumerable.Range(0, count)
            .Select(i => new SeriesRun($"run{i + 1:D2}", phases?[i] ?? SeriesMath.GoldenPhases(count, 600)[i], true, 0, 90, null)).ToList();
        var summaries = metrics.ToDictionary(static kv => kv.Key, static kv => SeriesAggregator.Summarize(kv.Value.Select(static v => (double?)v).ToList()),
            StringComparer.Ordinal);
        return new SeriesDocument(SeriesDocument.TypeName, SeriesDocument.FormatVersion, "mroczna-latarnia", DateTime.MinValue, "test", 600, "auto",
            [], truth, runs, 450, summaries, new Dictionary<string, LatencyCluster>(), episodes ?? []);
    }

    private static LatencyEpisode Episode(string key, double? median) =>
        new(key, key, true, true, 5, 0, median, median, median, [true, true, true, true, true], [median, median, median, median, median]);

    [Fact]
    public void Wyrazna_poprawa_pokrycia_i_pogorszenie_nieaktualnych_sa_istotne_a_szum_nie()
    {
        var a = Series(new()
        {
            ["coverage.cameraMotion.percent"] = [70, 71, 72, 73, 74],
            ["stale.seconds"] = [8, 8.5, 9, 9.5, 10],
            ["flicker.holeSeconds"] = [3.5, 9.8, 5, 4, 7],
            ["tracks.all"] = [119, 119, 119, 119, 119],
            ["cost.ocrCalls"] = [75, 74, 76, 73, 77],
        });
        var b = Series(new()
        {
            ["coverage.cameraMotion.percent"] = [80, 81, 82, 83, 84],
            ["stale.seconds"] = [12, 12.5, 13, 13.5, 14],
            ["flicker.holeSeconds"] = [4.2, 8, 6, 3.9, 6.5],
            ["tracks.all"] = [119, 119, 119, 119, 119],
            ["cost.ocrCalls"] = [85, 86, 84, 88, 87],
        });

        var comparison = SeriesComparison.Compare(a, b, "A", "B", 0.05);
        var byPath = comparison.Metrics.ToDictionary(static m => m.Path);

        Assert.Equal(SeriesComparison.Better, byPath["coverage.cameraMotion.percent"].Verdict);
        Assert.Equal(10, byPath["coverage.cameraMotion.percent"].Delta);
        Assert.Equal(2.0 / 252, byPath["coverage.cameraMotion.percent"].P!.Value, 4);
        Assert.Equal(SeriesComparison.Worse, byPath["stale.seconds"].Verdict);
        Assert.Equal(SeriesComparison.WithinSpread, byPath["flicker.holeSeconds"].Verdict);
        Assert.Equal(SeriesComparison.Constant, byPath["tracks.all"].Verdict);
        Assert.Equal(SeriesComparison.Changed, byPath["cost.ocrCalls"].Verdict);
        Assert.Equal(3, comparison.SignificantKey);
        Assert.Equal(4, comparison.ComparedKey);
        Assert.Equal(3, comparison.SignificantAll);
        Assert.Equal(5, comparison.ComparedAll);
        Assert.Equal(Math.Round(4 * 8.0 / 252, 2), comparison.ExpectedFalseAlarmsAll);
        Assert.Equal("coverage.cameraMotion.percent", comparison.Metrics[0].Path);
        Assert.Empty(comparison.Warnings);
    }

    [Fact]
    public void Seria_porownana_sama_ze_soba_nie_ma_istotnych_roznic()
    {
        var a = Series(new()
        {
            ["coverage.all.percent"] = [76.3, 70.1, 74, 79, 72.2],
            ["latency.all.median"] = [259, 911, 600, 300, 850],
        });

        var comparison = SeriesComparison.Compare(a, a, "A", "A", 0.05);

        Assert.Equal(0, comparison.SignificantAll);
        Assert.DoesNotContain(comparison.Metrics, static m => m.Significant);
        Assert.All(comparison.Metrics, static m => Assert.Equal(1.0, m.P!.Value, 6));
    }

    [Fact]
    public void Ostrzega_o_roznych_fazach_prawdzie_i_malej_liczbie_przebiegow()
    {
        var a = Series(new() { ["tracks.all"] = [119, 119, 119] }, [0, 100, 200], "prawda-a.jsonl");
        var b = Series(new() { ["tracks.all"] = [120, 120, 120] }, [0, 371, 142], "prawda-b.jsonl");

        var comparison = SeriesComparison.Compare(a, b, "A", "B", 0.05);

        Assert.Contains(comparison.Warnings, static w => w.StartsWith("Różne fazy startu", StringComparison.Ordinal));
        Assert.Contains(comparison.Warnings, static w => w.StartsWith("Różne pliki prawdy", StringComparison.Ordinal));
        Assert.Contains(comparison.Warnings, static w => w.StartsWith("Inna liczba wierszy prawdy", StringComparison.Ordinal));
        Assert.Contains(comparison.Warnings, static w => w.StartsWith("Mniej niż 5", StringComparison.Ordinal));
        Assert.Equal(SeriesComparison.Undecidable, comparison.Metrics.Single().Verdict);
    }

    [Fact]
    public void Ostrzega_o_innym_trybie_prawdy_przy_tym_samym_pliku()
    {
        var a = Series(new() { ["tracks.all"] = [1, 1, 1, 1, 1] }) with { TruthMode = "plain" };
        var b = a with { TruthMode = "split+smooth" };

        var comparison = SeriesComparison.Compare(a, b, "A", "B", 0.05);

        Assert.Equal(["Różny tryb prawdy: A plain, B split+smooth."], comparison.Warnings);
    }

    [Fact]
    public void Epizody_sa_parowane_po_kluczu_i_testowane_Wilcoxonem()
    {
        var a = Series(new() { ["tracks.all"] = [1, 1, 1, 1, 1] },
            episodes: [Episode("10|a", 2500), Episode("20|b", 2400), Episode("30|c", 2600), Episode("40|d", 2550), Episode("50|e", 2450), Episode("60|f", 2700), Episode("70|g", null)]);
        var b = Series(new() { ["tracks.all"] = [1, 1, 1, 1, 1] },
            episodes: [Episode("10|a", 100), Episode("20|b", 200), Episode("30|c", 150), Episode("40|d", 120), Episode("50|e", 90), Episode("60|f", 300), Episode("80|h", 50)]);

        var pairing = SeriesComparison.Compare(a, b, "A", "B", 0.05).Episodes;

        Assert.Equal(6, pairing.Paired);
        Assert.Equal(6, pairing.FasterInB);
        Assert.Equal(6, pairing.Items.Count);
        Assert.Equal(2.0 / 64, pairing.P, 4);
        Assert.True(pairing.MedianDelta < -2000);
    }

    [Fact]
    public void Raport_porownania_ma_podsumowanie_istotnosci_i_tabele_kluczowych()
    {
        var a = Series(new() { ["coverage.cameraMotion.percent"] = [70, 71, 72, 73, 74] });
        var b = Series(new() { ["coverage.cameraMotion.percent"] = [80, 81, 82, 83, 84] });

        var markdown = SeriesComparison.Markdown(SeriesComparison.Compare(a, b, "baza/pokoj", "nowy/pokoj", 0.05), all: true);

        Assert.Contains("**Istotne różnice: 1 z 1 kluczowych metryk**", markdown, StringComparison.Ordinal);
        Assert.Contains("| Pokrycie: kamera w ruchu [%] (`coverage.cameraMotion.percent`) | 72 [71–73] | 82 [81–83] | 10 | 10 | 1 | 0.0079 | **lepiej** |", markdown, StringComparison.Ordinal);
        Assert.Contains("## Wszystkie metryki", markdown, StringComparison.Ordinal);
    }
}
