using System.Text;
using GameTranslatorOverlay.CorpusEval;

internal static class Program
{
    private const string Help = """
        CorpusEval — eksperyment precyzji przyciągania odczytów OCR do korpusu gry (narzędzie dev).
          render   --corpus KORPUS.jsonl --backgrounds PNG[;PNG...] --out PRYWATNY.jsonl [--count 440] [--seed 1] [--examples KATALOG]
                   Renderuje losowe teksty korpusu (biały tekst, czarny obrys, 32–72 px) na wycinkach teł
                   i przepuszcza przez Windows OCR. Wynik zawiera teksty gry: tylko do folderu prywatnego.
          evaluate --corpus KORPUS.jsonl --ocr PRYWATNY.jsonl --cache KOPIA_cache.db --out KATALOG_LICZB --private KATALOG_PRYWATNY
                   Przegląd progów: prawda syntetyczna, prawdziwy cache (dni EA) i kontrola negatywna (dzień PoE2).
          bench    --corpus KORPUS.jsonl --ocr PRYWATNY.jsonl --cache KOPIA_cache.db --out KATALOG_LICZB [--synthetic 150000]
                   Czas dopasowania p50/p95 dla korpusu gry i korpusu syntetycznego.
          replay   --corpus KORPUS.jsonl --cache KOPIA_cache.db --prefilled BAZA_Z_KORPUSEM.db --work KATALOG_PRYWATNY --out KATALOG_LICZB [--profile escape-academy]
                   Powtórka bloków z cache przez TranslationPipeline (Mock): bez korpusu, z kluczami po akapitach,
                   z przyciąganiem do korpusu na pustym cache i na bazie wypełnionej przez CorpusTool translate.
        Do katalogu liczb trafiają wyłącznie liczby, nigdy teksty gry.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine(Help);
            return 0;
        }
        var options = EvalArgs.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "render" => RenderCommand.RunAsync(options).GetAwaiter().GetResult(),
                "evaluate" => EvaluateCommand.Run(options),
                "bench" => BenchCommand.Run(options),
                "replay" => ReplayCommand.Run(options),
                _ => Fail($"Nieznane polecenie: {args[0]}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

namespace GameTranslatorOverlay.CorpusEval
{
    internal sealed class EvalArgs(Dictionary<string, string> values)
    {
        public static EvalArgs Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Oczekiwano opcji, jest: {args[i]}");
                values[args[i][2..]] = args[i + 1];
            }
            return new EvalArgs(values);
        }

        public string Required(string name) =>
            values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Brak opcji --{name}.");

        public string? Optional(string name) => values.GetValueOrDefault(name);

        public int Int(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value) : fallback;
    }
}
