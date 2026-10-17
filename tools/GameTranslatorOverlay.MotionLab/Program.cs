using System.Globalization;

internal static class Program
{
    private const string Help = """
        MotionLab — stanowisko pomiarowe trybu live w ruchu (narzędzie dev, tylko lokalnie, bez sieci).

        MotionLab record "fragment tytułu okna" --out KATALOG [--seconds 30] [--fps 10] [--quality 92]
          Nagrywa okno gry przez PrintWindow (jak aplikacja) do JPEG + frames.jsonl.

        MotionLab truth NAGRANIE [--every 1] [--workers 4] [--force] [--plain] [--corpus PLIK] [--profile escape-academy]
          Offline Windows OCR każdej klatki (ramki odkręcone o TextAngle), imię mówiącego osobnym blokiem jak w aplikacji
          → NAGRANIE\truth-split.jsonl; --plain: bez dzielenia, jak do rundy 10 → NAGRANIE\truth.jsonl (gotowy plik jest używany ponownie).

        MotionLab replay NAGRANIE --out KATALOG [--speed 1] [--provider-delay-ms 500] [--cache KOPIA_cache.db]
                 [--corpus PLIK] [--profile escape-academy] [--placement cover] [--live at-source] [--render-every 1] [--capture auto|wgc|gdi]
                 [--composite-scale 0.5] [--opacity 0.4] [--font-size 0] [--font-family auto] [--tail-ms 2000] [--phase-ms 0]
                 [--max-frames 0] [--lookahead 10] [--show-overlay] [--no-analyze] [--keep-work] [--no-angle-probe] [--no-lost-read]
          Odtwarza klatki w oknie WPF w czasie z frames.jsonl, na nim prawdziwa LiveTranslationSession i OverlayWindow;
          zapisuje updates.jsonl, shown.jsonl, ocr.jsonl, warstwy nakładki, łatki i kompozyty, potem analyze.
          --phase-ms N: sesja startuje N ms po pierwszej klatce (faza harmonogramu sesji względem klatek nagrania).

        MotionLab analyze KATALOG_REPLAY [--truth PLIK] [--plain-truth] [--worst 15]
          Metryki ruchu → metrics.json + RAPORT.md + sheets\*.jpg (--plain-truth: prawda bez dzielenia imion, porównywalna z rundami do 10).

        MotionLab series NAGRANIE --out KATALOG [--runs 5] [--phases auto|0,120,240] [--period-ms 600] [--reanalyze]
                 [--truth PLIK] [--plain-truth] [--timeout-min 0] [pozostałe opcje replay]
          N przebiegów replay KOLEJNO (osobne procesy), każdy z inną fazą startu sesji, w podkatalogach runNN-fazaXXXms;
          gotowe przebiegi są używane ponownie (--reanalyze liczy ich metryki od nowa); wynik: series.json + series.md.

        MotionLab compare SERIA_A SERIA_B [--out PLIK.md] [--alpha 0.05] [--all]
          Metryki serii B względem A: mediany, różnica, dokładny test Manna–Whitneya, skupiska opóźnień → .md + .json.

        MotionLab lost NAGRANIE --out KATALOG [--placement cover|below] [--ocr-every 9] [--max-frames 0] [--periodic-every 3]
                 [--corpus PLIK] [--profile escape-academy]
          Offline, deterministycznie: zgubienia śladu w ruchu i lokalny odczyt OCR wycinka wobec prawdy → events.jsonl, RAPORT.md.

        NAGRANIE = ścieżka katalogu albo nazwa w katalogu nagrań (<GTO Diagnostics>\20261006-ruch\nagrania).
        <GTO Diagnostics> = zmienna środowiskowa GTO_DIAGNOSTICS albo folder „GTO Diagnostics” obok repozytorium;
        z niego biorą się też domyślne --cache i --corpus.
        """;

    private static readonly string[] ReplayFlags = ["--show-overlay", "--no-analyze", "--keep-work", "--no-angle-probe", "--no-lost-read"];

    private static readonly string[] SeriesOwnOptions = ["--out", "--runs", "--phases", "--period-ms", "--reanalyze", "--truth", "--plain-truth", "--timeout-min"];

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
                    var options = Options(args[2..], ["--force", "--plain"]);
                    return TruthCommand.Run(args[1], new TruthOptions(
                        (int)Number(options, "--every", 1, 1, 100000),
                        (int)Number(options, "--workers", 4, 1, 16),
                        options.ContainsKey("--force"),
                        ExistingFile(options, "--corpus", LabDefaults.Corpus)!,
                        Text(options, "--profile", LabDefaults.Profile),
                        !options.ContainsKey("--plain")));
                }
                case "replay":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak nagrania.");
                    return ReplayCommand.Run(args[1], ReplayOptionsOf(Options(args[2..], ReplayFlags)));
                }
                case "lost":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak nagrania.");
                    var options = Options(args[2..], []);
                    return LostCommand.Run(args[1], new LostOptions(
                        Path.GetFullPath(Required(options, "--out")),
                        OneOf(options, "--placement", "cover", "cover", "below"),
                        (int)Number(options, "--ocr-every", 9, 1, 1000),
                        (int)Number(options, "--max-frames", 0, 0, 1_000_000),
                        ExistingFile(options, "--corpus", LabDefaults.Corpus)!,
                        Text(options, "--profile", LabDefaults.Profile),
                        (int)Number(options, "--periodic-every", 3, 1, 1000)));
                }
                case "analyze":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak katalogu replay.");
                    var options = Options(args[2..], ["--plain-truth"]);
                    return AnalyzeCommand.Run(Path.GetFullPath(args[1]), new AnalyzeOptions(
                        options.TryGetValue("--truth", out var truth) ? Path.GetFullPath(truth) : null,
                        (int)Number(options, "--worst", 15, 1, 100),
                        options.ContainsKey("--plain-truth")));
                }
                case "series":
                {
                    if (args.Length < 2) throw new ArgumentException("Brak nagrania.");
                    return SeriesCommand.Run(args[1], SeriesOptionsOf(args[2..]));
                }
                case "compare":
                {
                    if (args.Length < 3) throw new ArgumentException("Podaj dwie serie: SERIA_A SERIA_B.");
                    var options = Options(args[3..], ["--all"]);
                    return CompareCommand.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]),
                        options.TryGetValue("--out", out var output) ? Path.GetFullPath(output) : null,
                        Number(options, "--alpha", 0.05, 0.0001, 0.5),
                        options.ContainsKey("--all"));
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

    private static ReplayOptions ReplayOptionsOf(Dictionary<string, string> options)
    {
        var cache = options.TryGetValue("--cache", out var cacheValue) && cacheValue.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : ExistingFile(options, "--cache", LabDefaults.Cache);
        return new ReplayOptions(
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
            (int)Number(options, "--lookahead", 10, 2, 60),
            OneOf(options, "--capture", "auto", "auto", "wgc", "gdi"),
            (int)Number(options, "--phase-ms", 0, 0, 60000),
            !options.ContainsKey("--no-lost-read"));
    }

    private static SeriesOptions SeriesOptionsOf(string[] args)
    {
        var own = new List<string>();
        var passthrough = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Nieoczekiwany argument {name}.");
            var target = SeriesOwnOptions.Contains(name) ? own : passthrough;
            target.Add(name);
            if (name is "--reanalyze" or "--plain-truth" || ReplayFlags.Contains(name)) continue;
            if (i + 1 >= args.Length) throw new ArgumentException($"Brak wartości {name}.");
            target.Add(args[++i]);
        }
        foreach (var forbidden in new[] { "--phase-ms", "--no-analyze" })
            if (passthrough.Contains(forbidden)) throw new ArgumentException($"series: {forbidden} ustawia sama seria.");
        passthrough = CanonicalReplayArgs(passthrough);
        var options = Options([.. own], ["--reanalyze", "--plain-truth"]);
        var replay = Options([.. passthrough], ReplayFlags);
        replay["--out"] = Path.Combine(Path.GetTempPath(), "motionlab-series-check");
        _ = ReplayOptionsOf(replay);

        IReadOnlyList<int>? phases = null;
        var phaseText = Text(options, "--phases", "auto");
        if (!phaseText.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            phases = phaseText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 60000
                    ? value
                    : throw new ArgumentException($"--phases: oczekiwano listy liczb 0–60000 ms, jest „{p}”."))
                .ToList();
            if (phases.Count == 0) throw new ArgumentException("--phases: pusta lista.");
            if (phases.Distinct().Count() != phases.Count) throw new ArgumentException("--phases: fazy muszą być różne.");
        }
        var runs = (int)Number(options, "--runs", phases?.Count ?? 5, 1, 30);
        if (phases is not null && options.ContainsKey("--runs") && runs != phases.Count)
            throw new ArgumentException($"--runs {runs} nie zgadza się z liczbą faz w --phases ({phases.Count}).");
        return new SeriesOptions(
            Path.GetFullPath(Required(options, "--out")),
            phases?.Count ?? runs,
            phases,
            (int)Number(options, "--period-ms", 600, 1, 60000),
            options.ContainsKey("--reanalyze"),
            options.TryGetValue("--truth", out var truth) ? Path.GetFullPath(truth) : null,
            Number(options, "--timeout-min", 0, 0, 600),
            passthrough,
            options.ContainsKey("--plain-truth"));
    }

    private static List<string> CanonicalReplayArgs(List<string> args)
    {
        var pairs = new List<(string Name, string? Value)>();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            pairs.Add(ReplayFlags.Contains(name) ? (name, null) : (name, args[++i]));
        }
        return [.. pairs.OrderBy(static p => p.Name, StringComparer.Ordinal)
            .SelectMany(static p => p.Value is null ? new[] { p.Name } : new[] { p.Name, p.Value })];
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
        var given = options.TryGetValue(name, out var value);
        var path = Path.GetFullPath(given ? value! : fallback);
        if (File.Exists(path)) return path;
        throw new ArgumentException(given
            ? $"{name}: nie ma pliku {path}"
            : $"{name}: nie ma domyślnego pliku {path} — podaj {name} albo ustaw GTO_DIAGNOSTICS.");
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
