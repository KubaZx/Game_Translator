using GameTranslatorOverlay.Core.Evaluation;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class EvaluationReportTests
{
    private static EvalLine Line(string id, string source, string reference) =>
        new(id, "scene", "dialog", source, reference, null, []);

    private static EvalLineResult Result(EvalLine line, string? hypothesis, double chrF, params TranslationCheckIssue[] issues) =>
        new(line, hypothesis, hypothesis is null ? TranslationOrigin.Unavailable : TranslationOrigin.Provider,
            hypothesis is null ? "Brak połączenia" : null, hypothesis is null ? null : 120.0, chrF,
            hypothesis is null ? null : new TranslationCheckResult(1.1, issues));

    private static EvalProviderRun SampleRun()
    {
        var lines = new[]
        {
            Result(Line("ok", "New Game", "Nowa gra"), "Nowa gra", 100),
            Result(Line("bad", "A | B\nC", "X, \"Y\""), "Pan | wie\nC", 12.5,
                new TranslationCheckIssue(TranslationCheckKind.FormalAddress, "Forma grzecznościowa zamiast „ty”: Pan.")),
            Result(Line("fail", "Continue", "Kontynuuj"), null, 0),
        };
        return new EvalProviderRun("DeepL", "v2", lines, 42.25, new LatencySummary(2, 120, 180, 200, 120));
    }

    [Fact]
    public void Tabela_zbiorcza_zawiera_linie_chrF_czasy_i_liczniki_kontroli()
    {
        var table = EvalReport.SummaryTable([SampleRun(), EvalProviderRun.Skip("azure", "v2", "brak zmiennej środowiskowej GTO_AZURE_KEY.")]);

        var rows = table.TrimEnd().Split('\n');
        Assert.Equal(3, rows.Length); // nagłówek, separator, jeden uruchomiony dostawca
        Assert.Equal("| DeepL | v2 | 2/3 | 42.3 | 120 ms | 180 ms | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 |", rows[2].TrimEnd('\r'));
    }

    [Fact]
    public void Najgorsze_linie_najpierw_bledy_potem_najnizszy_chrF()
    {
        var worst = EvalReport.WorstLines(SampleRun(), 2);

        Assert.Equal(["fail", "bad"], worst.Select(static l => l.Line.Id));
    }

    [Fact]
    public void Markdown_ucieka_kreski_i_nowe_wiersze_i_wymienia_pominietych()
    {
        var markdown = EvalReport.ToMarkdown(
            [SampleRun(), EvalProviderRun.Skip("azure", null, "brak zmiennej środowiskowej GTO_AZURE_KEY.")],
            new EvalReportInfo("en-pl.sample.jsonl", 3, "2026-09-30 12:00 +02:00", WorstCount: 10));

        Assert.Contains("# Porównanie dostawców tłumaczeń", markdown);
        Assert.Contains("`en-pl.sample.jsonl` (3 linii)", markdown);
        Assert.Contains("- azure: brak zmiennej środowiskowej GTO_AZURE_KEY.", markdown);
        Assert.Contains("## DeepL (v2) — najgorsze linie", markdown);
        Assert.Contains(@"A \| B<br>C", markdown);
        Assert.Contains("Brak tłumaczenia: Brak połączenia", markdown);
    }

    [Fact]
    public void CSV_cytuje_pola_z_przecinkami_cudzyslowami_i_nowymi_wierszami()
    {
        var csv = EvalReport.ToCsv([SampleRun()]);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.StartsWith("provider,variant,id,scene,kind,origin,translated,latency_ms,chrf", header);
        Assert.Contains("DeepL,v2,ok,scene,dialog,Provider,true,120.0,100.00,1.100,,,,New Game,Nowa gra,Nowa gra", csv);
        Assert.Contains("\"X, \"\"Y\"\"\"", csv);
        Assert.Contains("\"A | B\nC\"", csv);
        Assert.Contains("DeepL,v2,fail,scene,dialog,Unavailable,false,,0.00,,,,Brak połączenia,Continue,Kontynuuj,", csv);
        Assert.Equal("proste", EvalReport.Csv("proste"));
    }

    [Fact]
    public void Brak_uruchomionych_dostawcow_jest_opisany()
    {
        var markdown = EvalReport.ToMarkdown([EvalProviderRun.Skip("deepl", null, "brak klucza")],
            new EvalReportInfo("x.jsonl", 0, "teraz"));

        Assert.Contains("Żaden dostawca nie został uruchomiony.", markdown);
    }
}
