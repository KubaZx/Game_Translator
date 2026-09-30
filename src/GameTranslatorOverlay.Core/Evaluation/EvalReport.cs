using System.Globalization;
using System.Text;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Evaluation;

/// <summary>Opis przebiegu w nagłówku raportu (bez kluczy, bez ścieżek użytkownika).</summary>
public sealed record EvalReportInfo(string CorpusName, int CorpusLines, string GeneratedAt, int WorstCount = 10);

/// <summary>
/// Raport porównania dostawców: report.md do czytania (tabela zbiorcza + najgorsze linie)
/// i results.csv do własnych analiz. Czysta funkcja nad wynikami — testowalna bez sieci.
/// Liczby w formacie niezależnym od języka systemu (kropka dziesiętna), żeby CSV otwierał
/// się tak samo wszędzie.
/// </summary>
public static class EvalReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly (TranslationCheckKind Kind, string Label)[] CheckLabels =
    [
        (TranslationCheckKind.Numbers, "Liczby"),
        (TranslationCheckKind.FormalAddress, "Pan/Pani"),
        (TranslationCheckKind.Gender, "Rodzaj"),
        (TranslationCheckKind.Glossary, "Słownik"),
        (TranslationCheckKind.Paragraphs, "Wiersze"),
        (TranslationCheckKind.Length, "Długość"),
    ];

    public static string FormatScore(double score) => score.ToString("0.0", Invariant);

    public static string FormatLatency(LatencySummary? summary, bool p90) =>
        summary is null ? "—" : FormatMilliseconds(p90 ? summary.P90Ms : summary.MedianMs);

    private static string FormatMilliseconds(double ms) =>
        ms < 1000 ? $"{Math.Round(ms).ToString("0", Invariant)} ms" : $"{(ms / 1000).ToString("0.00", Invariant)} s";

    /// <summary>Tabela zbiorcza do wklejenia w konsoli albo w opisie PR.</summary>
    public static string SummaryTable(IReadOnlyList<EvalProviderRun> runs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Dostawca | Wariant | Linie | chrF korpusu | Mediana | p90 | Błędy dostawcy | Linie z uwagami | "
                           + string.Join(" | ", CheckLabels.Select(static c => c.Label)) + " |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|" + string.Concat(CheckLabels.Select(static _ => "---:|")));
        foreach (var run in runs.Where(static r => !r.Skipped))
        {
            builder.Append("| ").Append(Cell(run.Provider))
                .Append(" | ").Append(Cell(run.Variant ?? "—"))
                .Append(" | ").Append(run.TranslatedCount.ToString(Invariant)).Append('/').Append(run.Lines.Count.ToString(Invariant))
                .Append(" | ").Append(FormatScore(run.CorpusChrF))
                .Append(" | ").Append(FormatLatency(run.Latency, p90: false))
                .Append(" | ").Append(FormatLatency(run.Latency, p90: true))
                .Append(" | ").Append(run.FailedCount.ToString(Invariant))
                .Append(" | ").Append(run.LinesWithIssues.ToString(Invariant));
            foreach (var (kind, _) in CheckLabels)
            {
                builder.Append(" | ").Append(run.IssueCount(kind).ToString(Invariant));
            }
            builder.AppendLine(" |");
        }
        return builder.ToString();
    }

    /// <summary>
    /// Najgorsze linie: najpierw nieprzetłumaczone, potem najniższy chrF; przy remisie więcej
    /// uwag kontroli, a na końcu id — kolejność deterministyczna, raporty dają się porównać diffem.
    /// </summary>
    public static IReadOnlyList<EvalLineResult> WorstLines(EvalProviderRun run, int count) =>
        run.Lines
            .OrderBy(static l => l.IsTranslated)
            .ThenBy(static l => l.ChrF)
            .ThenByDescending(static l => l.Checks?.Issues.Count ?? 0)
            .ThenBy(static l => l.Line.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, count))
            .ToList();

    public static string ToMarkdown(IReadOnlyList<EvalProviderRun> runs, EvalReportInfo info)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Porównanie dostawców tłumaczeń");
        builder.AppendLine();
        builder.AppendLine($"- Korpus: `{info.CorpusName}` ({info.CorpusLines.ToString(Invariant)} linii)");
        builder.AppendLine($"- Wygenerowano: {info.GeneratedAt}");
        builder.AppendLine("- chrF: 0–100, n-gramy znakowe 1–6, beta = 2 (jak sacreBLEU). Jedna referencja — " +
                           "wynik służy do porównań względnych na tym samym korpusie, nie jako ocena bezwzględna.");
        builder.AppendLine("- Czas: jedno zapytanie do dostawcy na linię (bez trafień w słownik i cache).");
        builder.AppendLine();
        builder.AppendLine("## Podsumowanie");
        builder.AppendLine();
        if (runs.Any(static r => !r.Skipped)) builder.Append(SummaryTable(runs));
        else builder.AppendLine("Żaden dostawca nie został uruchomiony.");

        var skipped = runs.Where(static r => r.Skipped).ToList();
        if (skipped.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Pominięci dostawcy:");
            builder.AppendLine();
            foreach (var run in skipped)
            {
                builder.AppendLine($"- {run.Provider}: {run.SkipReason}");
            }
        }

        foreach (var run in runs.Where(static r => !r.Skipped))
        {
            var worst = WorstLines(run, info.WorstCount);
            builder.AppendLine();
            builder.AppendLine($"## {run.Provider}{(run.Variant is null ? string.Empty : $" ({run.Variant})")} — najgorsze linie");
            builder.AppendLine();
            if (worst.Count == 0)
            {
                builder.AppendLine("Brak linii.");
                continue;
            }
            builder.AppendLine("| Id | chrF | Oryginał | Referencja | Tłumaczenie | Uwagi |");
            builder.AppendLine("|---|---:|---|---|---|---|");
            foreach (var line in worst)
            {
                var notes = line.IsTranslated
                    ? string.Join(" ", line.Checks?.Issues.Select(static i => i.Message) ?? [])
                    : "Brak tłumaczenia: " + (line.Error ?? "nieznany błąd");
                builder.Append("| ").Append(Cell(line.Line.Id))
                    .Append(" | ").Append(FormatScore(line.ChrF))
                    .Append(" | ").Append(Cell(line.Line.Source))
                    .Append(" | ").Append(Cell(line.Line.Reference))
                    .Append(" | ").Append(Cell(line.Hypothesis ?? "—"))
                    .Append(" | ").Append(Cell(notes.Length == 0 ? "—" : notes))
                    .AppendLine(" |");
            }
        }
        return builder.ToString();
    }

    /// <summary>Komórka tabeli Markdown: „|” rozbiłby kolumny, a nowy wiersz — całą tabelę.</summary>
    public static string Cell(string text) =>
        text.Replace("\r\n", "\n").Replace("|", "\\|").Replace("\n", "<br>");

    public static string ToCsv(IReadOnlyList<EvalProviderRun> runs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("provider,variant,id,scene,kind,origin,translated,latency_ms,chrf,length_ratio,issue_kinds,issues,error,source,reference,hypothesis");
        foreach (var run in runs.Where(static r => !r.Skipped))
        {
            foreach (var line in run.Lines)
            {
                var fields = new[]
                {
                    run.Provider,
                    run.Variant ?? string.Empty,
                    line.Line.Id,
                    line.Line.Scene,
                    line.Line.Kind,
                    line.Origin.ToString(),
                    line.IsTranslated ? "true" : "false",
                    line.LatencyMs is { } ms ? ms.ToString("0.0", Invariant) : string.Empty,
                    line.ChrF.ToString("0.00", Invariant),
                    line.Checks is { } c ? c.LengthRatio.ToString("0.000", Invariant) : string.Empty,
                    string.Join(";", line.Checks?.Issues.Select(static i => i.Kind.ToString()).Distinct() ?? []),
                    string.Join(" ", line.Checks?.Issues.Select(static i => i.Message) ?? []),
                    line.Error ?? string.Empty,
                    line.Line.Source,
                    line.Line.Reference,
                    line.Hypothesis ?? string.Empty,
                };
                builder.AppendLine(string.Join(",", fields.Select(Csv)));
            }
        }
        return builder.ToString();
    }

    /// <summary>Pole CSV wg RFC 4180: cudzysłów, gdy zawiera przecinek, cudzysłów albo nowy wiersz.</summary>
    public static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
