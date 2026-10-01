using System.Diagnostics;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Evaluation;

/// <summary>Wynik jednej linii korpusu dla jednego dostawcy.</summary>
public sealed record EvalLineResult(
    EvalLine Line,
    string? Hypothesis,
    TranslationOrigin Origin,
    string? Error,
    double? LatencyMs,
    double ChrF,
    TranslationCheckResult? Checks)
{
    public bool IsTranslated => Hypothesis is not null;
}

/// <summary>Przebieg korpusu dla jednego dostawcy (albo informacja, dlaczego go pominięto).</summary>
public sealed record EvalProviderRun(
    string Provider,
    string? Variant,
    IReadOnlyList<EvalLineResult> Lines,
    double CorpusChrF,
    LatencySummary? Latency,
    string? SkipReason = null,
    double? FirstRequestMs = null)
{
    public bool Skipped => SkipReason is not null;

    public static EvalProviderRun Skip(string provider, string? variant, string reason) =>
        new(provider, variant, [], 0, null, reason);

    public int TranslatedCount => Lines.Count(static l => l.IsTranslated);
    public int FailedCount => Lines.Count(static l => !l.IsTranslated);
    public int LinesWithIssues => Lines.Count(static l => l.Checks is { Passed: false });

    public int IssueCount(TranslationCheckKind kind) =>
        Lines.Count(l => l.Checks?.Issues.Any(i => i.Kind == kind) == true);
}

public sealed class EvalRunnerOptions
{
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "pl";
    public string? Variant { get; init; }

    /// <summary>Nazwa gry przekazywana dostawcom kontekstowym, jak z profilu w aplikacji.</summary>
    public string? GameName { get; init; }

    /// <summary>Dodatkowe terminy (np. z pliku słownika) — obok pól „terms” z korpusu.</summary>
    public IReadOnlyList<GlossaryTerm> ExtraTerms { get; init; } = [];
}

/// <summary>
/// Odtwarza korpus przez ten sam <see cref="TranslationPipeline"/>, którego używa aplikacja
/// (słownik → cache → dostawca, sklejanie wierszy, kontekst ostatnich linii), z cache
/// wyłącznie w pamięci — ewaluacja niczego nie zapisuje w bazie tłumaczeń gracza.
/// Każda linia to osobne zapytanie, jak kolejne napisy w grze, więc czas odpowiedzi
/// jest mierzony per linia. Pierwsze zapytanie do dostawcy jest raportowane osobno
/// (<see cref="EvalProviderRun.FirstRequestMs"/>) i nie wchodzi do mediany ani p90: niesie
/// koszt „zimnego startu” — nawiązanie połączenia TLS, a w DeepL także listowanie, tworzenie
/// i usuwanie glosariusza — który przy małym korpusie albo --limit przesuwałby p90, a nawet
/// medianę, i krzywdził dostawców używających słownika po stronie serwera.
/// </summary>
public static class EvalRunner
{
    public static async Task<EvalProviderRun> RunAsync(
        ITranslationProvider provider,
        IReadOnlyList<EvalLine> lines,
        EvalRunnerOptions? options = null,
        IProgress<EvalLineResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvalRunnerOptions();

        var glossary = new GlossaryService();
        foreach (var term in lines.SelectMany(static l => l.Terms).Concat(options.ExtraTerms))
        {
            glossary.AddTerm(term);
        }

        var pipeline = new TranslationPipeline(
            glossary,
            new InMemoryTranslationCache(),
            provider,
            new UsageTracker(),
            new TranslationPipelineOptions { GameName = options.GameName });

        var providerLatencies = new List<double>();
        var results = new List<EvalLineResult>(lines.Count);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            TranslationOutcome outcome;
            try
            {
                var outcomes = await pipeline.TranslateAsync([line.Source], options.SourceLanguage, options.TargetLanguage, cancellationToken)
                    .ConfigureAwait(false);
                outcome = outcomes[0];
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Nieoczekiwany wyjątek jednej linii nie przerywa porównania pozostałych.
                outcome = new TranslationOutcome(line.Source, line.Source, null, TranslationOrigin.Unavailable,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            // Czas liczymy tylko dla zapytań do dostawcy — trafienie w słownik czy cache
            // (powtórzona linia) zaniżałoby medianę.
            double? lineLatency = null;
            if (outcome.Origin == TranslationOrigin.Provider && outcome.IsTranslated)
            {
                providerLatencies.Add(elapsed);
                lineLatency = elapsed;
            }

            TranslationCheckResult? checks = null;
            if (outcome.TranslatedText is { } hypothesis)
            {
                checks = TranslationChecks.Run(new TranslationCheckInput(
                    line.Source, hypothesis, line.Reference, line.ExpectGender,
                    glossary.FindTermsIn([line.Source], maxTerms: 100)));
            }

            var result = new EvalLineResult(
                line,
                outcome.TranslatedText,
                outcome.Origin,
                outcome.IsTranslated ? null : outcome.ErrorMessage,
                lineLatency,
                ChrF.Score(outcome.TranslatedText ?? string.Empty, line.Reference),
                checks);
            results.Add(result);
            progress?.Report(result);
        }

        // Linie bez tłumaczenia liczą się jak pusta hipoteza — dostawca, który zawodzi,
        // nie może mieć lepszego wyniku korpusu tylko dlatego, że pominięto jego błędy.
        var corpus = ChrF.CorpusScore(results.Select(static r => ((string?)r.Hypothesis, (string?)r.Line.Reference)));

        // Okno równe liczbie linii: mediana i p90 z całego przebiegu, nie z ostatnich 200.
        // Pierwszy pomiar (zimny start) pomijamy, chyba że jest jedynym — wtedy lepszy on niż brak.
        var latency = new LatencyMonitor(Math.Max(1, providerLatencies.Count));
        foreach (var ms in providerLatencies.Count > 1 ? providerLatencies.Skip(1) : providerLatencies)
        {
            latency.Record(LatencyStage.Provider, ms);
        }
        double? firstRequest = providerLatencies.Count > 0 ? providerLatencies[0] : null;

        return new EvalProviderRun(provider.Name, options.Variant, results, corpus, latency.Summarize(LatencyStage.Provider),
            FirstRequestMs: firstRequest);
    }
}
