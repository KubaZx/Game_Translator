using System.Globalization;

internal static class Program
{
    private const string Help = """
        MotionLab — stanowisko pomiarowe trybu live w ruchu (narzędzie dev, tylko lokalnie, bez sieci).

        MotionLab record "fragment tytułu okna" --out KATALOG [--seconds 30] [--fps 10] [--quality 92]
          Nagrywa okno gry przez PrintWindow (jak aplikacja) do JPEG + frames.jsonl.

        MotionLab truth NAGRANIE [--every 1] [--workers 4] [--force] [--corpus PLIK] [--profile escape-academy]
          Offline Windows OCR każdej klatki (ramki odkręcone o TextAngle) → NAGRANIE\truth.jsonl (gotowy plik jest używany ponownie).

        MotionLab replay NAGRANIE --out KATALOG [--speed 1] [--provider-delay-ms 500] [--cache KOPIA_cache.db]
                 [--corpus PLIK] [--profile escape-academy] [--placement cover] [--live at-source] [--render-every 1]
                 [--composite-scale 0.5] [--opacity 0.4] [--font-size 0] [--font-family auto] [--tail-ms 2000]
                 [--max-frames 0] [--lookahead 10] [--show-overlay] [--no-analyze] [--keep-work] [--no-angle-probe]
          Odtwarza klatki w oknie WPF w czasie z frames.jsonl, na nim prawdziwa LiveTranslationSession i OverlayWindow;
          zapisuje updates.jsonl, shown.jsonl, ocr.jsonl, warstwy nakładki, łatki i kompozyty, potem analyze.

        MotionLab analyze KATALOG_REPLAY [--truth PLIK] [--worst 15]
          Metryki ruchu → metrics.json + RAPORT.md + sheets\*.jpg.

        NAGRANIE = ścieżka katalogu albo nazwa w katalogu nagrań (domyślnie GTO Diagnostics\20261006-ruch\nagrania).
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine(Help);
            return 0;
        }
        try
        {
            switch (args[0])
            {
                case "record":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak fragmentu tytułu.");
                    var options = Options(args[2..], []);
                    return RecordCommand.Run(args[1], Required(options, "--out"),
                        Number(options, "--seconds", 30), Number(options, "--fps", 10), (long)Number(options, "--quality", 92));
                }
                case "truth":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak nagrania.");
                    var options = Options(args[2..], ["--force"]);
                    return TruthCommand.Run(args[1], new TruthOptions(
                        (int)Number(options, "--every", 1, 1, 100000),
                        (int)Number(options, "--workers", 4, 1, 16),
                        options.ContainsKey("--force"),
                        ExistingFile(options, "--corpus", LabDefaults.Corpus)!,
                        Text(options, "--profile", LabDefaults.Profile)));
                }
                case "replay":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak nagrania.");
                    var options = Options(args[2..], ["--show-overlay", "--no-analyze", "--keep-work", "--no-angle-probe"]);
                    var cache = options.TryGetValue("--cache", out var cacheValue) && cacheValue.Equals("none", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : ExistingFile(options, "--cache", LabDefaults.Cache);
                    return ReplayCommand.Run(args[1], new ReplayOptions(
                        Path.GetFullPath(Required(options, "--out")),
                        Number(options, "--speed", 1, 0.1, 4),
                        (int)Number(options, "--provider-delay-ms", 500, 0, 10000),
                        cache,
                        ExistingFile(options, "--corpus", LabDefaults.Corpus)!,
                        Text(options, "--profile", LabDefaults.Profile),
                        OneOf(options, "--placement", "cover", "cover", "below"),
                        OneOf(options, "--live", "at-source", "at-source", "subtitle"),
                        Number(options, "--opacity", 0.4, 0, 1),
                        Number(options, "--font-size", 0, 0, 200),
                        Text(options, "--font-family", "auto"),
                        (int)Number(options, "--render-every", 1, 1, 1000),
                        Number(options, "--composite-scale", 0.5, 0.1, 1),
                        options.ContainsKey("--show-overlay"),
                        !options.ContainsKey("--no-analyze"),
                        (int)Number(options, "--tail-ms", 2000, 0, 30000),
                        (int)Number(options, "--max-frames", 0, 0, 1_000_000),
                        options.ContainsKey("--keep-work"),
                        !options.ContainsKey("--no-angle-probe"),
                        (int)Number(options, "--lookahead", 10, 2, 60)));
                }
                case "analyze":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak katalogu replay.");
                    var options = Options(args[2..], []);
                    return AnalyzeCommand.Run(Path.GetFullPath(args[1]), new AnalyzeOptions(
                        options.TryGetValue("--truth", out var truth) ? Path.GetFullPath(truth) : null,
                        (int)Number(options, "--worst", 15, 1, 100)));
                }
                default:
                    throw new ArgumentException("Nieznane polecenie.");
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message + "\n" + Help);
            return 2;
        }
    }

    private static Dictionary<string, string> Options(string[] args, IReadOnlyCollection<string> flags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Nieoczekiwany argument {args[i]}.");
            if (flags.Contains(args[i]))
            {
                result[args[i]] = "true";
                continue;
            }
            if (i + 1 >= args.Length) throw new ArgumentException($"Brak wartości {args[i]}.");
            result[args[i]] = args[++i];
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Wymagane {name}.");

    private static string Text(Dictionary<string, string> options, string name, string fallback) =>
        options.TryGetValue(name, out var value) ? value : fallback;

    private static string? ExistingFile(Dictionary<string, string> options, string name, string fallback)
    {
        var path = Path.GetFullPath(options.TryGetValue(name, out var value) ? value : fallback);
        return File.Exists(path) ? path : throw new ArgumentException($"{name}: nie ma pliku {path}");
    }

    private static string OneOf(Dictionary<string, string> options, string name, string fallback, params string[] allowed)
    {
        var value = Text(options, name, fallback);
        return allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{name}: dozwolone {string.Join(", ", allowed)}.");
    }

    private static double Number(Dictionary<string, string> options, string name, double fallback,
        double min = double.MinValue, double max = double.MaxValue)
    {
        if (!options.TryGetValue(name, out var value)) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new ArgumentException($"{name}: oczekiwano liczby z zakresu {min.ToString(CultureInfo.InvariantCulture)}–{max.ToString(CultureInfo.InvariantCulture)}, jest „{value}”.");
        return number;
    }
}
