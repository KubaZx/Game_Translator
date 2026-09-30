using System.Globalization;
using System.Text;
using GameTranslatorOverlay.Core.Evaluation;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.ProviderEval;

internal static class Program
{
    private const string Help = """
        ProviderEval — porównanie dostawców tłumaczeń na korpusie EN→PL (chrF + kontrole).
          --corpus PLIK        Korpus JSONL; domyślnie eval/en-pl.sample.jsonl.
          --providers LISTA    Po przecinku: mock, deepl, azure, google, claude, llm; domyślnie mock.
          --out KATALOG        Gdzie zapisać report.md i results.csv; domyślnie eval/out.
          --limit N            Tylko pierwsze N linii (w kolejności scen).
          --variant ETYKIETA   Opis wariantu w raporcie, np. „prompt-v2”.
          --glossary PLIK      Dodatkowy słownik JSON (format glossaries/*.json).
          --game NAZWA         Nazwa gry przekazywana dostawcom kontekstowym (LLM, Claude).
          --no-deepl-glossary  DeepL bez glosariusza (nic nie powstaje na koncie DeepL).
          --help               Tylko pomoc.
        Klucze wyłącznie ze zmiennych środowiskowych: GTO_DEEPL_KEY, GTO_AZURE_KEY (+ GTO_AZURE_REGION),
        GTO_GOOGLE_KEY, ANTHROPIC_API_KEY (+ GTO_CLAUDE_MODEL), GTO_LLM_ENDPOINT + GTO_LLM_MODEL (+ GTO_LLM_KEY).
        Dostawca bez klucza jest pomijany. Do dostawców trafiają wyłącznie linie korpusu — nigdy zrzuty ekranu.
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ProviderEvalOptions options;
        try { options = ProviderEvalOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message + "\nUżyj --help."); return 2; }
        if (options.Help) { Console.WriteLine(Help); return 0; }

        EvalCorpus corpus;
        var extraTerms = new List<GlossaryTerm>();
        try
        {
            corpus = EvalCorpus.Parse(await File.ReadAllTextAsync(options.CorpusPath));
            if (options.GlossaryPath is not null)
            {
                var document = GlossarySerializer.FromJson(await File.ReadAllTextAsync(options.GlossaryPath));
                var errors = GlossaryValidator.Validate(document);
                if (errors.Count > 0) throw new FormatException(string.Join(Environment.NewLine, errors));
                extraTerms.AddRange(document.Terms);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Nie udało się wczytać korpusu albo słownika: {ex.Message}");
            return 1;
        }

        var lines = corpus.InReplayOrder(options.Limit);
        Console.WriteLine($"Korpus: {Path.GetFileName(options.CorpusPath)} — {lines.Count} z {corpus.Lines.Count} linii.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var httpClient = ProviderHttpClientFactory.Create();
        var ownedClients = new List<HttpClient>();
        var runs = new List<EvalProviderRun>();
        try
        {
            foreach (var id in options.Providers)
            {
                var (provider, skipReason) = ProviderFactory.Create(id, httpClient, () =>
                {
                    var client = ProviderHttpClientFactory.Create();
                    ownedClients.Add(client);
                    return client;
                }, options.DeepLGlossary);

                if (provider is null)
                {
                    Console.WriteLine($"Pomijam {id}: {skipReason}");
                    runs.Add(EvalProviderRun.Skip(id, options.Variant, skipReason ?? "niedostępny."));
                    continue;
                }

                Console.Write($"{provider.Name}: ");
                var done = 0;
                var progress = new InlineProgress(() =>
                {
                    if (++done % 10 == 0) Console.Write('.');
                });
                var run = await EvalRunner.RunAsync(provider, lines, new EvalRunnerOptions
                {
                    Variant = options.Variant,
                    GameName = options.GameName,
                    ExtraTerms = extraTerms,
                }, progress, cts.Token);
                runs.Add(run);
                Console.WriteLine($" chrF {EvalReport.FormatScore(run.CorpusChrF)}, przetłumaczono {run.TranslatedCount}/{run.Lines.Count}.");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Przerwano.");
            return 130;
        }
        finally
        {
            foreach (var client in ownedClients) client.Dispose();
        }

        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
            var info = new EvalReportInfo(
                Path.GetFileName(options.CorpusPath), lines.Count,
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
            var reportPath = Path.Combine(options.OutputDirectory, "report.md");
            var csvPath = Path.Combine(options.OutputDirectory, "results.csv");
            await File.WriteAllTextAsync(reportPath, EvalReport.ToMarkdown(runs, info), new UTF8Encoding(false));
            // BOM — Excel inaczej otwiera polskie znaki w CSV jako krzaki.
            await File.WriteAllTextAsync(csvPath, EvalReport.ToCsv(runs), new UTF8Encoding(true));

            Console.WriteLine();
            if (runs.Any(static r => !r.Skipped)) Console.Write(EvalReport.SummaryTable(runs));
            Console.WriteLine();
            Console.WriteLine($"Raport: {reportPath}");
            Console.WriteLine($"CSV:    {csvPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nie udało się zapisać raportu: {ex.Message}");
            return 1;
        }

        if (runs.All(static r => r.Skipped))
        {
            Console.Error.WriteLine("Żaden dostawca nie został uruchomiony (brak kluczy w zmiennych środowiskowych?).");
            return 3;
        }
        return 0;
    }
}

/// <summary>
/// Postęp wywoływany synchronicznie w pętli runnera — <see cref="Progress{T}"/> bez kontekstu
/// synchronizacji wysyłałby kropki z puli wątków w dowolnej kolejności.
/// </summary>
internal sealed class InlineProgress(Action onReport) : IProgress<EvalLineResult>
{
    public void Report(EvalLineResult value) => onReport();
}
